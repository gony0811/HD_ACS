using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.Acs.UI.Models;
using HD.Acs.UI.Primitives;
using HD.Acs.UI.Rendering;
using HD.Acs.UI.Services;
using Microsoft.Extensions.Options;

namespace HD.Acs.UI.ViewModels;

// 캔버스 렌더 레코드(AreaPoly/TaskSeg/StationMarker/LevelOption)는 PlotShapes.cs — TankViewModel과 공유.

/// <summary>
/// 선창 3D 정의(§2/§3) + 영역·검사 작업 (u,v) 등록(§4) [SPEC v3].
/// 파라미터 등록 → 면 자동생성 → 면 선택 → (u,v) 영역/작업 등록.
/// </summary>
public sealed partial class AreaPlanningViewModel : ObservableObject
{
    private readonly IAcsApiClient _api;
    private readonly string _operatorId;

    public const double CanvasSize = 600;
    private const double Margin = 28;

    public AreaPlanningViewModel(IAcsApiClient api, IOptions<AcsOptions> options)
    {
        _api = api;
        _operatorId = options.Value.OperatorId;
        Assistant = new PlanningAssistantViewModel(api, _operatorId, this);
    }

    /// <summary>계획 자연어 어시스턴트 패널 [ADR-013] — 이 화면의 면·층 선택을 공유하고 변경안을 전개도에 미리 그린다.</summary>
    public PlanningAssistantViewModel Assistant { get; }

    // 어시스턴트 변경안 미리보기(면-전체 v) — 선택 면에 해당하는 것만 그린다.
    private IReadOnlyList<PlanChangeOpDto>? _proposal;
    public ObservableCollection<AreaPoly> ProposalAreas { get; } = new();     // 생성·수정 후 모습(청록 점선)
    public ObservableCollection<AreaPoly> ProposalRemovals { get; } = new();  // 삭제 대상(빨강 점선)
    public ObservableCollection<TaskSeg> ProposalSegments { get; } = new();   // 생성·수정 용접선

    /// <summary>변경안 미리보기 지정(null=지움) → 전개도 재투영.</summary>
    public void SetProposal(IReadOnlyList<PlanChangeOpDto>? ops)
    {
        _proposal = ops;
        Project();
    }

    public ObservableCollection<WallDto> Walls { get; } = new();
    public ObservableCollection<LevelOption> Levels { get; } = new();          // 층 필터 목록(level_z 기반)
    public ObservableCollection<AreaDto> Areas { get; } = new();              // 그리드용 — 선택 층, 층-로컬 v
    private readonly List<AreaDto> _allAreas = new();                          // 전개도용 — 그 면 모든 층, 면-전체 v
    public ObservableCollection<AreaTaskDto> AreaTasks { get; } = new();
    public ObservableCollection<AreaPoly> AreaBoxes { get; } = new();          // 선택 층 영역(활성·녹색) — 4점 폴리곤
    public ObservableCollection<AreaPoly> InactiveAreaBoxes { get; } = new();  // 타 층 영역(회색·비활성)
    public ObservableCollection<AreaPoly> DraftAreas { get; } = new();          // 입력 중 영역 미리보기(점선)
    public ObservableCollection<TaskSeg> TaskSegments { get; } = new();
    public ObservableCollection<TaskSeg> DraftSegments { get; } = new();       // 입력 중 용접선 미리보기
    public ObservableCollection<StationMarker> StationMarkers { get; } = new(); // 정차점 = 영역 중심
    public ObservableCollection<TaskSeg> CrossPreview { get; } = new();         // CROSS3/4 그리기 미리보기(중심→가지)

    [ObservableProperty] private string _tankId = "CT1";
    [ObservableProperty] private string? _statusMessage;

    // ── 선창 파라미터 (각도 deg) ──
    [ObservableProperty] private double _lengthL = 30.0;
    [ObservableProperty] private double _wFloor = 10.0;
    [ObservableProperty] private double _thetaLowDeg = 45.0;
    [ObservableProperty] private double _hLow = 3.0;
    [ObservableProperty] private double _hWall = 8.0;
    [ObservableProperty] private double _thetaUpDeg = 45.0;
    [ObservableProperty] private double _hUp = 2.0;
    [ObservableProperty] private string _levelZText = "0, 3.2, 6.4, 9.6";
    [ObservableProperty] private string _reachZMinText = "";      // 선택 — 코봇 도달 밴드 하한
    [ObservableProperty] private string _reachZMaxText = "";      // 선택 — 코봇 도달 밴드 상한
    [ObservableProperty] private double _originOx;
    [ObservableProperty] private double _originOy;
    [ObservableProperty] private string _derivedText = "-";

    // 선택 면 경계 폴리곤(캔버스 px) — 면 전체(회색 음영). 마구리(F/A)는 팔각, 그 외 직사각형.
    [ObservableProperty] private IReadOnlyList<Pt2> _faceOutline = Array.Empty<Pt2>();
    // 선택 층 활성 밴드 폴리곤(캔버스 px) — 면 전체 위에 활성(밝게) 오버레이.
    [ObservableProperty] private IReadOnlyList<Pt2> _activeBand = Array.Empty<Pt2>();
    private double _derB, _derWCeil, _derH;   // 파생값(전폭·천장폭·전체높이) — 마구리 팔각 윤곽용

    // ── 층 필터 (v3.1 §9 — 선택 층에서 도달 가능한 면만 노출) ──
    [ObservableProperty] private LevelOption? _selectedLevel;

