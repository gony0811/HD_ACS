using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HD.Acs.UI.Models;
using HD.Acs.UI.Primitives;
using HD.Acs.UI.Rendering;
using HD.Acs.UI.Services;

namespace HD.Acs.UI.ViewModels;

/// <summary>
/// 좌측 화물창 뷰(3D + 전개도) 데이터. 벽면 코드/좌표계는 TankLayout(전개도·3D 공유 기준)에서 온다.
/// 3D 셸은 지오메트리 API(GetWallsAsync)의 실제 10면을 소비하며, 선택 층 도달 밴드를 강조한다.
/// 벽면별 완료율 집계 API는 아직 없어 로봇 현재 위치만 실데이터로 오버레이한다.
/// </summary>
public sealed partial class TankViewModel : ObservableObject
{
    private readonly IAcsApiClient _api;

    public ObservableCollection<TankFloor> Floors { get; } = new(TankLayout.Floors);
    public ObservableCollection<WallCode> Walls { get; } = new(TankLayout.Walls);

    /// <summary>3D 셸 = 지오메트리 API의 실제 10면(도면 3D 프레임).</summary>
    public ObservableCollection<WallDto> ShellWalls { get; } = new();
    /// <summary>선택 층에서 도달 가능한 면 + reachableVBand (층 z-밴드 강조용).</summary>
    public ObservableCollection<WallDto> LevelWalls { get; } = new();

    /// <summary>영역+소속 작업(용접선) 묶음 — 3D 오버레이용.</summary>
    public sealed record AreaOverlay(AreaDto Area, IReadOnlyList<AreaTaskDto> Tasks);

    /// <summary>등록된 영역·작업 (3D 도면 오버레이). 뷰 모드로 필터해 렌더.</summary>
    public ObservableCollection<AreaOverlay> Overlays { get; } = new();

    // ── 실행 큐(work_item)·용접라인(액션) 상태색 — 운영 중 진행 표시 [INSPECTION_SCENARIO §3.1] ──
    private readonly Dictionary<Guid, string> _workStatusByArea = new();
    private readonly Dictionary<Guid, string> _workStatusByTask = new();

