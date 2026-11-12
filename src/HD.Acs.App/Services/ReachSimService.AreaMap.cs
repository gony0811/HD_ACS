using System.Text.Json;
using HD.Acs.Core.Reach;
using Microsoft.EntityFrameworkCore;

namespace HD.Acs.App.Services;

/// <summary>
/// 영역 검사 가능 지도 — 영역마다 "운영 조건에서 용접선이 전부 검사 가능한가"를 판정한다(조회 전용).
/// 운영 조건 = 배차와 같은 정차점 + 저장된 영역 리프트(없으면 0) — 보고서(<see cref="ReportAsync"/>)와 같은 조건.
/// 용접선 하나라도 간섭 없이 닿지 못하면 그 영역은 불가. 임시 제외(enabled = false) 용접선은 배차가 싣지 않으므로 보지 않는다.
/// </summary>
public sealed partial class ReachSimService
{
    public async Task<IReadOnlyList<ReachAreaVerdict>?> AreaMapAsync(string tankId, int? level, string? wallCode, CancellationToken ct)
    {
        _model = _baseModel;   // 운영 가지 규칙
        var ctx = await LoadTankAsync(tankId, ct);
        if (ctx is null) return null;
        var q = _db.InspectionAreas.AsNoTracking().Include(a => a.Tasks).Where(a => a.TankId == tankId);
        if (level is { } lv) q = q.Where(a => a.Level == lv);
        if (wallCode is { Length: > 0 }) q = q.Where(a => a.WallCode == wallCode);
        var areas = await q.OrderBy(a => a.Level).ThenBy(a => a.WallCode).ThenBy(a => a.Name).ToListAsync(ct);

        var list = new List<ReachAreaVerdict>(areas.Count);
        foreach (var a in areas)
        {
            ct.ThrowIfCancellationRequested();
            var corners = JsonSerializer.Deserialize<double[][]>(a.Corners) ?? Array.Empty<double[]>();
            var tasks = a.Tasks.Where(t => t.Enabled).OrderBy(t => t.Seq).ToList();
            var disabled = a.Tasks.Count - tasks.Count;
            var lift = Math.Clamp(a.StationLiftMm ?? 0, 0, _model.StrokeMaxMm);
            // 임시 제외 용접선은 판정 없이(Ok = null) 위치만 싣는다
            var off = a.Tasks.Where(t => !t.Enabled).OrderBy(t => t.Seq)
                .Select(t => new ReachWeldVerdict(t.TaskId, t.Seq, t.StartU, t.StartV, t.EndU, t.EndV, null)).ToList();
            if (!ctx.Walls.TryGetValue(a.WallCode, out var wall) || tasks.Count == 0)
            {
                list.Add(new ReachAreaVerdict(a.AreaId, a.Name, a.WallCode, a.Level, corners, 0, 0, disabled, null, lift, Array.Empty<int>(), off));
                continue;
            }
            var setup = Setup(ctx, a, wall);
            var verdicts = new ReachVerdict[tasks.Count];
            Parallel.For(0, tasks.Count, new ParallelOptions { CancellationToken = ct }, i =>
                verdicts[i] = ReachSimulator.Survey(_model, setup.Planes, setup.Stations, SeamPoints(setup.Pose, tasks[i]),
                    setup.Normal, new[] { lift }, a.WallCode).Verdict);
            var bad = tasks.Where((_, i) => verdicts[i] != ReachVerdict.Clear).Select(t => t.Seq).ToArray();
            var welds = tasks.Select((t, i) => new ReachWeldVerdict(t.TaskId, t.Seq, t.StartU, t.StartV, t.EndU, t.EndV,
                verdicts[i] == ReachVerdict.Clear)).Concat(off).OrderBy(x => x.Seq).ToList();
            list.Add(new ReachAreaVerdict(a.AreaId, a.Name, a.WallCode, a.Level, corners,
                tasks.Count, tasks.Count - bad.Length, disabled, bad.Length == 0, lift, bad, welds));
        }
        return list;
    }
}

/// <summary>
/// 영역 하나의 검사 가능 판정. Possible = 검사 대상 용접선이 전부 가능(null = 검사 대상 용접선 없음).
/// Corners = 면 (u, v) [m], Welds = 검사 대상(임시 제외를 뺀) 용접선 수, Ok = 그중 가능 수, BadSeqs = 불가 용접선 순번.
/// </summary>
public sealed record ReachAreaVerdict(Guid AreaId, string Name, string WallCode, int Level, double[][] Corners,
    int Welds, int Ok, int Disabled, bool? Possible, double LiftMm, int[] BadSeqs, IReadOnlyList<ReachWeldVerdict> Seams);

/// <summary>용접선 하나의 검사 가능 판정 — 면 (u, v) [m] 시작·끝, Ok = 가능 여부(null = 임시 제외라 판정하지 않음).</summary>
public sealed record ReachWeldVerdict(Guid TaskId, int Seq, double StartU, double StartV, double EndU, double EndV, bool? Ok);