    // ── 영역 등록 입력 (면 로컬 u,v, 임의 4점 사각형). level은 서버가 유도 → 입력 없음 ──
    [ObservableProperty] private WallDto? _selectedWall;
    [ObservableProperty] private AreaDto? _selectedArea;
    /// <summary>작업 목록에서 선택한 작업 — 선택 시 입력 폼에 값이 채워지고 "선택 작업 수정"이 활성화된다(taskId 유지 수정).</summary>
    [ObservableProperty] private AreaTaskDto? _selectedTask;
    [ObservableProperty] private string _areaName = "A01";
    // 코너 P1~P4 (면-로컬 u,v). 기본=작은 사각형. 캔버스 클릭으로도 순서대로 지정.
    [ObservableProperty] private double _c1U; [ObservableProperty] private double _c1V;
    [ObservableProperty] private double _c2U = 2.0; [ObservableProperty] private double _c2V;
    [ObservableProperty] private double _c3U = 2.0; [ObservableProperty] private double _c3V = 2.0;
    [ObservableProperty] private double _c4U; [ObservableProperty] private double _c4V = 2.0;
    [ObservableProperty] private int _cornerIndex;   // 다음 클릭이 지정할 코너(0~3)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanvasPickActive))]
    private bool _pickMode;     // 도면에서 4점 선택(픽) 모드 — 켜면 캔버스 커서=크로스헤어
    [ObservableProperty] private bool _stationOverride;
    [ObservableProperty] private double _stationX;
    [ObservableProperty] private double _stationY;
    [ObservableProperty] private double _stationTheta;
    // 정차 이격 [m] — 정차점 = 영역 중심 + 내부향 법선 수평성분 × 이격. null(빈값)=서버 설정 기본(0.8m)
    [ObservableProperty] private double? _stationStandoffM;

    // ── 작업 등록 입력 (면 로컬 u,v) ──
    [ObservableProperty] private double _startU;
    [ObservableProperty] private double _startV;
    [ObservableProperty] private double _endU = 1.0;
    [ObservableProperty] private double _endV;

    // 용접라인 형태(seamType) — 도면에서 추출한 형태를 운영자가 지정. VDA §8.2/§8.5.1 카탈로그와 1:1(canonical 8종, 2026-10-07 개정).
    //   LINE=직선 · CROSS3_R0/R90/R180/R270=T자 3갈래 회전 4종(면 자세 무관) · CROSS4=4갈래 十자 · CORNER2=2면 코너 · CORNER3=3면 코너.
    // CROSS3*/CROSS4/CORNER* 는 HD_AMR 실행 게이트 OFF([협의 N13])라 실제 검사는 미동작 — 계획 데이터로 저장·전달만.
    // ⚠️ CROSS3/4 는 시작·끝점 2점만으로 교차 형상을 정의할 수 없다 — 교차 중심+가지 입력은 후속(제안: 데이터 모델 points 확장).
    public IReadOnlyList<string> SeamTypes { get; } =
        new[] { "LINE", "CROSS3_R0", "CROSS3_R90", "CROSS3_R180", "CROSS3_R270", "CROSS4", "CORNER2", "CORNER3" };
    [ObservableProperty] private string _selectedSeamType = "LINE";

    // ── CROSS3/4 교차 그리기 [VDA §8.5.1, N13] — 중심+가지 클릭, 회전은 서버가 자동 유도·표시 ──
    //   CROSS4 = 중심+가지4(5클릭), CROSS3 = 중심+줄기+통과2(4클릭, 줄기=회전 결정).
    //   회전 유도·AMR 점 정렬은 서버(CrossGeometry) 정본 — UI는 도메인 Core 미참조라 /cross-preview 호출.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanvasPickActive))]
    [NotifyPropertyChangedFor(nameof(SeamTypeEditable))]
    private string? _crossDrawShape;            // null=비활성 / "CROSS3" / "CROSS4"
    [ObservableProperty] private int _crossStep;        // 0=중심, 1..N=가지
    [ObservableProperty] private string _crossStepText = "";
    [ObservableProperty] private string _seamRotationText = "";
    private (double U, double V)? _crossCenter;
    private readonly List<(double U, double V)> _crossArms = new();   // 층-로컬 v
    private double[][]? _crossPointsForRegister;    // 가지(층-로컬 v) — 등록 시 +VOff
    private int _crossSeq;   // 비동기 프리뷰 경합 토큰

    private int CrossArmCount => CrossDrawShape == "CROSS4" ? 4 : 3;
    public bool CanvasPickActive => PickMode || CrossDrawShape is not null;   // 캔버스 크로스헤어 커서
    public bool SeamTypeEditable => CrossDrawShape is null;                   // 그리기 중 seamType 콤보 잠금

    // 선택 층 로컬 v 오프셋 — (0,0)=그 층 도달 구간 좌하단. VM은 로컬 v로 동작, API 경계에서 ±VOff.
    private double VOff => SelectedWall?.ReachableVBand is { Length: 2 } b ? b[0] : 0;
    private double SliceH => SelectedWall?.ReachableVBand is { Length: 2 } b ? b[1] - b[0] : SelectedWall?.VLen ?? 0;

    public string SelectedWallInfo => SelectedWall is { } w
        ? $"면 {w.WallCode}{(SelectedLevel is { } l ? $" · {l.Label} 로컬" : "")} — u∈[0,{w.ULen:0.###}], v∈[0,{SliceH:0.###}] (좌하단 0,0)"
          + (w.FacingYaw is null ? " (바닥/천장: 정차 수동지정 필요)" : "")
        : "면 선택";

