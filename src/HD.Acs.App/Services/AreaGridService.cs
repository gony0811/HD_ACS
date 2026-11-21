using System.Text.Json;
using HD.Acs.Core.Planning;
using HD.Acs.Data;
using HD.Acs.Data.Entities;

namespace HD.Acs.App.Services;

/// <summary>격자 재분할 미리보기의 영역 하나 — Leftover = 격자 칸 하나에 들어가지 않아 따로 묶은 용접선.</summary>
public sealed record AreaGridCell(string Name, int Row, int Col, int TaskCount, double UMin, double VMin, double UMax, double VMax, bool Leftover);

/// <summary>격자 재분할 미리보기 — Busy = 진행 중 run 에 남아 있어 지금은 적용할 수 없음.</summary>
public sealed record AreaGridPreview(string TankId, int Level, string WallCode, double CellUM, double CellVM,
    int AreasBefore, int AreasAfter, int Tasks, int LeftoverAreas, bool Busy, List<AreaGridCell> Cells);

/// <summary>
/// 면·층 하나의 영역을 균등 격자(예: 1.44 m, 1.08 × 1.08 m)로 다시 나눠 용접선을 묶는다 —
/// 정차 묶음 최적화(set cover)로 정차마다 흩어 놓은 영역을 격자 영역으로 되돌리고 영역별 정차 최적화를 다시 돌릴 때 쓴다.
/// <list type="bullet">
/// <item>격자: 창 경계는 용접선 끝점 좌표에서만 고른다(가로 작업의 u 끝점·세로 작업의 v 끝점 + 면 가장자리). 앞에서부터 폭이 칸 크기
///   이하가 되게 묶는다(<c>tools/regrid_wall_areas.py</c> 와 같은 규칙) — 용접선이 칸을 가로지르지 않는다.</item>
/// <item>칸 하나에 들어가지 않는 용접선(교차 가지가 경계를 넘는 경우 등)은 칸 크기 창 단위로 따로 묶는다(이름 G).</item>
/// <item>이 면·층의 작업 전부가 대상이다 — X 영역(못 닿음)·임시 제외 작업도 칸에 다시 들어간다(임시 제외 표시는 그대로).</item>
/// <item>정차 덮어쓰기·리프트는 지운다(자동 정차점) — 적용 뒤 영역별 정차 최적화를 다시 돌려 정한다.</item>
/// <item>taskId·검사 기록 유지, 영역 ID 물려주기, 시나리오 연결, 감사로그 <c>AREA_GRID_REGROUP</c> — <see cref="AreaRegroupService"/>.</item>
/// </list>
/// </summary>
public sealed class AreaGridService(AcsDbContext db)
{
    public const double MinCellM = 0.3;
    private const double Edge = 1e-6;

    public async Task<AreaGridPreview> PreviewAsync(string tank, int level, string wallCode, double cellU, double? cellV, CancellationToken ct = default)
    {
        var core = new AreaRegroupService(db);
        var (s, groups, cells, cu, cv) = await BuildAsync(core, tank, level, wallCode, cellU, cellV, ct);
        AreaRegroupService.Shape(s, groups);
        return ToPreview(s, groups, cells, cu, cv, await core.IsBusyAsync(s, ct));
    }

    public async Task<(AreaGridPreview Preview, SetCoverApplyResult Result)> ApplyAsync(string tank, int level, string wallCode,
        double cellU, double? cellV, string? userId, CancellationToken ct = default)
    {
        var core = new AreaRegroupService(db);
        var (s, groups, cells, cu, cv) = await BuildAsync(core, tank, level, wallCode, cellU, cellV, ct);
        var r = await core.ApplyAsync(s, groups, userId, "AREA_GRID_REGROUP", new { cellUM = cu, cellVM = cv }, ct);
        return (ToPreview(s, groups, cells, cu, cv, false), r);
    }

    private static AreaGridPreview ToPreview(RegroupScope s, List<RegroupGroup> groups, List<(int Row, int Col)> cells, double cu, double cv, bool busy) =>
        new(s.Tank, s.Level, s.WallCode, cu, cv, s.Areas.Count, groups.Count, s.Tasks.Count, cells.Count(c => c.Row == 0), busy,
            groups.Select((g, i) => new AreaGridCell(g.Name!, cells[i].Row, cells[i].Col, g.Tasks.Count,
                g.Box.MinU, g.Box.MinV, g.Box.MaxU, g.Box.MaxV, cells[i].Row == 0)).ToList());

