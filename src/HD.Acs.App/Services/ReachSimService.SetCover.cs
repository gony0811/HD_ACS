using HD.Acs.Core.Geometry;
using HD.Acs.Core.Planning;
using HD.Acs.Core.Reach;
using HD.Acs.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HD.Acs.App.Services;

/// <summary>
/// 정차 묶음 최적화(탐욕 set cover) — 면·층 하나의 용접선을 모두 덮는 정차 수를 줄이는 제안(조회 전용, DB 를 바꾸지 않는다).
/// 계산은 <see cref="StationSetCover"/>. 정차 하나 = 영역 하나(리프트 하나)이고, 정차가 맡는 용접선은 AREA 최대 크기 창 하나에 들어간다.
/// 지금 배치(영역 정차·저장 리프트)의 정차 수·간섭 없이 닿는 용접선 수를 함께 싣는다(비교용).
/// </summary>
public sealed partial class ReachSimService
{
    public async Task<ReachSetCoverPlan?> SetCoverAsync(string tankId, int level, string wallCode, SetCoverOptions? options,
        CancellationToken ct, IProgress<(int Done, int Total)>? progress = null)
    {
        _model = await BaseAsync(tankId, ct);   // 운영 가지 규칙
        var ctx = await LoadTankAsync(tankId, ct);
        if (ctx is null || !ctx.Walls.TryGetValue(wallCode, out var wall)) return null;
        var areas = await _db.InspectionAreas.AsNoTracking().Include(a => a.Tasks)
            .Where(a => a.TankId == tankId && a.Level == level && a.WallCode == wallCode).ToListAsync(ct);
        var tasks = areas.SelectMany(a => a.Tasks).Where(t => t.Enabled).OrderBy(t => t.StartV).ThenBy(t => t.StartU).ToList();
        var floorZ = level >= 1 && level <= ctx.LevelZ.Length ? ctx.LevelZ[level - 1] : 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var current = await CurrentLayoutAsync(tankId, level, wallCode, ct);
        if (tasks.Count == 0)
            return new ReachSetCoverPlan(tankId, level, wallCode, 0, 0, 0, current.Stations, current.Clear,
                new List<ReachSetCoverStation>(), new List<Guid>(), 0, 0, sw.ElapsedMilliseconds, Options(options));

        foreach (var a in areas) _areaWall[a.AreaId] = wallCode;
        var pose = new WallPose(Json<double[]>(wall.Origin), Json<double[]>(wall.UAxis), Json<double[]>(wall.VAxis));
        var normal = Json<double[]>(wall.Normal);
        var planes = ReachSimulator.PlanesForLevel(ctx.WallPlanes, floorZ, level);
        var seams = tasks.Select(t => (IReadOnlyList<double[]>)SeamPoints(pose, t)).ToList();
        var uv = tasks.Select(t => (IReadOnlyList<double[]>)TaskUv(t)).ToList();
        if (WallSpace(wall, pose, normal, floorZ, seams) is not { } space) return null;

        var plan = StationSetCover.Plan(_model, planes, space, seams, uv, AreaGeometry.MaxSpanM, AreaGeometry.MaxSpanV(wallCode),
            normal, Strokes, wallCode, options, progress, ct);
        var stations = plan.Stations
            .OrderBy(s => s.Window[1]).ThenBy(s => s.Window[0])
            .Select((s, k) =>
            {
                var members = s.Seams.Select(i => tasks[i]).ToList();
                return new ReachSetCoverStation(k + 1,
                    Math.Round(s.Station.X, 3), Math.Round(s.Station.Y, 3), Math.Round(s.Station.Yaw, 4), s.StrokeMm, s.Tier,
                    Round1(s.MinMarginDeg), s.Window.Select(v => Math.Round(v, 3)).ToArray(),
                    members.Select(t => t.TaskId).ToList(),
                    members.Select(t => new ReachWeldVerdict(t.TaskId, t.Seq, t.StartU, t.StartV, t.EndU, t.EndV, true)).ToList());
            }).ToList();
        return new ReachSetCoverPlan(tankId, level, wallCode, tasks.Count, plan.SafeCovered, plan.ClearCovered,
            current.Stations, current.Clear, stations,
            plan.Uncovered.Select(i => tasks[i].TaskId).ToList(), plan.Cells, plan.Evaluations, sw.ElapsedMilliseconds,
            Options(options),
            plan.Uncovered.Select(i => tasks[i]).Select(t => new ReachWeldVerdict(t.TaskId, t.Seq, t.StartU, t.StartV, t.EndU, t.EndV, false)).ToList());
    }