    [RelayCommand]
    public async Task LoadAsync()
    {
        try
        {
            var g = await _api.GetTankGeometryAsync(TankId);
            if (g is not null) ApplyGeometry(g);
            await LoadWallsAsync();
            await RefreshAreasAsync();
        }
        catch (Exception ex) { StatusMessage = $"조회 실패: {ex.Message}"; }
    }

    /// <summary>선택 층 필터로 면 목록 재적재 [v3.1 §8/§9]. 층 미선택 시 전체 면.</summary>
    private async Task LoadWallsAsync()
    {
        var list = await _api.GetWallsAsync(TankId, SelectedLevel?.Level);
        Walls.Clear();
        foreach (var w in list) Walls.Add(w);
        // 면 재생성/필터 후 인스턴스가 바뀌므로 코드로 재바인딩(콤보 stale 방지)
        SelectedWall = Walls.FirstOrDefault(x => x.WallCode == SelectedWall?.WallCode) ?? Walls.FirstOrDefault();
    }

    [RelayCommand]
    private async Task RegisterGeometryAsync() => await TryRegisterGeometryAsync();

    /// <summary>선창 파라미터 등록(면 자동생성) 후 재로드. 성공 여부 반환 — 새 프로젝트 팝업이 결과를 사용.</summary>
    public async Task<bool> TryRegisterGeometryAsync()
    {
        double[] levelZ;
        try { levelZ = ParseLevelZ(LevelZText); }
        catch { StatusMessage = "level_z 형식 오류 — 쉼표로 구분된 숫자 목록 (예: 0, 3.2, 6.4)."; return false; }
        double? reachMin = ParseOptional(ReachZMinText), reachMax = ParseOptional(ReachZMaxText);
        try
        {
            int n = await _api.RegisterTankGeometryAsync(TankId, LengthL, WFloor, ThetaLowDeg, HLow,
                HWall, ThetaUpDeg, HUp, levelZ, OriginOx, OriginOy, _operatorId, reachMin, reachMax);
            StatusMessage = $"선창 파라미터 등록: {TankId} → {n}면 자동생성";
            await LoadAsync();
            return true;
        }
        catch (Exception ex) { StatusMessage = $"등록 실패: {ex.Message}"; return false; }
    }

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync();

    private bool CanRegisterArea() => SelectedWall is not null;

    /// <summary>입력 코너 P1~P4 (면-로컬 u,v, v는 층-로컬).</summary>
    private double[][] InputCorners() => new[]
    {
        new[] { C1U, C1V }, new[] { C2U, C2V }, new[] { C3U, C3V }, new[] { C4U, C4V },
    };

    [RelayCommand(CanExecute = nameof(CanRegisterArea))]
    private async Task RegisterAreaAsync()
    {
        if (SelectedWall is not { } w) return;
        if (InputCornersFaceGlobal() is not { } corners) return;
        try
        {
            var (areaId, level) = await _api.CreateAreaAsync(TankId, w.WallCode, AreaName, corners,
                StationOverride ? StationX : null, StationOverride ? StationY : null, StationOverride ? StationTheta : null,
                _operatorId, StationStandoffM);
            StatusMessage = $"영역 등록: {w.WallCode}/{AreaName} (4점) → 유도 층 L{level}";
            await RefreshAreasAsync(select: areaId);   // 새 영역을 선택 → 바로 작업 등록·수정 가능
        }
        catch (Exception ex) { StatusMessage = $"영역 등록 실패: {ex.Message}"; }   // 면범위·층유도 400·중복 409
    }

    /// <summary>
    /// 입력 코너(층-로컬 v) 사전 검증 후 면-전체 v로 환산해 반환. 위반 시 StatusMessage를 채우고 null.
    /// 서버가 최종 판정하지만, 흔한 실수는 왕복 전에 알린다(등록·수정 공통).
    /// </summary>
    private double[][]? InputCornersFaceGlobal()
    {
        var local = InputCorners();
        var (miu, miv, mau, mav) = AreaBboxLocal(local);
        if (mau - miu < 1e-6 || mav - miv < 1e-6) { StatusMessage = "영역이 퇴화했습니다 — 유효한 사각형 4점을 입력하세요."; return null; }
        if (mau - miu > 1.44 + 1e-9 || mav - miv > 1.44 + 1e-9) { StatusMessage = "AREA 최대 크기는 u/v 각 1.44m(1440mm)입니다."; return null; }
        if (mav > SliceH + 1e-6 || miv < -1e-6) { StatusMessage = $"코너 v가 선택 층 구간(0~{SliceH:0.###})을 벗어났습니다."; return null; }
        double off = VOff;   // 층-로컬 → 면-전체 v 변환 후 저장(각 코너 v)
        return local.Select(p => new[] { p[0], p[1] + off }).ToArray();
    }

    private bool CanUpdateArea() => SelectedArea is not null;

