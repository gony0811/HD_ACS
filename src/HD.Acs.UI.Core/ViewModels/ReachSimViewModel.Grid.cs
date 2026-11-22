using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.Acs.UI.Models;
using HD.Acs.UI.Services;

namespace HD.Acs.UI.ViewModels;

/// <summary>
/// 균등 격자 재분할 — 선택 층·면의 영역을 칸 크기(1.44 m, 1.08 × 1.08 m 등)로 다시 나눠 용접선을 묶는다.
/// 정차 묶음 최적화(set cover)로 정차마다 흩어 놓은 영역을 격자 영역으로 되돌린 뒤, 영역별 정차점 최적화를 다시 돌리는 흐름.
/// 적용하면 그 면·층의 정차 덮어쓰기·리프트는 지워진다(자동 정차점) — 이어서 정차점 최적화를 돌려 다시 정한다.
/// </summary>
public sealed partial class ReachSimViewModel
{
    /// <summary>칸 가로 [m] — 0.3~1.44.</summary>
    [ObservableProperty] private double _gridCellUM = 1.44;
    /// <summary>칸 세로 [m] — 0 이면 가로와 같게(면별 상한까지).</summary>
    [ObservableProperty] private double _gridCellVM;
    [ObservableProperty] private string _gridSummary = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanGrid))] private bool _isGridding;
    /// <summary>적용 뒤 이 층·면 영역마다 정차점 최적화를 바로 돌린다(계산만 — 결과 적용은 최적화 표에서).</summary>
    [ObservableProperty] private bool _gridOptimizeAfter = true;
    public bool CanGrid => !IsGridding && !IsOptimizing && !IsSetCovering;

    partial void OnIsOptimizingChanged(bool value) => OnPropertyChanged(nameof(CanGrid));

    /// <summary>칸 크기 빠른 선택 — "1.44" / "1.08" (가로·세로 같게).</summary>
    [RelayCommand]
    private void SetGridCell(string? size)
    {
        if (!double.TryParse(size, NumberStyles.Float, CultureInfo.InvariantCulture, out var m)) return;
        GridCellUM = m;
        GridCellVM = 0;
    }

    /// <summary>나눈 결과를 미리 본다(DB 무변경).</summary>
    [RelayCommand]
    public async Task PreviewGridAsync()
    {
        if (!CanGrid) return;
        IsGridding = true;
        try
        {
            var r = await _api.GridAreasAsync(TankId, SelectedLevel, SelectedWall, GridCellUM, GridCellVM > 0 ? GridCellVM : null, false);
            GridSummary = r is null ? "관제 서버가 격자 재분할을 지원하지 않습니다(구빌드) — 서버를 재빌드·재시작하세요."
                : "미리보기 — " + DescribeGrid(r.Preview) + (r.Preview.Busy ? "  ⚠ 진행 중 run 에 남아 있어 지금은 적용할 수 없습니다." : "  (영역은 아직 바뀌지 않았습니다)");
        }
        catch (Exception ex) { GridSummary = $"격자 재분할 미리보기 실패: {ex.Message}"; }
        finally { IsGridding = false; }
    }

    /// <summary>격자로 다시 나눠 적용한다(확인 창) — 끝나면 다시 조사하고, 옵션이 켜져 있으면 영역별 정차점 최적화를 돌린다.</summary>
    [RelayCommand]
    public async Task ApplyGridAsync()
    {
        if (!CanGrid) return;
        var (level, wall) = (SelectedLevel, SelectedWall);
        var cellV = GridCellVM > 0 ? GridCellVM : (double?)null;
        IsGridding = true;
        var applied = false;
        try
        {
            var pre = await _api.GridAreasAsync(TankId, level, wall, GridCellUM, cellV, false);
            if (pre is null) { GridSummary = "관제 서버가 격자 재분할을 지원하지 않습니다(구빌드) — 서버를 재빌드·재시작하세요."; return; }
            if (pre.Preview.Busy) { GridSummary = "이 면·층 영역이 진행 중인 run 에 남아 있습니다 — run 이 끝난 뒤 적용하세요."; return; }
            var p = pre.Preview;
            var msg = $"{LevelLabel.Of(level)} {wall} 의 영역을 {Size(p)} 격자로 다시 나눕니다.\n\n"
                      + $"· 영역 {p.AreasBefore}개 → {p.AreasAfter}개 (용접선 {p.Tasks}개)\n"
                      + "· 작업(taskId)·검사 기록은 그대로, 영역 이름은 L{층}-R{행}C{열} 로 바뀝니다.\n"
                      + "· 이 면·층의 정차 덮어쓰기·리프트는 지워집니다(자동 정차점) — 이어서 정차점 최적화로 다시 정하세요.\n"
                      + "· X 영역(못 닿음)의 용접선도 격자 영역에 다시 들어갑니다.\n"
                      + "· 감사로그에 적용 전 배치가 남지만 자동 되돌리기는 없습니다. 적용 뒤 파일 ▸ 저장으로 .hdacs 에 반영하세요.\n\n계속할까요?";
            if (_confirm is not null && !await _confirm.ConfirmAsync(msg, "균등 격자 재분할 적용")) return;

            var r = await _api.GridAreasAsync(TankId, level, wall, GridCellUM, cellV, true);
            if (r?.Result is not { } res) { GridSummary = "적용 결과를 받지 못했습니다 — 계획 ▸ 영역·작업에서 확인하세요."; return; }
            applied = true;
            GridSummary = "적용함 — " + DescribeGrid(r.Preview)
                          + $" (영역 ID 물려받음 {res.Areas.Count(a => a.Inherited)} · 지운 기존 영역 {res.DeletedAreas} · 옮긴 작업 {res.TasksMoved}"
                          + (res.ScenarioLinksAdded > 0 ? $" · 시나리오 연결 추가 {res.ScenarioLinksAdded}" : "") + "). "
                          + (GridOptimizeAfter ? "정차점 최적화를 이어서 돌립니다." : "정차점 최적화로 정차를 다시 정하세요.") + " 파일 ▸ 저장으로 .hdacs 에 반영하세요.";
        }
        catch (Exception ex) { GridSummary = $"격자 재분할 실패(DB 변경 없음): {ex.Message}"; }
        finally { IsGridding = false; }
        if (!applied) return;

        ClearOptima();
        try { ApplyAreaWalls(await _api.GetAreasAsync(TankId)); } catch { /* 면 목록은 그대로 */ }
        await SurveyAsync();
        if (GridOptimizeAfter && SelectedLevel == level && SelectedWall == wall) await OptimizeStationsAsync();
    }

    private static string Size(GridAreasPreviewDto p)
    {
        var ci = CultureInfo.InvariantCulture;
        return $"{p.CellUM.ToString("0.0##", ci)} × {p.CellVM.ToString("0.0##", ci)} m";
    }

    /// <summary>요약 한 줄 — 영역 수 변화·영역 크기·영역당 용접선 수.</summary>
    public static string DescribeGrid(GridAreasPreviewDto p)
    {
        var ci = CultureInfo.InvariantCulture;
        var s = $"{LevelLabel.Of(p.Level)} {p.WallCode} 격자 {Size(p)}: 영역 {p.AreasBefore} → {p.AreasAfter}개 · 용접선 {p.Tasks}개";
        if (p.Cells.Count > 0)
            s += $" · 영역당 용접선 {p.Cells.Min(c => c.TaskCount)}~{p.Cells.Max(c => c.TaskCount)}개"
                 + $" · 영역 크기 최대 {p.Cells.Max(c => c.UMax - c.UMin).ToString("0.00", ci)} × {p.Cells.Max(c => c.VMax - c.VMin).ToString("0.00", ci)} m";
        if (p.LeftoverAreas > 0) s += $" · 칸에 들어가지 않아 따로 묶은 영역 {p.LeftoverAreas}개(이름 G)";
        return s;
    }
}