    private static async Task<(RegroupScope S, List<RegroupGroup> Groups, List<(int Row, int Col)> Cells, double CellU, double CellV)> BuildAsync(
        AreaRegroupService core, string tank, int level, string wallCode, double cellU, double? cellV, CancellationToken ct)
    {
        var cv = cellV ?? Math.Min(cellU, AreaGeometry.MaxSpanV(wallCode));
        if (!double.IsFinite(cellU) || cellU < MinCellM - Edge || cellU > AreaGeometry.MaxSpanM + Edge)
            throw new AreaSplitException(400, $"칸 가로 크기는 {MinCellM:0.0#}~{AreaGeometry.MaxSpanM:0.0#} m 입니다.");
        if (!double.IsFinite(cv) || cv < MinCellM - Edge || cv > AreaGeometry.MaxSpanV(wallCode) + Edge)
            throw new AreaSplitException(400, $"칸 세로 크기는 {MinCellM:0.0#}~{AreaGeometry.MaxSpanV(wallCode):0.0#} m 입니다(면 {wallCode}).");
        var s = await core.LoadAsync(tank, level, wallCode, ct);
        if (s.Tasks.Count == 0) throw new AreaSplitException(400, $"{tank} L{level} {wallCode} 에 용접선이 없습니다.");

        // 다른 층에도 영역이 있는 면은 이 층 용접선 범위만, 한 층만 쓰는 면(바닥·천장)은 면 전체를 세로 기준으로
        var (cellsOf, leftover) = Cells(s.Tasks, s.Wall.ULen, s.OtherNames.Count == 0 ? s.Wall.VLen : null, cellU, cv);
        var groups = new List<RegroupGroup>();
        var index = new List<(int Row, int Col)>();
        var taken = s.OtherNames.ToHashSet();
        foreach (var c in cellsOf.OrderBy(c => c.Key.Row).ThenBy(c => c.Key.Col))
        {
            var name = $"L{level}-R{c.Key.Row:00}C{c.Key.Col:00}";
            for (var k = 2; !taken.Add(name); k++) name = $"L{level}-R{c.Key.Row:00}C{c.Key.Col:00}-{k}";
            groups.Add(new RegroupGroup(c.Value) { Name = name });
            index.Add(c.Key);
        }
        var n = 0;
        foreach (var chunk in AreaRegroupService.Windows(leftover, cellU, cv))
        {
            string name;
            do name = $"L{level}-G{++n:000}"; while (!taken.Add(name));
            groups.Add(new RegroupGroup(chunk) { Name = name });
            index.Add((0, 0));
        }
        return (s, groups, index, cellU, cv);
    }

    /// <summary>
    /// 용접선을 격자 칸에 나눠 담는다(순수 함수). 칸 번호는 1부터(행 = v 오름차순, 열 = u 오름차순 — 빈 창도 번호를 차지한다).
    /// <paramref name="vLen"/> 을 주면 세로 창을 면 가장자리(0·vLen)부터 잡고, null 이면 용접선이 있는 범위부터 잡는다.
    /// 칸 하나에 들어가지 않는 용접선은 Leftover 로 돌려준다.
    /// </summary>
    public static (Dictionary<(int Row, int Col), List<AreaTaskEntity>> Cells, List<AreaTaskEntity> Leftover) Cells(
        IReadOnlyList<AreaTaskEntity> tasks, double uLen, double? vLen, double cellU, double cellV)
    {
        var ext = tasks.Select(Extent).ToList();
        var bu = new SortedSet<double> { 0, R4(uLen) };
        var bv = vLen is { } vl ? new SortedSet<double> { 0, R4(vl) } : new SortedSet<double> { R4(ext.Min(e => e.V0)), R4(ext.Max(e => e.V1)) };
        foreach (var t in tasks)
        {
            if (Math.Abs(t.StartU - t.EndU) > Edge) { bu.Add(R4(t.StartU)); bu.Add(R4(t.EndU)); }
            if (Math.Abs(t.StartV - t.EndV) > Edge) { bv.Add(R4(t.StartV)); bv.Add(R4(t.EndV)); }
        }
        var uw = Pack(bu.ToList(), cellU);
        var vw = Pack(bv.ToList(), cellV);
        var cells = new Dictionary<(int, int), List<AreaTaskEntity>>();
        var leftover = new List<AreaTaskEntity>();
        const double tol = 1e-4;   // 경계 좌표는 0.1 mm 로 반올림했다
        for (var i = 0; i < tasks.Count; i++)
        {
            var e = ext[i];
            var ci = uw.FindIndex(w => w.Lo - tol <= e.U0 && e.U1 <= w.Hi + tol);
            var cj = vw.FindIndex(w => w.Lo - tol <= e.V0 && e.V1 <= w.Hi + tol);
            if (ci < 0 || cj < 0) { leftover.Add(tasks[i]); continue; }
            if (!cells.TryGetValue((cj + 1, ci + 1), out var list)) cells[(cj + 1, ci + 1)] = list = new List<AreaTaskEntity>();
            list.Add(tasks[i]);
        }
        return (cells, leftover);
    }

    /// <summary>경계 좌표를 앞에서부터 폭이 <paramref name="win"/> 이하가 되게 묶은 창 목록.</summary>
    public static List<(double Lo, double Hi)> Pack(IReadOnlyList<double> breaks, double win)
    {
        var list = new List<(double, double)>();
        for (var i = 0; i < breaks.Count - 1;)
        {
            var j = i + 1;
            while (j + 1 < breaks.Count && breaks[j + 1] - breaks[i] <= win + Edge) j++;
            list.Add((breaks[i], breaks[j]));
            i = j;
        }
        if (list.Count == 0 && breaks.Count > 0) list.Add((breaks[0], breaks[0]));
        return list;
    }

    /// <summary>용접선이 차지하는 면 좌표 범위 — 시작·끝 + 교차 가지.</summary>
    private static (double U0, double V0, double U1, double V1) Extent(AreaTaskEntity t)
    {
        double u0 = Math.Min(t.StartU, t.EndU), u1 = Math.Max(t.StartU, t.EndU), v0 = Math.Min(t.StartV, t.EndV), v1 = Math.Max(t.StartV, t.EndV);
        if (!string.IsNullOrWhiteSpace(t.Points) && JsonSerializer.Deserialize<double[][]>(t.Points) is { } arms)
            foreach (var p in arms.Where(p => p is { Length: >= 2 }))
            {
                u0 = Math.Min(u0, p[0]); u1 = Math.Max(u1, p[0]);
                v0 = Math.Min(v0, p[1]); v1 = Math.Max(v1, p[1]);
            }
        return (u0, v0, u1, v1);
    }

    private static double R4(double x) => Math.Round(x, 4);
}