    /// <summary>
    /// 선택 영역 수정 — 삭제→재등록과 달리 **areaId와 소속 작업(taskId)이 유지**된다 [SAIGE v2.6 §2.5].
    /// 이름·4점 코너·정차 설정을 폼 값으로 교체한다. 면은 바꿀 수 없고, 층은 서버가 새 코너로 다시 유도한다.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUpdateArea))]
    private async Task UpdateAreaAsync()
    {
        if (SelectedArea is not { } area) return;
        if (InputCornersFaceGlobal() is not { } corners) return;
        try
        {
            int level = await _api.UpdateAreaAsync(area.AreaId, AreaName, corners,
                StationOverride ? StationX : null, StationOverride ? StationY : null, StationOverride ? StationTheta : null,
                StationStandoffM, _operatorId);
            StatusMessage = $"영역 수정: {area.WallCode}/{AreaName} → 층 L{level} — areaId·작업 유지"
                + (SelectedLevel is { } l && l.Level != level ? $" (층이 L{level}로 바뀌어 현재 {l.Label} 목록에서 빠집니다)" : "");
            await RefreshAreasAsync(select: area.AreaId);
        }
        catch (Exception ex) { StatusMessage = $"영역 수정 실패: {ex.Message}"; }   // 면범위·층유도·작업 이탈 400·이름 중복 409·없음 404
    }

    private static (double MinU, double MinV, double MaxU, double MaxV) AreaBboxLocal(double[][] pts)
    {
        double miu = double.MaxValue, miv = double.MaxValue, mau = double.MinValue, mav = double.MinValue;
        foreach (var p in pts)
        {
            if (p[0] < miu) miu = p[0]; if (p[0] > mau) mau = p[0];
            if (p[1] < miv) miv = p[1]; if (p[1] > mav) mav = p[1];
        }
        return (miu, miv, mau, mav);
    }

    [RelayCommand]
    private async Task DeleteAreaAsync(AreaDto? area)
    {
        if (area is null) return;
        try { await _api.DeleteAreaAsync(area.AreaId); StatusMessage = "영역 삭제됨."; await RefreshAreasAsync(); }
        catch (Exception ex) { StatusMessage = $"영역 삭제 실패: {ex.Message}"; }
    }

    private bool CanRegisterTask() => SelectedArea is not null;

    [RelayCommand(CanExecute = nameof(CanRegisterTask))]
    private async Task RegisterTaskAsync()
    {
        if (SelectedArea is not { } a) return;
        double off = VOff;   // 층-로컬 → 면-전체 v 변환 후 저장
        var pts = CrossPointsFaceGlobal(off);   // CROSS 그리기 버퍼가 있으면 가지 끝점(면-전체 v), 아니면 null
        try
        {
            int seq = await _api.CreateAreaTaskAsync(a.AreaId, StartU, StartV + off, EndU, EndV + off, SelectedSeamType, "DXF-1", "PROF-1", _operatorId, points: pts);
            StatusMessage = $"작업 등록: seq {seq} [{SelectedSeamType}] ({StartU},{StartV})–({EndU},{EndV})(로컬)";
            if (pts is not null) CancelCrossDraw();   // 교차 등록 완료 → 버퍼 비움
            await LoadTasksAndProjectAsync();
            await RefreshAreasAsync();
        }
        catch (Exception ex) { StatusMessage = $"작업 등록 실패: {ex.Message}"; }   // 경계 밖 400
    }

    private bool CanUpdateTask() => SelectedTask is not null;

    /// <summary>
    /// 선택 작업 수정 — 삭제→재등록과 달리 **taskId가 유지**된다 [SAIGE v2.6 §2.5]: 좌표를 고쳐도 같은 용접선의
    /// 검사 이력(SAIGE productId)·attempt 누적이 이어진다. seq·name은 건드리지 않는다(null=기존값 유지).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUpdateTask))]
    private async Task UpdateTaskAsync()
    {
        if (SelectedTask is not { } task) return;
        double off = VOff;   // 층-로컬 → 면-전체 v 변환 후 저장
        var pts = CrossPointsFaceGlobal(off);   // 재그리기 했으면 가지 교체, 아니면 null(기존 유지)
        try
        {
            await _api.UpdateAreaTaskAsync(task.TaskId, StartU, StartV + off, EndU, EndV + off, SelectedSeamType, _operatorId, points: pts);
            StatusMessage = $"작업 수정: seq {task.Seq} [{SelectedSeamType}] ({StartU},{StartV})–({EndU},{EndV})(로컬) — taskId 유지";
            await LoadTasksAndProjectAsync();
            await RefreshAreasAsync();
        }
        catch (Exception ex) { StatusMessage = $"작업 수정 실패: {ex.Message}"; }   // 경계 밖 400·없음 404
    }

    partial void OnSelectedTaskChanged(AreaTaskDto? value)
    {
        UpdateTaskCommand.NotifyCanExecuteChanged();
        // 작업 재선택 = 진행 중 교차 그리기 버퍼 비움(섞임 방지)
        CrossDrawShape = null; CrossStep = 0; CrossStepText = "";
        _crossCenter = null; _crossArms.Clear(); _crossPointsForRegister = null; CrossPreview.Clear();
        if (value is null) { SeamRotationText = ""; return; }
        // 선택 작업의 값(층-로컬 v — AreaTasks가 이미 −VOff 적용분)을 폼에 채운다 → 전개도 점선 미리보기도 그 선분으로 이동
        StartU = value.StartU; StartV = value.StartV; EndU = value.EndU; EndV = value.EndV;
        if (SeamTypes.Contains(value.SeamType)) SelectedSeamType = value.SeamType;
        // CROSS + 저장된 가지(points, 층-로컬 v) 백필 → 재편집 시 가지 복원, 재그리기 없이 수정하면 그대로 유지
        if (value.Points is { Length: > 0 } pts && value.SeamType.StartsWith("CROSS", StringComparison.Ordinal))
        {
            _crossCenter = (value.StartU, value.StartV);
            foreach (var p in pts) _crossArms.Add((p[0], p[1]));
            _crossPointsForRegister = _crossArms.Select(p => new[] { p.U, p.V }).ToArray();
            SeamRotationText = value.SeamType;
        }
        else SeamRotationText = "";
        Project();   // CrossPreview 갱신
    }