    private static SetCoverOptions Options(SetCoverOptions? o) => o ?? SetCoverOptions.Default;

    /// <summary>지금 배치 — 검사 대상 용접선이 있는 영역 수(= 정차 수)와 그 정차·저장 리프트에서 간섭 없이 닿는 용접선 수.</summary>
    private async Task<(int Stations, int Clear)> CurrentLayoutAsync(string tankId, int level, string wallCode, CancellationToken ct)
    {
        var map = await AreaMapAsync(tankId, level, wallCode, ct) ?? new List<ReachAreaVerdict>();
        return (map.Count(a => a.Welds > 0), map.Sum(a => a.Ok));
    }

    /// <summary>
    /// 면 전체 탐색 좌표 — 중심 = 용접선 중심(수평), 면 안쪽 수평 방향·정면 방향. 바닥·천장(법선이 수직)은 면 U축 수평 방향을 기준으로
    /// 전 방향(±180°)을 본다(MinDistM &lt; 0 표식).
    /// </summary>
    private StationSearchSpace? WallSpace(WallEntity wall, WallPose pose, double[] normal, double floorZ, IReadOnlyList<IReadOnlyList<double[]>> seams)
    {
        var pts = seams.SelectMany(s => s).ToList();
        var c = new[] { pts.Average(p => p[0]), pts.Average(p => p[1]) };
        var len = Math.Sqrt(normal[0] * normal[0] + normal[1] * normal[1]);
        if (len < 1e-6 || wall.FacingYaw is null)
        {
            var ulen = Math.Sqrt(pose.U[0] * pose.U[0] + pose.U[1] * pose.U[1]);
            if (ulen < 1e-6) return null;
            var u = new[] { pose.U[0] / ulen, pose.U[1] / ulen };
            return new StationSearchSpace(c, u, Math.Atan2(u[1], u[0]), floorZ, 0,
                MinDistM: -1, MaxDistM: 1, MaxYawDeg: 180, YawStepDeg: 90);
        }
        return new StationSearchSpace(c, new[] { normal[0] / len, normal[1] / len }, wall.FacingYaw.Value, floorZ, 0,
            MinWallDistM: _wallMinDistM.GetValueOrDefault(wall.WallCode));
    }
}

/// <summary>
/// 정차 묶음 최적화(set cover) 결과 — 면·층 하나. Welds = 검사 대상 용접선 수, SafeCovered = 순회 여유 목표 이상으로 덮은 수,
/// ClearCovered = 간섭 없이(여유 부족) 덮은 수, CurrentStations·CurrentClear = 지금 배치의 정차 수·간섭 없이 닿는 수,
/// Uncovered = 어느 후보로도 간섭 없이 닿지 못한 작업. Cells·Evaluations = 정차 후보 수·푼 판정 수.
/// </summary>
public sealed record ReachSetCoverPlan(string TankId, int Level, string WallCode, int Welds, int SafeCovered, int ClearCovered,
    int CurrentStations, int CurrentClear, List<ReachSetCoverStation> Stations, List<Guid> Uncovered,
    int Cells, long Evaluations, long ElapsedMs, SetCoverOptions Options, List<ReachWeldVerdict>? UncoveredSeams = null);

/// <summary>고른 정차 하나 — 순번·도면 정차·리프트·Tier(Safe|Clear)·순회 최소 여유·묶음 창[u0, v0, u1, v1]·맡은 작업.</summary>
public sealed record ReachSetCoverStation(int No, double X, double Y, double Theta, double StrokeMm, string Tier, double? MinMarginDeg,
    double[] Window, List<Guid> TaskIds, List<ReachWeldVerdict> Seams);
