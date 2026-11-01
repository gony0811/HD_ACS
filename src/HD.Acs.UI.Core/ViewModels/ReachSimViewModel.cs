using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.Acs.UI.Models;
using HD.Acs.UI.Rendering;
using HD.Acs.UI.Services;

namespace HD.Acs.UI.ViewModels;

/// <summary>조사 결과로 채운 영역 목록 항목.</summary>
public sealed record ReachAreaItem(ReachSurveyAreaDto Area, string Label)
{
    public override string ToString() => Label;
}

/// <summary>조사 결과로 채운 용접선 목록 항목.</summary>
public sealed record ReachSeamItem(ReachSurveySeamDto Seam, string Label)
{
    public override string ToString() => Label;
}

/// <summary>부품별 간섭 표 한 줄. Level = ok | warn | bad.</summary>
public sealed record ReachPartRow(string Name, string HeightM, string Plane, string ClearanceMm, string Level);

/// <summary>IK 해 표 한 줄. ★ = 운영 선택, 선택 = 지금 그리는 해.</summary>
public sealed record ReachSolutionRow(int Index, string Label, string Joints, string Posture, bool PostureOk,
    string WristBody, string MinClearanceMm, string Level, bool IsOperational);

/// <summary>정차점 최적화 표 한 줄. Improved = 닿는 용접선이 늘어 옮길 만함, Applied = 이번에 덮어쓰기를 적용함.</summary>
public sealed record ReachOptimumRow(ReachStationOptimumDto Optimum, string Area, string Current, string Best, string Move,
    bool Improved, bool Applied = false);

/// <summary>
/// 계획 › 리치·간섭 — ACS 정차점(배차와 같은 식)에서 FR5-WML 이 용접선에 세 자세(② 400 → 정렬 준비 300 →
/// 초점 검사)로 접근할 때 관절 위치와 탱크 면 간섭을 3D 로 본다. 리프트·정차 이동·IK 해를 바꿔 볼 수 있다.
/// 계산은 서버(<c>/api/reach-sim</c>)가 하고 이 VM 은 선택 상태·표·3D 씬을 관리한다.
/// 정차점 최적화는 계산만 하고, 사용자가 적용할 때만 영역 정차 덮어쓰기(x·y·방향)를 쓴다.
/// </summary>
public sealed partial class ReachSimViewModel : ObservableObject
{
    public static readonly string[] WallOrder = { "SU", "PU", "SL", "PL", "SM", "PM", "A", "F", "T", "B" };
    public static readonly string[] StageLabels = { "② 접근 400", "정렬 준비 300", "초점 검사" };
    public static readonly string[] PointLabels = { "시작", "중간", "끝" };
    private const int DetailDebounceMs = 150;

    private readonly IAcsApiClient _api;
    private readonly List<WallDto> _walls = new();
    /// <summary>층 → 영역이 있는 면. 비어 있으면(영역 목록을 못 읽음) 모든 면을 보인다.</summary>
    private readonly Dictionary<int, HashSet<string>> _areaWalls = new();
    private CancellationTokenSource? _detailCts;
    private bool _suppress;
    private int _surveyVersion;

    public ObservableCollection<int> Levels { get; } = new(new[] { 1, 2, 3, 4 });
    public ObservableCollection<string> WallCodes { get; } = new(WallOrder);
    public ObservableCollection<ReachAreaItem> Areas { get; } = new();
    public ObservableCollection<ReachSeamItem> Seams { get; } = new();
    public ObservableCollection<ReachPartRow> Parts { get; } = new();
    public ObservableCollection<ReachSolutionRow> Solutions { get; } = new();
    public ObservableCollection<string> YawOptions { get; } = new();
    public ObservableCollection<ReachOptimumRow> Optima { get; } = new();
    public IReadOnlyList<string> Stages => StageLabels;
    public IReadOnlyList<string> Points => PointLabels;