    [RelayCommand]
    private async Task DeleteTaskAsync(AreaTaskDto? task)
    {
        if (task is null) return;
        try { await _api.DeleteAreaTaskAsync(task.TaskId); StatusMessage = "작업 삭제됨."; await LoadTasksAndProjectAsync(); await RefreshAreasAsync(); }
        catch (Exception ex) { StatusMessage = $"작업 삭제 실패: {ex.Message}"; }
    }

    /// <summary>영역·작업이 등록/삭제되어 3D 오버레이 갱신이 필요함(등록/삭제가 모두 RefreshAreasAsync 경유).</summary>
    public event EventHandler? PlanningChanged;

    private async Task RefreshAreasAsync(Guid? select = null)
    {
        // 그 면의 **모든 층** 영역을 로드(_allAreas, 면-전체 v — 전개도가 타 층을 회색으로 표시).
        // 그리드 Areas = 선택 층만 + 층-로컬 v(−VOff) — 입력 규약과 일치.
        double off = VOff; int? sel = SelectedLevel?.Level;
        var list = SelectedWall is { } w
            ? await _api.GetAreasAsync(TankId, w.WallCode)
            : (IReadOnlyList<AreaDto>)Array.Empty<AreaDto>();
        _allAreas.Clear();
        _allAreas.AddRange(list);
        var keep = select ?? SelectedArea?.AreaId;   // 새로고침 후에도 같은 영역을 계속 선택(수정·작업 등록 연속)
        Areas.Clear();
        foreach (var a in list.Where(a => sel is null || a.Level == sel))
            Areas.Add(a with { VMin = a.VMin - off, VMax = a.VMax - off, Corners = OffsetCornersV(a.Corners, -off) });
        SelectedArea = keep is Guid id ? Areas.FirstOrDefault(a => a.AreaId == id) : null;
        await LoadTasksAndProjectAsync();
        PlanningChanged?.Invoke(this, EventArgs.Empty);   // 3D 오버레이 동기화
    }

    private async Task LoadTasksAndProjectAsync()
    {
        double off = VOff;   // 작업 v도 층-로컬(−VOff)로 변환
        var list = SelectedArea is { } a
            ? await _api.GetAreaTasksAsync(a.AreaId)
            : (IReadOnlyList<AreaTaskDto>)Array.Empty<AreaTaskDto>();
        var keep = SelectedTask?.TaskId;   // 수정·새로고침 후에도 같은 작업을 계속 선택(연속 미세 조정)
        AreaTasks.Clear();
        foreach (var t in list) AreaTasks.Add(t with { StartV = t.StartV - off, EndV = t.EndV - off, Points = OffsetCornersV(t.Points, -off) });
        SelectedTask = keep is Guid id ? AreaTasks.FirstOrDefault(t => t.TaskId == id) : null;
        Project();
    }