    /// <summary>현재 run의 영역별 work_item + 용접라인별 액션 상태 반영(초기 로드·푸시 갱신 시 Shell이 호출).
    /// null/빈=계획 보기(영역=기본 녹색, 용접선=주황).</summary>
    public void ApplyWorkItemStatuses(
        IReadOnlyDictionary<Guid, string>? statusByArea,
        IReadOnlyDictionary<Guid, string>? statusByTask = null)
    {
        _workStatusByArea.Clear();
        if (statusByArea is not null)
            foreach (var (areaId, status) in statusByArea) _workStatusByArea[areaId] = status;
        _workStatusByTask.Clear();
        if (statusByTask is not null)
            foreach (var (taskId, status) in statusByTask) _workStatusByTask[taskId] = status;
        BuildFacePlots();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>영역의 현재 work_item 상태 (없으면 null = 계획 보기).</summary>
    public string? WorkItemStatusOf(Guid areaId) =>
        _workStatusByArea.TryGetValue(areaId, out var s) ? s : null;

    /// <summary>용접라인의 현재 액션 상태 (없으면 null = 계획 보기, 기본 주황).</summary>
    public string? TaskStatusOf(Guid taskId) =>
        _workStatusByTask.TryGetValue(taskId, out var s) ? s : null;

    /// <summary>상태 → (채움, 외곽선) 색 — 전개도(브러시 컨버터)·3D(material)·상태 배지가 공유하는 단일 매핑.
    /// 영역(work_item): null=계획(녹) / PENDING=회 / DISPATCHED=파랑 / DONE=녹(진) / SKIPPED·FAILED=빨강.
    /// 용접라인(액션): PLANNED·WAITING=회 / RUNNING=파랑 / FINISHED=녹(진) / FAILED=빨강.</summary>
    public static (Rgba Fill, Rgba Line) StatusColors(string? status) => status switch
    {
        "PENDING" or "PLANNED" or "WAITING" =>
            (Rgba.FromArgb(0x26, 0x80, 0x8A, 0x94), Rgba.FromRgb(0x80, 0x8A, 0x94)),
        "DISPATCHED" or "RUNNING" =>
            (Rgba.FromArgb(0x38, 0x2E, 0x86, 0xDE), Rgba.FromRgb(0x2E, 0x86, 0xDE)),
        "DONE" or "FINISHED" =>
            (Rgba.FromArgb(0x40, 0x27, 0xAE, 0x60), Rgba.FromRgb(0x2E, 0xCC, 0x71)),
        "FAILED" or "SKIPPED" =>
            (Rgba.FromArgb(0x38, 0xE7, 0x4C, 0x3C), Rgba.FromRgb(0xE7, 0x4C, 0x3C)),
        _ => (Rgba.FromArgb(0x26, 0x27, 0xAE, 0x60), Rgba.FromRgb(0x22, 0x99, 0x54)),
    };

    /// <summary>용접선 외곽 색 — 상태 없으면 계획 기본 주황(#E67E22), 있으면 상태색.</summary>
    public static Rgba WeldLineColor(string? status) =>
        status is null ? Rgba.FromRgb(0xE6, 0x7E, 0x22) : StatusColors(status).Line;

    /// <summary>전개도 탭 — 면별 2D 도면(실제 비율 형상 + 영역·작업 오버레이, 이미 캔버스 px로 투영).</summary>
    public sealed record FacePlot(string Code, string Dim, IReadOnlyList<Pt2> Outline,
        IReadOnlyList<AreaPoly> Areas, IReadOnlyList<TaskSeg> Tasks);
    public ObservableCollection<FacePlot> FacePlots { get; } = new();

    // 전개도 셀(면당) 렌더 크기(px)
    private const double CellW = 240, CellH = 150, CellMargin = 14;

    /// <summary>3D 뷰 모드: "전체" + 층 목록(L1~L4). 전체=개관, L{n}=그 층 슬라이스 격리.</summary>
    public ObservableCollection<string> ViewModes { get; } =
        new(new[] { AllMode }.Concat(TankLayout.Floors.Select(f => f.Level)));

    private const string AllMode = "전체";

    /// <summary>셸/강조를 다시 빌드해야 함(데이터 로드·뷰 모드 변경). 뷰가 구독.</summary>
    public event EventHandler? ViewChanged;

    [ObservableProperty] private string _tankId = TankLayout.DefaultTankId;
    [ObservableProperty] private string _selectedViewMode = AllMode;
    [ObservableProperty] private bool _showOverlays = true;   // 영역·작업 3D 표시 토글
    [ObservableProperty] private TankFloor? _selectedFloor;

    /// <summary>선택 뷰 모드의 층 번호(1-based). "전체"면 null.</summary>
    public int? SelectedLevel =>
        SelectedViewMode is { } m && m != AllMode &&
        int.TryParse(m.TrimStart('L', 'l'), NumberStyles.Integer, CultureInfo.InvariantCulture, out int lv)
            ? lv : null;

    /// <summary>층 슬라이스 격리 모드인지(전체가 아니면 true) — 뷰가 채움 면을 생략하고 슬라이스를 강조.</summary>
    public bool IsolateLevel => SelectedLevel is not null;
    [ObservableProperty] private double? _robotX;
    [ObservableProperty] private double? _robotY;
    [ObservableProperty] private string? _robotMapId;
    /// <summary>로봇 heading — 맵 프레임(rad, VDA agvPosition.theta). null=미보고.</summary>
    [ObservableProperty] private double? _robotTheta;

    // ── 수동 이동 (층 격리 뷰 바닥 그리드 클릭 → goto Order) — 이동 테스트용 ──
    /// <summary>켜면 층 바닥 그리드 클릭이 해당 지점으로 수동 이동을 명령한다 (오조작 방지 토글).</summary>
    [ObservableProperty] private bool _manualMoveMode;
    [ObservableProperty] private string? _moveStatus;
    private string? _lastRobotId;   // 최근 state 보고 로봇 — 단일 로봇 MVP

    /// <summary>바닥 그리드 클릭 지점(도면 x,y)으로 이동 명령. 뷰 코드비하인드가 호출.</summary>
    public async Task RequestMoveAsync(double xDrawing, double yDrawing)
    {
        if (SelectedLevel is not int level) return;
        if (_lastRobotId is not { } robotId) { MoveStatus = "로봇 보고 없음 — 연결 확인"; return; }
        try
        {
            await _api.GotoAsync(robotId, level, xDrawing, yDrawing);
            MoveStatus = $"이동 명령: ({xDrawing:F2}, {yDrawing:F2}) → {robotId}";
        }
        catch (Exception ex)
        {
            MoveStatus = $"이동 실패: {ex.Message}";
        }
    }

    public bool HasRobotPosition => RobotX is not null && RobotY is not null;

    /// <summary>선택 뷰 층에 로봇이 있는지 — 다른 층이면 마커를 흐리게. "전체"면 항상 표시(비교 안 함).</summary>
    public bool RobotOnSelectedFloor =>
        SelectedLevel is null ||
        string.Equals(RobotMapId, $"{TankId}-L{SelectedLevel}", StringComparison.OrdinalIgnoreCase);

    public TankViewModel(IAcsApiClient api, IMonitoringClient monitoring)
    {
        _api = api;
        monitoring.RobotStateReceived += OnRobotState;
    }

    /// <summary>선창 파라미터(팔각 치수·유도값). 마구리(F/A) 팔각 윤곽 렌더에 사용.</summary>
    public TankGeometryDto? Geometry { get; private set; }

    /// <summary>선창 면 전체를 불러와 3D 셸을 구성하고, 현재 뷰 모드 강조를 로드한다.</summary>
    public async Task LoadAsync()
    {
        _calByMapId.Clear(); _calMissing.Clear();   // 프로젝트/캘리브레이션 변경 대비 T_W_D 캐시 무효화
        try
        {
            Geometry = await _api.GetTankGeometryAsync(TankId);   // 마구리 팔각 윤곽용 치수
            var walls = await _api.GetWallsAsync(TankId);
            ShellWalls.Clear();
            foreach (var w in walls) ShellWalls.Add(w);
            await LoadOverlaysAsync();     // 영역·작업 로드 후 ViewChanged
            await LoadLevelWallsAsync();   // ViewChanged 발생(셸+강조 재빌드)
        }
        catch
        {
            // 서버 미기동/면 미생성 — 빈 셸 유지(무해).
            Geometry = null;
            ShellWalls.Clear();
            LevelWalls.Clear();
            Overlays.Clear();
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>등록된 영역·작업을 불러와 3D 오버레이를 재구성한다. 2D 탭 변경 시 외부에서 재호출.</summary>
    public async Task LoadOverlaysAsync()
    {
        Overlays.Clear();
        try
        {
            var areas = await _api.GetAreasAsync(TankId);
            foreach (var a in areas)
            {
                var tasks = await _api.GetAreaTasksAsync(a.AreaId);
                Overlays.Add(new AreaOverlay(a, tasks));
            }
        }
        catch { /* 조회 실패는 무해 — 오버레이 없이 셸만 */ }
        BuildFacePlots();   // 전개도(면별 2D 도면) 갱신
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>전개도: 각 면(ShellWalls)을 실제 비율 2D 도형(사각형/마구리 팔각)으로, 영역·작업 오버레이 포함해 셀에 투영.</summary>
    private void BuildFacePlots()
    {
        FacePlots.Clear();
        foreach (var w in ShellWalls)
        {
            if (w.ULen <= 0 || w.VLen <= 0) continue;
            double scale = Math.Min((CellW - 2 * CellMargin) / w.ULen, (CellH - 2 * CellMargin) / w.VLen);
            (double x, double y) Proj(double u, double v) => (CellMargin + u * scale, CellMargin + (w.VLen - v) * scale);
            Pt2[] PC(IEnumerable<double[]> uv) => Plot2D.ProjectCorners(uv, Proj);

            var outline = PC(FaceOutlineUv(w));

            var areas = new List<AreaPoly>();
            var tasks = new List<TaskSeg>();
            if (ShowOverlays)
            {
                foreach (var ov in Overlays.Where(o => o.Area.WallCode == w.WallCode))
                {
                    var corners = ov.Area.Corners ?? RectUv(ov.Area.UMin, ov.Area.VMin, ov.Area.UMax, ov.Area.VMax);
                    var pc = PC(corners);
                    var (cx, cy) = Plot2D.Centroid(pc);
                    areas.Add(new AreaPoly(pc, cx, cy, ov.Area.Name, WorkItemStatusOf(ov.Area.AreaId)));
                    foreach (var t in ov.Tasks)
                    {
                        var (x1, y1) = Proj(t.StartU, t.StartV);
                        var (x2, y2) = Proj(t.EndU, t.EndV);
                        tasks.Add(new TaskSeg(x1, y1, x2, y2, x2 - 4, y2 - 4, (x1 + x2) / 2, (y1 + y2) / 2, t.Seq.ToString(),
                            TaskStatusOf(t.TaskId)));
                    }
                }
            }

            FacePlots.Add(new FacePlot(w.WallCode, $"{w.ULen:0.#} × {w.VLen:0.#} m", outline, areas, tasks));
        }
    }

    private static double[][] RectUv(double uMin, double vMin, double uMax, double vMax) => new[]
    {
        new[] { uMin, vMin }, new[] { uMax, vMin }, new[] { uMax, vMax }, new[] { uMin, vMax },
    };

    /// <summary>면 경계 (u,v) 정점. 마구리(F/A)+Geometry 로드 시 팔각, 그 외 사각형.</summary>
    private IReadOnlyList<double[]> FaceOutlineUv(WallDto w)
    {
        if (w.WallCode is "F" or "A" && Geometry is { } g)
        {
            double b = g.Derived.B, wf = g.WFloor, wc = g.Derived.WCeil, hl = g.HLow, zw = g.HLow + g.HWall, h = g.Derived.H;
            return new[]
            {
                new[] { b / 2 - wf / 2, 0 }, new[] { 0.0, hl }, new[] { 0.0, zw }, new[] { b / 2 - wc / 2, h },
                new[] { b / 2 + wc / 2, h }, new[] { b, zw }, new[] { b, hl }, new[] { b / 2 + wf / 2, 0.0 },
            };
        }
        return RectUv(0, 0, w.ULen, w.VLen);
    }

    /// <summary>선택 뷰 모드가 층(L{n})이면 그 층 도달 면+reachableVBand 로드, "전체"면 clear. 후 ViewChanged.</summary>
    private async Task LoadLevelWallsAsync()
    {
        LevelWalls.Clear();
        if (ShellWalls.Count > 0 && SelectedLevel is int level)
        {
            try
            {
                foreach (var w in await _api.GetWallsAsync(TankId, level)) LevelWalls.Add(w);
            }
            catch { /* 강조 실패는 무해 — 와이어만 표시 */ }
        }
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── 로봇 마커 좌표 변환: 맵 → 도면 (T_W_D 역변환) ────────────────────────
    // 로봇 보고는 맵 좌표, 3D 씬은 도면 좌표 — 역변환 없이 찍으면 T_W_D(tx,ty,yaw)만큼 어긋나 보인다.
    private readonly Dictionary<string, (double Tx, double Ty, double Yaw)> _calByMapId = new();
    private readonly HashSet<string> _calMissing = new();   // 미보정 층 — 재조회 폭주 방지

    /// <summary>마커용 도면 좌표. 캘리브레이션 미보정 층은 원시(맵) 좌표 폴백.</summary>
    public double RobotDrawingX { get; private set; }
    public double RobotDrawingY { get; private set; }
    /// <summary>로봇 층의 주행 평면 z (level_z) — 마커를 그 층 높이에 표시.</summary>
    public double RobotDrawingZ { get; private set; }
    /// <summary>도면 프레임 heading(rad, x축 기준 CCW). 위치와 같은 규칙으로 T_W_D yaw를 뺀 값(미보정 층은 원시 theta). null=theta 미보고 → 화살표 생략.</summary>
    public double? RobotDrawingTheta { get; private set; }

    /// <summary>맵 heading → 도면 heading. 위치가 drawing = R(−yaw)·(map − t)이므로 방향은 theta − yaw. 결과는 (−π, π]로 정규화.</summary>
    public static double MapThetaToDrawing(double thetaMap, double calYaw)
    {
        double t = thetaMap - calYaw;
        t = Math.IEEERemainder(t, 2 * Math.PI);   // (−π, π]
        if (t <= -Math.PI) t += 2 * Math.PI;
        return t;
    }

    private void OnRobotState(object? sender, RobotStateDto s)
    {
        _lastRobotId = s.RobotId;
        RobotX = s.ReportedX;
        RobotY = s.ReportedY;
        RobotTheta = s.ReportedTheta;
        RobotMapId = s.ReportedMapId;
        UpdateRobotDrawingPose();
        OnPropertyChanged(nameof(HasRobotPosition));
        OnPropertyChanged(nameof(RobotOnSelectedFloor));
    }

    private void UpdateRobotDrawingPose()
    {
        double mx = RobotX ?? 0, my = RobotY ?? 0;
        double dx = mx, dy = my;   // 폴백: 미보정 → 원시 좌표
        double? dTheta = RobotTheta;   // 폴백: 미보정 → 원시 heading
        if (RobotMapId is { } mapId)
        {
            if (_calByMapId.TryGetValue(mapId, out var c))
            {
                // drawing = R(−yaw) · (map − t)
                double px = mx - c.Tx, py = my - c.Ty;
                double cos = Math.Cos(-c.Yaw), sin = Math.Sin(-c.Yaw);
                dx = cos * px - sin * py;
                dy = sin * px + cos * py;
                if (RobotTheta is double th) dTheta = MapThetaToDrawing(th, c.Yaw);
            }
            else if (!_calMissing.Contains(mapId))
            {
                _ = LoadCalibrationAsync(mapId);   // 최초 1회 비동기 로드 — 도착 시 재계산
            }

            // 층 z: mapId "{tank}-L{n}" → level_z[n-1]
            var idx = mapId.LastIndexOf("-L", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0 && int.TryParse(mapId[(idx + 2)..], out int lv)
                && Geometry?.LevelZ is { } lz && lv - 1 >= 0 && lv - 1 < lz.Length)
                RobotDrawingZ = lz[lv - 1];
            else
                RobotDrawingZ = 0;
        }
        RobotDrawingX = dx;
        RobotDrawingY = dy;
        RobotDrawingTheta = dTheta;
        OnPropertyChanged(nameof(RobotDrawingX));
        OnPropertyChanged(nameof(RobotDrawingY));
        OnPropertyChanged(nameof(RobotDrawingTheta));
    }

    private async Task LoadCalibrationAsync(string mapId)
    {
        try
        {
            var cal = await _api.GetCalibrationAsync(mapId);
            if (cal is not null) _calByMapId[mapId] = (cal.Tx, cal.Ty, cal.YawRad);
            else _calMissing.Add(mapId);   // 미보정 층 — 원시 좌표 폴백 유지
        }
        catch { _calMissing.Add(mapId); }
        UpdateRobotDrawingPose();
    }

    partial void OnSelectedViewModeChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedLevel));
        OnPropertyChanged(nameof(IsolateLevel));
        OnPropertyChanged(nameof(RobotOnSelectedFloor));
        _ = LoadLevelWallsAsync();   // 뷰 모드 변경 시 슬라이스 갱신
    }

    partial void OnShowOverlaysChanged(bool value) { BuildFacePlots(); ViewChanged?.Invoke(this, EventArgs.Empty); }
}