    [ObservableProperty] private string _tankId = TankLayout.DefaultTankId;
    [ObservableProperty] private int _selectedLevel = 4;
    [ObservableProperty] private string _selectedWall = "SU";
    [ObservableProperty] private ReachAreaItem? _selectedArea;
    [ObservableProperty] private ReachSeamItem? _selectedSeam;
    [ObservableProperty] private int _pointIndex = 1;
    [ObservableProperty] private int _stageIndex = 2;
    [ObservableProperty] private double _strokeMm;
    [ObservableProperty] private double _offsetForwardMm;
    [ObservableProperty] private double _offsetLeftMm;
    [ObservableProperty] private int _yawIndex;
    [ObservableProperty] private bool _clipSection = true;
    /// <summary>IK 가지를 간섭 여유 최대로 고른다(대안 검토). 끄면 운영 규칙(HD_AMR — 자세 여유 최대).</summary>
    [ObservableProperty] private bool _clearanceBranch;
    /// <summary>그릴 IK 해 순번 — null = 운영 선택.</summary>
    [ObservableProperty] private int? _solutionIndex;
    [ObservableProperty] private ReachSolutionRow? _selectedSolution;
    [ObservableProperty] private ReachDetailDto? _detail;

    [ObservableProperty] private string _statusMessage = "선창 면을 불러오세요.";
    [ObservableProperty] private string _surveySummary = "";
    [ObservableProperty] private string _verdictTitle = "—";
    [ObservableProperty] private string _verdictDetail = "";
    /// <summary>ok | warn | bad | na — 판정 카드 색.</summary>
    [ObservableProperty] private string _verdictLevel = "na";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private ReachOptimumRow? _selectedOptimum;
    [ObservableProperty] private string _optimizeSummary = "";
    [ObservableProperty] private string _optimumDetail = "";

    /// <summary>현재 3D 씬. 바뀌면 <see cref="SceneChanged"/>.</summary>
    public Scene3 Scene { get; private set; } = new();
    /// <summary>씬만 바뀜(카메라 유지).</summary>
    public event EventHandler? SceneChanged;
    /// <summary>정차가 바뀌어 카메라를 다시 맞춰야 함.</summary>
    public event EventHandler? ViewReset;

    public bool HasYawChoice => YawOptions.Count > 1;

    public ReachSimViewModel(IAcsApiClient api) => _api = api;