    partial void OnSelectedWallChanged(WallDto? value)
    {
        RegisterAreaCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SelectedWallInfo));
        _ = RefreshAreasAsync();
    }

    private Guid? _formAreaId;   // 폼에 값이 채워진 영역 — 같은 영역 재선택(새로고침)은 편집 중인 폼을 덮지 않는다

    partial void OnSelectedAreaChanged(AreaDto? value)
    {
        RegisterTaskCommand.NotifyCanExecuteChanged();
        UpdateAreaCommand.NotifyCanExecuteChanged();
        if (value is not null && value.AreaId != _formAreaId) FillAreaForm(value);
        _formAreaId = value?.AreaId;
        _ = LoadTasksAndProjectAsync();
    }

    /// <summary>선택 영역 값을 입력 폼에 채운다(Areas는 이미 층-로컬 v) → 전개도 점선 미리보기도 그 영역으로 이동.</summary>
    private void FillAreaForm(AreaDto a)
    {
        var c = a.Corners is { Length: 4 } ? a.Corners : RectCorners(a.UMin, a.VMin, a.UMax, a.VMax);   // 구데이터 폴백
        AreaName = a.Name;
        for (int i = 0; i < 4; i++) SetCorner(i, c[i][0], c[i][1]);
        StationOverride = a.StationX is not null && a.StationY is not null;
        StationX = a.StationX ?? 0; StationY = a.StationY ?? 0; StationTheta = a.StationTheta ?? 0;
        StationStandoffM = a.StationStandoffM;
    }

    /// <summary>
    /// 선택 면을 **면 전체**로 투영. 면 전체=회색 음영, 선택 층 밴드=활성(밝게). 영역은 선택 층=녹색·타 층=회색.
    /// 입력값은 층-로컬 v이므로 그릴 때 +VOff로 면-전체 v로 변환. (좌표는 auto-fit·v 뒤집기.)
    /// </summary>
    private double _projScale = 1, _projVlen = 1;   // 역투영(캔버스 클릭)용

    private void Project()
    {
        AreaBoxes.Clear(); InactiveAreaBoxes.Clear(); DraftAreas.Clear();
        TaskSegments.Clear(); StationMarkers.Clear(); DraftSegments.Clear();
        ProposalAreas.Clear(); ProposalRemovals.Clear(); ProposalSegments.Clear();
        CrossPreview.Clear();
        if (SelectedWall is not { } w || w.ULen <= 0 || w.VLen <= 0) return;

        double off = VOff;                     // 층-로컬 → 면-전체 v
        double vlen = w.VLen;                  // 캔버스 = 면 전체
        double scale = Math.Min((CanvasSize - 2 * Margin) / w.ULen, (CanvasSize - 2 * Margin) / vlen);
        _projScale = scale; _projVlen = vlen;
        (double x, double y) Proj(double u, double v) => (Margin + u * scale, Margin + (vlen - v) * scale);

        // 코너 배열(면-전체 v) → 캔버스 폴리곤 + 라벨 앵커(centroid).
        AreaPoly Poly(double[][] corners, string label)
        {
            var pts = Plot2D.ProjectCorners(corners, Proj);
            var c = Plot2D.Centroid(pts);
            return new AreaPoly(pts, c.X, c.Y, label);
        }

        // 면 전체(회색 음영) + 선택 층 활성 밴드(밝게)
        FaceOutline = Plot2D.ProjectPolygon(FaceClipPolygon(w, 0, w.VLen), Proj);
        ActiveBand = SliceH > 1e-9 ? Plot2D.ProjectPolygon(FaceClipPolygon(w, off, off + SliceH), Proj) : Array.Empty<Pt2>();

        // 영역: 그 면 모든 층(면-전체 v 코너). 선택 층=활성(녹색), 타 층=비활성(회색).
        int? sel = SelectedLevel?.Level;
        foreach (var a in _allAreas)
        {
            var corners = a.Corners ?? RectCorners(a.UMin, a.VMin, a.UMax, a.VMax);   // 구데이터 폴백
            var poly = Poly(corners, a.Name);
            if (sel is null || a.Level == sel)
            {
                AreaBoxes.Add(poly);
                StationMarkers.Add(new StationMarker(poly.LabelX - 7, poly.LabelY - 7, a.Name));   // centroid
            }
            else InactiveAreaBoxes.Add(poly);
        }
        // 선택 영역의 작업(로컬 v → +off)
        foreach (var t in AreaTasks)
        {
            var (x1, y1) = Proj(t.StartU, t.StartV + off);
            var (x2, y2) = Proj(t.EndU, t.EndV + off);
            TaskSegments.Add(new TaskSeg(x1, y1, x2, y2, x2 - 4, y2 - 4, (x1 + x2) / 2, (y1 + y2) / 2, t.Seq.ToString()));
        }
        // 입력 미리보기(코너, 로컬 v → +off) — 활성 밴드 위치에 표시
        var draft = InputCorners();
        var (dmiu, dmiv, dmau, dmav) = AreaBboxLocal(draft);
        if (dmau - dmiu > 1e-6 && dmav - dmiv > 1e-6)
            DraftAreas.Add(Poly(draft.Select(p => new[] { p[0], p[1] + off }).ToArray(), AreaName));
        // 어시스턴트 변경안(면-전체 v) — 이 면의 통과 연산만
        foreach (var op in _proposal?.Where(o => o.Ok && o.WallCode == w.WallCode) ?? [])
        {
            if (op.Corners is { Length: >= 3 } c)
            {
                var poly = Poly(c, op.AreaName ?? "");
                if (op.Kind == "deleteArea") ProposalRemovals.Add(poly); else if (op.Kind.EndsWith("Area")) ProposalAreas.Add(poly);
            }
            if (op.Segment is { Length: 4 } sg && op.Kind != "deleteTask")
            {
                var (sx1, sy1) = Proj(sg[0], sg[1]);
                var (sx2, sy2) = Proj(sg[2], sg[3]);
                ProposalSegments.Add(new TaskSeg(sx1, sy1, sx2, sy2, sx2 - 4, sy2 - 4, (sx1 + sx2) / 2, (sy1 + sy2) / 2, ""));
            }
        }
        if (SelectedArea is not null)
        {
            var (px1, py1) = Proj(StartU, StartV + off);
            var (px2, py2) = Proj(EndU, EndV + off);
            DraftSegments.Add(new TaskSeg(px1, py1, px2, py2, px2 - 4, py2 - 4, (px1 + px2) / 2, (py1 + py2) / 2, "new"));
        }
        // CROSS 그리기/백필 미리보기 — 중심에서 각 가지로 뻗는 선분(중심=start 점 표시)
        if (_crossCenter is { } cc && _crossArms.Count > 0)
        {
            var (cx, cy) = Proj(cc.U, cc.V + off);
            foreach (var arm in _crossArms)
            {
                var (ax, ay) = Proj(arm.U, arm.V + off);
                CrossPreview.Add(new TaskSeg(cx, cy, ax, ay, ax - 4, ay - 4, cx, cy, ""));
            }
        }
    }

    private static double[][] RectCorners(double uMin, double vMin, double uMax, double vMax) => new[]
    {
        new[] { uMin, vMin }, new[] { uMax, vMin }, new[] { uMax, vMax }, new[] { uMin, vMax },
    };

    private static double[][]? OffsetCornersV(double[][]? corners, double dv) =>
        corners?.Select(p => new[] { p[0], p[1] + dv }).ToArray();

    partial void OnPickModeChanged(bool value) { if (value) { CornerIndex = 0; CancelCrossDraw(); } }   // 켜면 P1부터, 교차 그리기와 배타

    /// <summary>픽 모드 해제 — 캔버스 우클릭 또는 ESC 키. 교차 그리기 중이면 그것을 해제.</summary>
    [RelayCommand]
    private void CancelPick()
    {
        if (CrossDrawShape is not null) CancelCrossDraw();
        else PickMode = false;
    }

    /// <summary>캔버스 클릭(px) → 면-로컬 (u,v, 층-로컬) 역투영. 교차 그리기 중이면 중심/가지, 아니면 코너 픽.</summary>
    public void CanvasClick(double px, double py)
    {
        if (SelectedWall is not { } w || _projScale <= 0) return;
        double u = Math.Clamp((px - Margin) / _projScale, 0, w.ULen);
        double v = Math.Clamp(_projVlen - (py - Margin) / _projScale - VOff, 0, SliceH > 1e-9 ? SliceH : w.VLen);
        if (CrossDrawShape is not null) { CrossClick(u, v); return; }
        if (!PickMode) return;
        SetCorner(CornerIndex % 4, u, v);
        CornerIndex = (CornerIndex + 1) % 4;
        Project();
    }

    private void SetCorner(int i, double u, double v)
    {
        switch (i)
        {
            case 0: C1U = u; C1V = v; break;
            case 1: C2U = u; C2V = v; break;
            case 2: C3U = u; C3V = v; break;
            default: C4U = u; C4V = v; break;
        }
    }

    // ── CROSS3/4 교차 그리기 [VDA §8.5.1, N13] ──

    /// <summary>교차 그리기 시작 — shape = "CROSS3" | "CROSS4". 영역 선택 필요. 코너 픽과 배타.</summary>
    [RelayCommand]
    private void StartCrossDraw(string? shape)
    {
        if (SelectedArea is null) { StatusMessage = "먼저 영역을 선택하세요."; return; }
        if (shape is not ("CROSS3" or "CROSS4")) return;
        PickMode = false;
        _crossCenter = null; _crossArms.Clear(); _crossPointsForRegister = null;
        CrossStep = 0; SeamRotationText = "";
        CrossDrawShape = shape;   // 마지막에 설정(CanvasPickActive·SeamTypeEditable 갱신)
        UpdateCrossStepText();
        Project();
    }

    /// <summary>교차 그리기 취소 — 버퍼·미리보기 비움(우클릭/ESC/완료 후).</summary>
    [RelayCommand]
    private void CancelCrossDraw()
    {
        if (CrossDrawShape is null && _crossArms.Count == 0 && _crossCenter is null) return;
        CrossDrawShape = null; CrossStep = 0; CrossStepText = "";
        _crossCenter = null; _crossArms.Clear(); _crossPointsForRegister = null;
        CrossPreview.Clear();
        Project();
    }

    private void CrossClick(double u, double v)
    {
        if (CrossStep == 0)
        {
            _crossCenter = (u, v);
            StartU = u; StartV = v; EndU = u; EndV = v;   // 중심 = seamStart=seamEnd(퇴화, §4.4)
            CrossStep = 1;
        }
        else
        {
            _crossArms.Add((u, v));
            CrossStep++;
        }
        UpdateCrossStepText();
        Project();
        if (_crossArms.Count >= CrossArmCount) _ = CompleteCrossDrawAsync();
    }

    /// <summary>가지 수집 완료 → 서버 /cross-preview 로 회전 유도·AMR 점 정렬. 비동기 경합은 토큰으로 차단.</summary>
    private async Task CompleteCrossDrawAsync()
    {
        if (SelectedArea is not { } a || _crossCenter is not { } center || CrossDrawShape is not { } shape) return;
        int token = ++_crossSeq;
        double off = VOff;
        var armsGlobal = _crossArms.Select(p => new[] { p.U, p.V + off }).ToArray();
        try
        {
            var res = await _api.CrossPreviewAsync(a.AreaId, shape, center.U, center.V + off, armsGlobal);
            if (token != _crossSeq) return;   // 더 최근 그리기로 대체됨
            if (res is null) { SeamRotationText = "미리보기 실패"; return; }
            if (!res.FrameOk)
            {
                SeamRotationText = "바닥/천장 CROSS3 미지원";
                StatusMessage = "이 면(바닥/천장)은 CROSS3 회전을 정의할 수 없습니다 — AMR 역질의 중.";
                return;
            }
            if (SeamTypes.Contains(res.SeamType)) SelectedSeamType = res.SeamType;   // 유도된 CROSS3_R* / CROSS4
            SeamRotationText = shape == "CROSS4"
                ? "CROSS4"
                : res.SeamType + (res.SnapResidualDeg > 10 ? $" (격자 이탈 {res.SnapResidualDeg:0}°)" : " (자동)");
            _crossPointsForRegister = _crossArms.Select(p => new[] { p.U, p.V }).ToArray();   // 층-로컬
            StatusMessage = $"교차 그리기 완료 — 회전 {SeamRotationText}. [작업 등록]을 누르세요.";
            Project();
        }
        catch (Exception ex) { if (token == _crossSeq) SeamRotationText = $"미리보기 오류: {ex.Message}"; }
    }

    private void UpdateCrossStepText()
    {
        if (CrossDrawShape is null) { CrossStepText = ""; return; }
        if (CrossStep == 0) CrossStepText = "① 중심 클릭";
        else if (CrossDrawShape == "CROSS3")
            CrossStepText = CrossStep switch { 1 => "② 줄기 클릭(회전 결정)", 2 => "③ 통과선 1 클릭", 3 => "④ 통과선 2 클릭", _ => "완료" };
        else
            CrossStepText = CrossStep <= CrossArmCount ? $"가지 {CrossStep}/{CrossArmCount} 클릭" : "완료";
    }

    /// <summary>등록·수정용 가지 끝점(면-전체 v). 교차 그리기·백필 버퍼가 있을 때만 non-null.</summary>
    private double[][]? CrossPointsFaceGlobal(double off) =>
        _crossPointsForRegister?.Select(p => new[] { p[0], p[1] + off }).ToArray();

    /// <summary>
    /// 면을 v∈[vLo, vHi]로 클리핑한 (u,v) 정점(면-전체 v, 재원점 없음).
    /// 마구리(F/A)+지오메트리 로드 시 챔퍼로 잘린 팔각/사다리꼴, 그 외 직사각형.
    /// </summary>
    private IReadOnlyList<(double u, double v)> FaceClipPolygon(WallDto w, double vLo, double vHi)
    {
        vLo = Math.Max(0, vLo); vHi = Math.Min(w.VLen, vHi);
        if (vHi <= vLo) return Array.Empty<(double, double)>();

        if (w.WallCode is "F" or "A" && _derH > 0)
        {
            double zw = HLow + HWall;
            var vs = new List<double> { vLo };
            foreach (var knee in new[] { HLow, zw })
                if (knee > vLo + 1e-9 && knee < vHi - 1e-9) vs.Add(knee);
            vs.Add(vHi);
            vs.Sort();
            var pts = new List<(double, double)>(vs.Count * 2);
            foreach (var v in vs) { var (l, _) = HalfWidthU(v); pts.Add((l, v)); }                    // 좌측(u 작은) 아래→위
            for (int i = vs.Count - 1; i >= 0; i--) { var (_, r) = HalfWidthU(vs[i]); pts.Add((r, vs[i])); } // 우측 위→아래
            return pts;
        }
        return new[] { (0.0, vLo), (w.ULen, vLo), (w.ULen, vHi), (0.0, vHi) };
    }

    /// <summary>마구리 팔각의 높이 v(=z)에서 좌/우 경계 u — 하부챔퍼/수직/상부챔퍼 구간별. (u 중심=B/2)</summary>
    private (double uLeft, double uRight) HalfWidthU(double v)
    {
        double b = _derB, hw;   // 반폭
        double wf2 = WFloor / 2, wc2 = _derWCeil / 2, b2 = b / 2, hl = HLow, zw = HLow + HWall, h = _derH;
        if (v <= hl) hw = hl > 1e-9 ? wf2 + (v / hl) * (b2 - wf2) : b2;          // 하부 챔퍼
        else if (v <= zw) hw = b2;                                               // 수직
        else { double hu = h - zw; hw = hu > 1e-9 ? b2 - ((v - zw) / hu) * (b2 - wc2) : wc2; }   // 상부 챔퍼
        return (b2 - hw, b2 + hw);
    }

    partial void OnC1UChanged(double value) => Project();
    partial void OnC1VChanged(double value) => Project();
    partial void OnC2UChanged(double value) => Project();
    partial void OnC2VChanged(double value) => Project();
    partial void OnC3UChanged(double value) => Project();
    partial void OnC3VChanged(double value) => Project();
    partial void OnC4UChanged(double value) => Project();
    partial void OnC4VChanged(double value) => Project();
    partial void OnAreaNameChanged(string value) => Project();
    partial void OnStartUChanged(double value) => Project();
    partial void OnStartVChanged(double value) => Project();
    partial void OnEndUChanged(double value) => Project();
    partial void OnEndVChanged(double value) => Project();

    private void ApplyGeometry(TankGeometryDto g)
    {
        LengthL = g.LengthL; WFloor = g.WFloor; ThetaLowDeg = g.ThetaLowDeg; HLow = g.HLow;
        HWall = g.HWall; ThetaUpDeg = g.ThetaUpDeg; HUp = g.HUp;
        OriginOx = g.OriginOx; OriginOy = g.OriginOy;
        ReachZMinText = g.ReachZMin?.ToString("0.###", CultureInfo.InvariantCulture) ?? "";
        ReachZMaxText = g.ReachZMax?.ToString("0.###", CultureInfo.InvariantCulture) ?? "";
        if (g.LevelZ is { Length: > 0 })
        {
            LevelZText = string.Join(", ", g.LevelZ.Select(z => z.ToString("0.###", CultureInfo.InvariantCulture)));
            // 층 필터 목록 = level_z 길이만큼 L1..LN (기존 선택 층 유지)
            int keep = SelectedLevel?.Level ?? 1;
            Levels.Clear();
            for (int i = 1; i <= g.LevelZ.Length; i++) Levels.Add(new LevelOption(i, $"L{i}"));
            SelectedLevel = Levels.FirstOrDefault(x => x.Level == keep) ?? Levels.FirstOrDefault();
        }
        var d = g.Derived;
        _derB = d.B; _derWCeil = d.WCeil; _derH = d.H;   // 마구리 팔각 윤곽용
        DerivedText = $"B(전폭)={d.B:0.###}  W_ceil(천장폭)={d.WCeil:0.###}  H(전체높이)={d.H:0.###}";
    }

    partial void OnSelectedLevelChanged(LevelOption? value) => _ = ReloadWallsForLevelAsync();

    /// <summary>층 필터 변경 시 면 목록 재적재 후 전개도 갱신 [v3.1 §9].</summary>
    private async Task ReloadWallsForLevelAsync()
    {
        try { await LoadWallsAsync(); await RefreshAreasAsync(); }
        catch (Exception ex) { StatusMessage = $"면 조회 실패: {ex.Message}"; }
    }

    private static double[] ParseLevelZ(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();

    /// <summary>빈 문자열 → null, 아니면 파싱(실패 시 null). reach_z 선택 입력용.</summary>
    private static double? ParseOptional(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null
        : double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
}