    /// <summary>탱크 면을 읽고 현재 층·면을 조사한다.</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        try
        {
            StatusMessage = "선창 면 조회 중…";
            _walls.Clear();
            _walls.AddRange(await _api.GetWallsAsync(TankId));
            if (await _api.GetTankGeometryAsync(TankId) is { LevelZ: { Length: > 0 } z })
            {
                _suppress = true;
                Levels.Clear();
                for (var i = 1; i <= z.Length; i++) Levels.Add(i);
                if (!Levels.Contains(SelectedLevel)) SelectedLevel = Levels[^1];
                _suppress = false;
                OnPropertyChanged(nameof(LevelIndex));
            }
            try { ApplyAreaWalls(await _api.GetAreasAsync(TankId)); }
            catch { ApplyAreaWalls(Array.Empty<AreaDto>()); }   // 면 필터 없이 계속
            await SurveyAsync();
        }
        catch (Exception ex) { StatusMessage = $"선창 조회 실패: {ex.Message}"; }
    }

    /// <summary>층 콤보 순번(0 기반) — 콤보가 비는 순간 null 이 int 로 들어오지 않게 순번으로 묶는다.</summary>
    public int LevelIndex
    {
        get => Levels.IndexOf(SelectedLevel);
        set { if (value >= 0 && value < Levels.Count) SelectedLevel = Levels[value]; }
    }

    /// <summary>면 콤보 순번(0 기반) — 층을 바꿔 목록이 줄 때 사라진 면이 null 로 들어오지 않게 순번으로 묶는다.</summary>
    public int WallIndex
    {
        get => WallCodes.IndexOf(SelectedWall);
        set { if (value >= 0 && value < WallCodes.Count) SelectedWall = WallCodes[value]; }
    }

    /// <summary>영역 목록으로 층별 면을 모은다. 면 목록은 그 층에 영역이 있는 면만 남긴다(PU·SU·T 는 최상층에만 있다).</summary>
    public void ApplyAreaWalls(IEnumerable<AreaDto> areas)
    {
        _areaWalls.Clear();
        foreach (var a in areas)
        {
            if (!_areaWalls.TryGetValue(a.Level, out var set)) _areaWalls[a.Level] = set = new();
            set.Add(a.WallCode);
        }
        SyncWallCodes();
    }

    private void SyncWallCodes()
    {
        var want = _areaWalls.TryGetValue(SelectedLevel, out var set) && set.Count > 0
            ? WallOrder.Where(set.Contains).Concat(set.Except(WallOrder).Order()).ToList()
            : WallOrder.ToList();
        var was = _suppress;
        _suppress = true;   // 고친 면으로 따로 조사하지 않는다 — 부른 쪽이 조사한다
        if (!want.Contains(SelectedWall)) SelectedWall = want[0];
        for (var i = WallCodes.Count - 1; i >= 0; i--)
            if (!want.Contains(WallCodes[i])) WallCodes.RemoveAt(i);
        for (var i = 0; i < want.Count; i++)
            if (i >= WallCodes.Count || WallCodes[i] != want[i]) WallCodes.Insert(i, want[i]);
        _suppress = was;
        OnPropertyChanged(nameof(WallIndex));
    }

    partial void OnSelectedLevelChanged(int value)
    {
        OnPropertyChanged(nameof(LevelIndex));
        ClearOptima();
        SyncWallCodes();
        if (!_suppress) _ = SurveyAsync();
    }
    partial void OnSelectedWallChanged(string value)
    {
        OnPropertyChanged(nameof(WallIndex));
        ClearOptima();
        if (!_suppress) _ = SurveyAsync();
    }
    partial void OnClearanceBranchChanged(bool value) { ClearOptima(); _ = SurveyAsync(); }

    private string? Branch => ClearanceBranch ? "clearance" : null;

    private void ClearOptima()
    {
        Optima.Clear();
        OptimizeSummary = "";
    }

    partial void OnSelectedOptimumChanged(ReachOptimumRow? value)
    {
        if (value is null) { OptimumDetail = ""; return; }
        var (c, b) = (value.Optimum.Current, value.Optimum.Best);
        static string Cnt(ReachStationCandidateDto x) => $"간섭 없음 {x.Clear} · 간섭 {x.Collision} · 불가 {x.Unreachable}";
        OptimumDetail = $"{value.Optimum.Name}: 지금 — 중심에서 안쪽 {c.DistM:0.00} m · 좌 {c.LateralM:+0.00;-0.00} m · 방향 {c.YawDeg:+0;-0}° · " +
                        $"차체 여유 {c.BodyClearanceM * 1000:0} mm · {Cnt(c)}\n" +
                        (value.Improved || value.Applied
                            ? $"찾은 정차 — 안쪽 {b.DistM:0.00} m · 좌 {b.LateralM:+0.00;-0.00} m · 방향 {b.YawDeg:+0;-0}° · " +
                              $"차체 여유 {b.BodyClearanceM * 1000:0} mm · {Cnt(b)} · 도면 ({b.X:0.000}, {b.Y:0.000}, {b.Theta:0.0000} rad)" +
                              (value.Applied ? " — 적용됨" : "")
                            : "더 나은 정차 없음 — 옮겨도 닿는 용접선이 늘지 않습니다.");
    }

    /// <summary>선택 층·면의 영역·용접선마다 리프트를 훑어 간섭 없이 닿는지 서버에 묻는다.</summary>
    [RelayCommand]
    public async Task SurveyAsync()
    {
        var version = ++_surveyVersion;
        IsBusy = true;
        StatusMessage = $"L{SelectedLevel} {SelectedWall} 조사 중… (리프트 0~1000 mm × 세 자세)";
        try
        {
            var areas = await _api.GetReachSurveyAsync(TankId, SelectedLevel, SelectedWall, branch: Branch);
            if (version != _surveyVersion) return;   // 그 사이 층·면이 또 바뀜
            Areas.Clear();
            Seams.Clear();
            if (areas is null) { StatusMessage = $"선창 '{TankId}' 형상이 없습니다 — 계획 › 영역 · 작업에서 선창을 등록하세요."; return; }
            foreach (var a in areas) Areas.Add(new ReachAreaItem(a, AreaLabel(a)));

            var all = areas.SelectMany(a => a.Seams).ToList();
            int clear = all.Count(s => s.Verdict == "Clear"), hit = all.Count(s => s.Verdict == "Collision");
            SurveySummary = $"용접선 {all.Count} · 간섭 없이 도달 {clear} · 닿지만 간섭 {hit} · 도달 불가 {all.Count - clear - hit}";
            StatusMessage = areas.Count == 0 ? $"L{SelectedLevel} {SelectedWall} 에 영역이 없습니다." : $"영역 {areas.Count}개 조사 완료";
            // 간섭이 있는 첫 영역부터 보여 준다(없으면 첫 영역).
            SelectedArea = Areas.FirstOrDefault(a => a.Area.Seams.Any(s => s.Verdict == "Collision")) ?? Areas.FirstOrDefault();
        }
        catch (Exception ex) { if (version == _surveyVersion) StatusMessage = $"조사 실패: {ex.Message}"; }
        finally { if (version == _surveyVersion) IsBusy = false; }
    }

    partial void OnSelectedAreaChanged(ReachAreaItem? value)
    {
        Seams.Clear();
        if (value is null) { SelectedSeam = null; return; }
        foreach (var s in value.Area.Seams) Seams.Add(new ReachSeamItem(s, SeamLabel(s)));
        SelectedSeam = Seams.FirstOrDefault(s => s.Seam.Verdict == "Collision")
                       ?? Seams.OrderBy(s => s.Seam.MinClearanceM ?? double.PositiveInfinity).FirstOrDefault();
    }

    partial void OnSelectedSeamChanged(ReachSeamItem? value)
    {
        if (value is null) { Detail = null; return; }
        ResetStationValues(value.Seam);
        ViewResetPending = true;
        RequestDetail();
    }

    /// <summary>ACS 정차·조사 리프트로 되돌린다.</summary>
    [RelayCommand]
    private void ResetStation()
    {
        if (SelectedSeam is null) return;
        ResetStationValues(SelectedSeam.Seam);
        ViewResetPending = true;
        RequestDetail();
    }

    private void ResetStationValues(ReachSurveySeamDto s)
    {
        _suppress = true;
        StrokeMm = s.StrokeMm ?? 0;
        OffsetForwardMm = 0;
        OffsetLeftMm = 0;
        YawIndex = s.YawIndex;
        SolutionIndex = null;
        _suppress = false;
    }

    partial void OnPointIndexChanged(int value) { if (!_suppress) { SolutionIndex = null; RequestDetail(); } }
    partial void OnStrokeMmChanged(double value) { if (!_suppress) RequestDetail(); }
    partial void OnOffsetForwardMmChanged(double value) { if (!_suppress) RequestDetail(); }
    partial void OnOffsetLeftMmChanged(double value) { if (!_suppress) RequestDetail(); }
    partial void OnYawIndexChanged(int value) { if (!_suppress) { ViewResetPending = true; RequestDetail(); } }
    partial void OnStageIndexChanged(int value) { if (!_suppress) { SolutionIndex = null; Refresh(); } }
    partial void OnSolutionIndexChanged(int? value) { if (!_suppress) Refresh(); }
    partial void OnClipSectionChanged(bool value) => Refresh();
    partial void OnSelectedSolutionChanged(ReachSolutionRow? value)
    {
        if (_suppress || value is null) return;
        SolutionIndex = value.Index;
    }

    /// <summary>운영 선택 해로 되돌린다.</summary>
    [RelayCommand]
    private void OperationalSolution() => SolutionIndex = null;

    /// <summary>자세 한계 안에서 간섭 여유가 가장 큰 해를 고른다.</summary>
    [RelayCommand]
    private void ClearestSolution()
    {
        if (Detail is null || StageIndex >= Detail.Stages.Count) return;
        var sols = Detail.Stages[StageIndex].Solutions;
        var best = sols.Select((s, i) => (s, i)).OrderByDescending(x => x.s.PostureOk).ThenByDescending(x => x.s.MinClearanceM)
            .Select(x => (int?)x.i).FirstOrDefault();
        SolutionIndex = best;
    }

    private bool ViewResetPending { get; set; }

    /// <summary>입력이 잇따라 바뀌는 동안(슬라이더) 마지막 값만 서버에 묻는다.</summary>
    private void RequestDetail()
    {
        if (SelectedArea is null || SelectedSeam is null) return;
        _detailCts?.Cancel();
        var cts = _detailCts = new CancellationTokenSource();
        _ = FetchDetailAsync(SelectedArea.Area.AreaId, SelectedSeam.Seam.TaskId, cts.Token);
    }

    private async Task FetchDetailAsync(Guid areaId, Guid taskId, CancellationToken ct)
    {
        try
        {
            await Task.Delay(DetailDebounceMs, ct);
            var d = await _api.GetReachDetailAsync(areaId, taskId, PointIndex, StrokeMm, OffsetForwardMm, OffsetLeftMm, YawIndex, ct, Branch);
            if (ct.IsCancellationRequested) return;
            ApplyDetail(d);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusMessage = $"시뮬레이션 실패: {ex.Message}"; }
    }

    /// <summary>상세 응답 반영(테스트가 직접 부른다).</summary>
    public void ApplyDetail(ReachDetailDto? d)
    {
        Detail = d;
        _suppress = true;
        YawOptions.Clear();
        if (d is not null)
            for (var i = 0; i < d.YawCount; i++)
                YawOptions.Add(d.YawCount == 1 ? "면 정면" : $"{i * 90}° 회전");
        _suppress = false;
        OnPropertyChanged(nameof(HasYawChoice));
        Refresh();
        if (ViewResetPending) { ViewResetPending = false; ViewReset?.Invoke(this, EventArgs.Empty); }
    }

    /// <summary>판정·표·씬을 현재 자세·해 선택으로 다시 만든다(서버 왕복 없음).</summary>
    private void Refresh()
    {
        var d = Detail;
        Parts.Clear();
        Solutions.Clear();
        if (d is null)
        {
            VerdictTitle = "—"; VerdictDetail = ""; VerdictLevel = "na";
            Scene = new Scene3();
            SceneChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var warn = d.WarnClearanceM;
        string Lv(double c) => c < 0 ? "bad" : c < warn ? "warn" : "ok";
        string Mm(double m) => double.IsFinite(m) ? Math.Round(m * 1000).ToString("0", CultureInfo.InvariantCulture) : "–";

        var stage = StageIndex < d.Stages.Count ? d.Stages[StageIndex] : null;
        if (stage is not null)
        {
            var chosen = SolutionIndex is { } si && si < stage.Solutions.Count ? si : stage.ChosenIndex;
            _suppress = true;
            for (var i = 0; i < stage.Solutions.Count; i++)
            {
                var s = stage.Solutions[i];
                var row = new ReachSolutionRow(i, $"{i + 1}{(i == stage.ChosenIndex ? " ★" : "")}",
                    string.Join(" / ", s.JointsDeg.Take(5).Select(v => v.ToString("0", CultureInfo.InvariantCulture))),
                    s.PostureOk ? $"{s.MarginDeg:0}°" : "한계", s.PostureOk, s.WristDown ? "아래" : "위",
                    Mm(s.MinClearanceM), Lv(s.MinClearanceM), i == stage.ChosenIndex);
                Solutions.Add(row);
                if (i == chosen) SelectedSolution = row;
            }
            if (chosen < 0) SelectedSolution = null;
            _suppress = false;
        }

        var sol = ReachSimSceneBuilder.SolutionOf(d, StageIndex, SolutionIndex);
        Parts.Add(new ReachPartRow("AMR 차체", "0.89", d.Body.NearestPlane, Mm(d.Body.ClearanceM), Lv(d.Body.ClearanceM)));
        if (sol is not null)
            foreach (var p in sol.Parts)
                Parts.Add(new ReachPartRow(p.Name, (Math.Max(p.A[2], p.B[2]) - d.FloorZ).ToString("0.00", CultureInfo.InvariantCulture),
                    p.NearestPlane, Mm(p.ClearanceM), Lv(p.ClearanceM)));

        if (stage is null || sol is null)
        {
            VerdictLevel = "na";
            VerdictTitle = "도달 불가";
            VerdictDetail = (stage is null ? "앞 자세에서 해가 없어 이 자세는 풀지 않았습니다. " : "IK 해가 없습니다(팔이 닿지 않음). ")
                            + $"AMR 차체 여유 {Mm(d.Body.ClearanceM)} mm ({d.Body.NearestPlane}).";
        }
        else
        {
            var all = sol.Parts.Select(p => (Name: p.Name, p.ClearanceM, p.NearestPlane))
                .Prepend((Name: "AMR 차체", d.Body.ClearanceM, d.Body.NearestPlane)).ToList();
            int hits = all.Count(p => p.ClearanceM < 0), warns = all.Count(p => p.ClearanceM >= 0 && p.ClearanceM < warn);
            var worst = all.OrderBy(p => p.ClearanceM).First();
            VerdictLevel = hits > 0 ? "bad" : !sol.PostureOk || warns > 0 ? "warn" : "ok";
            VerdictTitle = (hits > 0 ? $"간섭 {hits}곳" : warns > 0 ? $"여유 부족 {warns}곳" : "간섭 없음")
                           + (sol.PostureOk ? "" : " · 자세 한계");
            VerdictDetail = $"최소 여유 {Mm(worst.ClearanceM)} mm — {worst.Name} ↔ {worst.NearestPlane}. " +
                            $"자세 여유 {sol.MarginDeg:0.0}° ({sol.Limiting}). J5 몸체 방향 {(sol.WristDown ? "아래" : "위")}. " +
                            $"관절 {string.Join(" / ", sol.JointsDeg.Take(5).Select(v => v.ToString("0.0", CultureInfo.InvariantCulture)))}°";
        }

        Scene = ReachSimSceneBuilder.Build(new ReachSceneInput(_walls, d, StageIndex, SolutionIndex, ClipSection));
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── 정차점 최적화 ────────────────────────────────────────────────

    /// <summary>선택 층·면의 영역마다 간섭 없이 닿는 용접선이 가장 많은 정차를 서버에서 찾는다(계산만 — DB 를 바꾸지 않는다).</summary>
    [RelayCommand]
    public async Task OptimizeStationsAsync()
    {
        IsBusy = true;
        StatusMessage = $"L{SelectedLevel} {SelectedWall} 정차점 최적화 중… (영역마다 후보 정차 수십 곳 × 리프트 × 세 자세)";
        try
        {
            var list = await _api.GetStationOptimaAsync(TankId, SelectedLevel, SelectedWall, null, branch: Branch);
            ApplyOptima(list);
            StatusMessage = list is null ? $"선창 '{TankId}' 형상이 없습니다." : $"정차점 최적화 완료 — 영역 {list.Count}개";
        }
        catch (Exception ex) { StatusMessage = $"정차점 최적화 실패: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    /// <summary>최적화 응답 반영(테스트가 직접 부른다).</summary>
    public void ApplyOptima(IReadOnlyList<ReachStationOptimumDto>? list)
    {
        Optima.Clear();
        if (list is null) { OptimizeSummary = ""; return; }
        foreach (var o in list) Optima.Add(Row(o));
        int cur = list.Sum(o => o.Current.Clear), best = list.Sum(o => o.Best.Clear), n = list.Sum(o => o.Current.Clear + o.Current.Collision + o.Current.Unreachable);
        OptimizeSummary = $"간섭 없이 도달 {cur} → {best} / {n} · 옮길 영역 {Optima.Count(r => r.Improved)}개 (바닥·천장은 대상 아님)";
        SelectedOptimum = Optima.FirstOrDefault(r => r.Improved);
    }

    private static ReachOptimumRow Row(ReachStationOptimumDto o, bool applied = false)
    {
        static string Counts(ReachStationCandidateDto c) => $"{c.Clear}/{c.Collision}/{c.Unreachable}";
        var improved = o.Best.Clear > o.Current.Clear;
        var move = !improved ? "그대로"
            : $"{o.Best.DistM:0.0} m ← {o.Current.DistM:0.0}" +
              (Math.Abs(o.Best.LateralM - o.Current.LateralM) > 1e-6 ? $" · 좌 {o.Best.LateralM:+0.0;-0.0}" : "") +
              (Math.Abs(o.Best.YawDeg - o.Current.YawDeg) > 1e-6 ? $" · {o.Best.YawDeg:+0;-0}°" : "");
        return new ReachOptimumRow(o, o.Name + (o.Overridden ? " (덮어씀)" : ""), Counts(o.Current), Counts(o.Best), move, improved, applied);
    }

    /// <summary>
    /// 고른 행의 찾은 정차를 3D 로 미리 본다 — 최적화로 새로 닿게 된 용접선(없으면 닿는 첫 용접선)을 그 리프트로,
    /// 지금 정차에서 전후·좌우 이동으로 옮겨 그린다(방향 보정은 반영하지 못한다).
    /// </summary>
    [RelayCommand]
    private void PreviewOptimum()
    {
        if (SelectedOptimum is not { } row) return;
        var area = Areas.FirstOrDefault(a => a.Area.AreaId == row.Optimum.AreaId);
        if (area is null) { StatusMessage = "조사 목록에 그 영역이 없습니다 — 다시 조사하세요."; return; }
        SelectedArea = area;   // 용접선 선택·정차 초기화가 따라온다
        var seams = row.Optimum.Seams ?? new List<ReachOptimumSeamDto>();
        var pick = seams.FirstOrDefault(s => s.Best == "Clear" && s.Current != "Clear") ?? seams.FirstOrDefault(s => s.Best == "Clear");
        if (pick is not null && Seams.FirstOrDefault(s => s.Seam.TaskId == pick.TaskId) is { } seam) SelectedSeam = seam;

        var (c, b) = (row.Optimum.Current, row.Optimum.Best);
        var (dx, dy) = (b.X - c.X, b.Y - c.Y);
        _suppress = true;
        OffsetForwardMm = Math.Round((dx * Math.Cos(c.Theta) + dy * Math.Sin(c.Theta)) * 1000);
        OffsetLeftMm = Math.Round((-dx * Math.Sin(c.Theta) + dy * Math.Cos(c.Theta)) * 1000);
        StrokeMm = pick?.BestStrokeMm ?? StrokeMm;
        SolutionIndex = null;
        _suppress = false;
        ViewResetPending = true;
        RequestDetail();
        StatusMessage = $"{row.Optimum.Name} 찾은 정차 미리보기 — 전후 {OffsetForwardMm:0} mm · 좌우 {OffsetLeftMm:0} mm"
                        + (pick is null ? "" : $" · 용접선 {pick.Seq} 리프트 {StrokeMm:0} mm")
                        + (Math.Abs(b.YawDeg - c.YawDeg) > 1e-6 ? $" (방향 {b.YawDeg - c.YawDeg:+0;-0}° 는 미리보기에 반영 안 됨)" : "") + ".";
    }

    /// <summary>고른 행의 찾은 정차를 영역 정차 덮어쓰기로 저장한다(배차가 이 정차를 쓴다).</summary>
    [RelayCommand]
    private async Task ApplyOptimumAsync()
    {
        if (SelectedOptimum is not { Improved: true } row) return;
        await ApplyRowsAsync(new[] { row });
    }

    /// <summary>옮길 만한 영역(닿는 용접선이 늘어나는 영역) 모두에 찾은 정차를 저장한다.</summary>
    [RelayCommand]
    private async Task ApplyAllOptimaAsync() => await ApplyRowsAsync(Optima.Where(r => r.Improved && !r.Applied).ToList());

    private async Task ApplyRowsAsync(IReadOnlyList<ReachOptimumRow> rows)
    {
        if (rows.Count == 0) return;
        IsBusy = true;
        var done = 0;
        try
        {
            foreach (var r in rows)
            {
                var b = r.Optimum.Best;
                await _api.SetAreaStationAsync(r.Optimum.AreaId, b.X, b.Y, b.Theta,
                    $"정차점 최적화: 간섭 없이 도달 {r.Optimum.Current.Clear}→{b.Clear}");
                var i = Optima.IndexOf(r);
                if (i >= 0) Optima[i] = Row(r.Optimum with { Overridden = true }, applied: true) with { Improved = false };
                done++;
            }
            StatusMessage = $"정차 덮어쓰기 {done}개 영역 적용 — 다시 조사합니다.";
        }
        catch (Exception ex) { StatusMessage = $"정차 적용 실패({done}개 적용 후): {ex.Message}"; }
        finally { IsBusy = false; }
        await SurveyAsync();
    }

    /// <summary>선택 영역의 정차 덮어쓰기를 지워 배차 식 정차로 되돌린다.</summary>
    [RelayCommand]
    private async Task ClearStationOverrideAsync()
    {
        if (SelectedArea is not { } a) return;
        try
        {
            await _api.SetAreaStationAsync(a.Area.AreaId, null, null, null, "정차 덮어쓰기 해제");
            StatusMessage = $"{a.Area.Name} 정차 덮어쓰기 해제 — 다시 조사합니다.";
        }
        catch (Exception ex) { StatusMessage = $"덮어쓰기 해제 실패: {ex.Message}"; return; }
        await SurveyAsync();
    }

    private static string AreaLabel(ReachSurveyAreaDto a)
    {
        int n = a.Seams.Count, hit = a.Seams.Count(s => s.Verdict == "Collision"), un = a.Seams.Count(s => s.Verdict == "Unreachable");
        var tag = hit > 0 ? $"간섭 {hit}" : un == n && n > 0 ? "도달 불가" : un > 0 ? $"불가 {un}" : "간섭 없음";
        return $"{a.Name} · {tag} / {n}";
    }

    private static string SeamLabel(ReachSurveySeamDto s)
    {
        var tag = s.Verdict switch
        {
            "Clear" => $"간섭 없음 (h {s.StrokeMm:0})",
            "Collision" => $"간섭 {(s.MinClearanceM is { } c ? $"{c * 1000:0} mm" : "")}",
            _ => "도달 불가",
        };
        return $"{s.Seq}. 높이 {s.ZMin:0.00}–{s.ZMax:0.00} m · {tag}";
    }
}
