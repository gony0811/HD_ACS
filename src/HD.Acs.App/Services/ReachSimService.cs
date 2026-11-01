using System.Text.Json;
using HD.Acs.Core.Geometry;
using HD.Acs.Core.Planning;
using HD.Acs.Core.Reach;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HD.Acs.App.Services;

/// <summary>
/// 리치·간섭 시뮬레이션 조회 — 영역 정차(배차와 같은 <see cref="InspectionDispatcher.StationDrawingOf"/>)에서 FR5-WML 이
/// 용접선에 세 자세(② 400 → 정렬 준비 300 → 초점 0)로 접근할 때 관절 위치와 탱크 면 간섭을 계산한다.
/// 계산은 <see cref="ReachSimulator"/>(Core)가 하고, 여기서는 DB 의 선창·면·영역·작업을 도면 좌표로 엮는다.
/// 조회 전용 — 코봇·DB 를 바꾸지 않는다.
/// </summary>
public sealed class ReachSimService
{
    /// <summary>리프트 조사 간격 [mm] — 0~스트로크 최대.</summary>
    public const double StrokeStepMm = 100;

    private readonly AcsDbContext _db;
    private readonly InspectionDispatcher _dispatcher;
    private ReachRobotModel _model = ReachRobotModel.Default;

    public ReachSimService(AcsDbContext db, InspectionDispatcher dispatcher)
    {
        _db = db;
        _dispatcher = dispatcher;
    }

    private IReadOnlyList<double> Strokes => Enumerable.Range(0, (int)(_model.StrokeMaxMm / StrokeStepMm) + 1)
        .Select(i => i * StrokeStepMm).ToArray();

    /// <summary>
    /// 선창(층·면 필터)의 용접선마다 정차·리프트를 훑어 간섭 없이 닿는지 판정한다. 선창이 없으면 null.
    /// </summary>
    public async Task<IReadOnlyList<ReachSurveyArea>?> SurveyAsync(string tankId, int? level, string? wallCode, CancellationToken ct,
        ReachBranchRule branch = ReachBranchRule.MaxMargin)
    {
        _model = ReachRobotModel.Default with { BranchRule = branch };
        var ctx = await LoadTankAsync(tankId, ct);
        if (ctx is null) return null;
        var q = _db.InspectionAreas.AsNoTracking().Include(a => a.Tasks).Where(a => a.TankId == tankId);
        if (level is { } lv) q = q.Where(a => a.Level == lv);
        if (wallCode is { Length: > 0 }) q = q.Where(a => a.WallCode == wallCode);
        var areas = await q.OrderBy(a => a.Level).ThenBy(a => a.WallCode).ThenBy(a => a.Name).ToListAsync(ct);

        var list = new List<ReachSurveyArea>(areas.Count);
        foreach (var a in areas)
        {
            if (!ctx.Walls.TryGetValue(a.WallCode, out var wall)) continue;
            var setup = Setup(ctx, a, wall);
            var seams = a.Tasks.OrderBy(t => t.Seq).Select(t =>
            {
                var pts = SeamPoints(setup.Pose, t);
                var s = ReachSimulator.Survey(_model, setup.Planes, setup.Stations, pts, setup.Normal, Strokes);
                return new ReachSurveySeam(t.TaskId, t.Seq,
                    Math.Round(pts.Min(p => p[2]) - setup.FloorZ, 3), Math.Round(pts.Max(p => p[2]) - setup.FloorZ, 3),
                    s.Verdict.ToString(), s.StrokeMm, s.MinClearanceM is { } c ? Math.Round(c, 4) : null, s.YawIndex);
            }).ToList();
            list.Add(new ReachSurveyArea(a.AreaId, a.Name, a.WallCode, a.Level, setup.Stations[0].X, setup.Stations[0].Y, seams));
        }
        return list;
    }

    /// <summary>
    /// 용접선 점 하나(0 시작 · 1 중간 · 2 끝)를 세 자세로 풀어 IK 해 전부·부품 간섭을 돌려준다.
    /// strokeMm 이 없으면 조사로 고른 리프트(없으면 0)를 쓴다. 영역·작업이 없으면 null.
    /// </summary>
    public async Task<ReachDetail?> DetailAsync(Guid areaId, Guid taskId, int point, double? strokeMm,
        double offsetForwardMm, double offsetLeftMm, int yawIndex, CancellationToken ct,
        ReachBranchRule branch = ReachBranchRule.MaxMargin)
    {
        _model = ReachRobotModel.Default with { BranchRule = branch };
        var area = await _db.InspectionAreas.AsNoTracking().Include(a => a.Tasks).FirstOrDefaultAsync(a => a.AreaId == areaId, ct);
        var task = area?.Tasks.FirstOrDefault(t => t.TaskId == taskId);
        if (area is null || task is null) return null;
        var ctx = await LoadTankAsync(area.TankId, ct);
        if (ctx is null || !ctx.Walls.TryGetValue(area.WallCode, out var wall)) return null;

        var setup = Setup(ctx, area, wall);
        var pts = SeamPoints(setup.Pose, task);
        yawIndex = Math.Clamp(yawIndex, 0, setup.Stations.Count - 1);
        var surveyed = strokeMm is null
            ? ReachSimulator.Survey(_model, setup.Planes, new[] { setup.Stations[yawIndex] }, pts, setup.Normal, Strokes)
            : null;
        var h = Math.Clamp(strokeMm ?? surveyed?.StrokeMm ?? 0, 0, _model.StrokeMaxMm);
        var r = ReachSimulator.Evaluate(_model, setup.Planes, setup.Stations[yawIndex], pts[Math.Clamp(point, 0, 2)],
            setup.Normal, h, offsetForwardMm, offsetLeftMm);

        var seams = area.Tasks.OrderBy(t => t.Seq).Select(t =>
        {
            var p = SeamPoints(setup.Pose, t);
            return new ReachSeamLine(t.TaskId, t.Seq, p[0], p[2]);
        }).ToList();
        var st = setup.Stations[yawIndex];
        return new ReachDetail(area.AreaId, area.Name, area.WallCode, area.Level, setup.FloorZ,
            st.X, st.Y, st.Yaw, setup.Stations.Count, yawIndex, h, offsetForwardMm, offsetLeftMm,
            task.TaskId, Math.Clamp(point, 0, 2), pts[Math.Clamp(point, 0, 2)], seams,
            new ReachBodyDto(r.Body.Corners, r.Body.ClearanceM, r.Body.NearestPlane),
            r.Stages.Select(s => new ReachStageDto(s.Stage.ToString(), s.StandoffMm, s.TcpTarget, s.ChosenIndex,
                s.Solutions.Select(x => new ReachSolutionDto(x.JointsDeg, x.PostureOk, x.MarginDeg, x.Limiting, x.WristDown,
                    x.MinClearanceM, x.Focus,
                    x.Parts.Select(p => new ReachPartDto(p.Name, p.A, p.B, p.RadiusM, p.ClearanceM, p.NearestPlane)).ToList(),
                    x.Joints.Select(j => new ReachJointDto(j.Name, j.Position)).ToList())).ToList())).ToList(),
            _model.WarnClearanceM);
    }

    /// <summary>
    /// 영역 정차점 최적화(조회 전용) — 지금 정차에서 시작해 거리·좌우·방향을 옮겨 가며 간섭 없이 닿는 용접선이 가장 많은 정차를 찾는다.
    /// 바닥·천장(정면 방향 없음)과 용접선 없는 영역은 건너뛴다. 선창이 없으면 null.
    /// </summary>
    public async Task<IReadOnlyList<ReachStationOptimum>?> OptimizeAsync(string tankId, int? level, string? wallCode, Guid? areaId,
        CancellationToken ct, ReachBranchRule branch = ReachBranchRule.MaxMargin)
    {
        _model = ReachRobotModel.Default with { BranchRule = branch };
        var ctx = await LoadTankAsync(tankId, ct);
        if (ctx is null) return null;
        var q = _db.InspectionAreas.AsNoTracking().Include(a => a.Tasks).Where(a => a.TankId == tankId);
        if (areaId is { } id) q = q.Where(a => a.AreaId == id);
        if (level is { } lv) q = q.Where(a => a.Level == lv);
        if (wallCode is { Length: > 0 }) q = q.Where(a => a.WallCode == wallCode);
        var areas = await q.OrderBy(a => a.Level).ThenBy(a => a.WallCode).ThenBy(a => a.Name).ToListAsync(ct);

        var list = new List<ReachStationOptimum>();
        foreach (var a in areas)
        {
            if (!ctx.Walls.TryGetValue(a.WallCode, out var wall) || wall.FacingYaw is null || a.Tasks.Count == 0) continue;
            var setup = Setup(ctx, a, wall);
            if (SearchSpace(a, wall, setup) is not { } space) continue;
            var seams = a.Tasks.OrderBy(t => t.Seq).Select(t => (IReadOnlyList<double[]>)SeamPoints(setup.Pose, t)).ToList();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = StationOptimizer.Optimize(_model, setup.Planes, space, seams, setup.Normal, Strokes, ct);
            var tasks = a.Tasks.OrderBy(t => t.Seq).ToList();
            var perSeam = tasks.Select((t, i) => new ReachOptimumSeam(t.TaskId, t.Seq,
                i < r.Baseline.Seams.Count ? r.Baseline.Seams[i].Verdict.ToString() : nameof(ReachVerdict.Unreachable),
                i < r.Best.Seams.Count ? r.Best.Seams[i].Verdict.ToString() : nameof(ReachVerdict.Unreachable),
                i < r.Best.Seams.Count ? r.Best.Seams[i].StrokeMm : null)).ToList();
            list.Add(new ReachStationOptimum(a.AreaId, a.Name, a.WallCode, a.Level,
                a.StationX is not null || a.StationY is not null || a.StationTheta is not null,
                Candidate(r.Baseline), Candidate(r.Best), r.Evaluated, sw.ElapsedMilliseconds, perSeam));
        }
        return list;
    }

    /// <summary>
    /// 탐색 좌표계 — 영역 중심(수평)·면 안쪽 수평 방향. 지금 정차(덮어쓰기 포함)를 그 좌표로 바꿔 시작점으로 둔다.
    /// 법선에 수평 성분이 없으면(바닥·천장) null.
    /// </summary>
    private static StationSearchSpace? SearchSpace(InspectionAreaEntity area, WallEntity wall, AreaSetup setup)
    {
        var nx = setup.Normal[0];
        var ny = setup.Normal[1];
        var len = Math.Sqrt(nx * nx + ny * ny);
        if (len < 1e-6) return null;
        nx /= len;
        ny /= len;
        var (uc, vc) = AreaGeometry.Centroid(Json<double[][]>(area.Corners));
        var c = setup.Pose.LocalToDrawing(uc, vc);
        var st = setup.Stations[0];
        var dx = st.X - c[0];
        var dy = st.Y - c[1];
        var facing = wall.FacingYaw!.Value;
        var dyaw = Math.Atan2(Math.Sin(st.Yaw - facing), Math.Cos(st.Yaw - facing)) * 180 / Math.PI;
        return new StationSearchSpace(new[] { c[0], c[1] }, new[] { nx, ny }, facing, setup.FloorZ,
            dx * nx + dy * ny, -dx * ny + dy * nx, dyaw);
    }

    private static ReachStationCandidate Candidate(StationCandidate c) => new(
        Math.Round(c.Station.X, 3), Math.Round(c.Station.Y, 3), Math.Round(c.Station.Yaw, 4),
        Math.Round(c.DistM, 3), Math.Round(c.LateralM, 3), Math.Round(c.YawDeg, 1),
        Math.Round(c.BodyClearanceM, 3), c.Feasible, c.Clear, c.Collision, c.Unreachable, Math.Round(c.Robustness, 4));

    // ── 내부 ─────────────────────────────────────────────────────────────

    private sealed record TankCtx(double[] LevelZ, Dictionary<string, WallEntity> Walls, ReachPlane[] WallPlanes);

    private sealed record AreaSetup(WallPose Pose, double[] Normal, double FloorZ,
        IReadOnlyList<ReachPlane> Planes, IReadOnlyList<ReachStation> Stations);

    private async Task<TankCtx?> LoadTankAsync(string tankId, CancellationToken ct)
    {
        var g = await _db.TankGeometries.AsNoTracking().FirstOrDefaultAsync(x => x.TankId == tankId, ct);
        if (g is null) return null;
        var walls = await _db.Walls.AsNoTracking().Where(w => w.TankId == tankId).ToDictionaryAsync(w => w.WallCode, ct);
        var planes = walls.Values.Select(w => new ReachPlane(w.WallCode, Json<double[]>(w.Origin), Json<double[]>(w.Normal))).ToArray();
        return new TankCtx(Json<double[]>(g.LevelZ), walls, planes);
    }

    /// <summary>영역의 정차 후보 — 배차와 같은 정차점. 정차 방향이 없는 바닥·천장은 네 방향을 후보로 둔다.</summary>
    private AreaSetup Setup(TankCtx ctx, InspectionAreaEntity area, WallEntity wall)
    {
        var floorZ = area.Level >= 1 && area.Level <= ctx.LevelZ.Length ? ctx.LevelZ[area.Level - 1] : 0;
        var (x, y, yaw) = InspectionDispatcher.StationDrawingOf(area, wall, _dispatcher.DefaultStandoffM(wall));
        var yaws = yaw is { } v ? new[] { v } : new[] { 0, Math.PI / 2, Math.PI, -Math.PI / 2 };
        var pose = new WallPose(Json<double[]>(wall.Origin), Json<double[]>(wall.UAxis), Json<double[]>(wall.VAxis));
        return new AreaSetup(pose, Json<double[]>(wall.Normal), floorZ,
            ReachSimulator.PlanesForLevel(ctx.WallPlanes, floorZ, area.Level),
            yaws.Select(t => new ReachStation(x, y, t, floorZ)).ToArray());
    }

    /// <summary>용접선 시작·중간·끝(도면 m).</summary>
    private static double[][] SeamPoints(WallPose pose, AreaTaskEntity t)
    {
        var a = pose.LocalToDrawing(t.StartU, t.StartV);
        var b = pose.LocalToDrawing(t.EndU, t.EndV);
        return new[] { a, Vec3.Scale(Vec3.Add(a, b), 0.5), b };
    }

    private static T Json<T>(string s) => JsonSerializer.Deserialize<T>(s)!;
}

/// <summary>조사 결과 — 영역 하나.</summary>
public sealed record ReachSurveyArea(Guid AreaId, string Name, string WallCode, int Level, double StationX, double StationY,
    IReadOnlyList<ReachSurveySeam> Seams);

/// <summary>조사 결과 — 용접선 하나. Z 는 층 바닥 위 높이[m], Verdict = Clear | Collision | Unreachable.</summary>
public sealed record ReachSurveySeam(Guid TaskId, int Seq, double ZMin, double ZMax, string Verdict,
    double? StrokeMm, double? MinClearanceM, int YawIndex);

/// <summary>정차점 최적화 결과 — 영역 하나. Overridden = 이미 정차 덮어쓰기가 있음.</summary>
public sealed record ReachStationOptimum(Guid AreaId, string Name, string WallCode, int Level, bool Overridden,
    ReachStationCandidate Current, ReachStationCandidate Best, int Evaluated, long ElapsedMs, IReadOnlyList<ReachOptimumSeam> Seams);

/// <summary>용접선 하나의 지금·찾은 정차 판정과 찾은 정차에서 고른 리프트.</summary>
public sealed record ReachOptimumSeam(Guid TaskId, int Seq, string Current, string Best, double? BestStrokeMm);

/// <summary>후보 정차 — 도면 x·y·방향, 탐색 좌표(중심에서 안쪽 거리·좌측·방향 보정 °), 차체 여유, 판정 개수.</summary>
public sealed record ReachStationCandidate(double X, double Y, double Theta, double DistM, double LateralM, double YawDeg,
    double BodyClearanceM, bool Feasible, int Clear, int Collision, int Unreachable, double Robustness);

public sealed record ReachSeamLine(Guid TaskId, int Seq, double[] Start, double[] End);
public sealed record ReachBodyDto(double[][] Corners, double ClearanceM, string NearestPlane);
public sealed record ReachPartDto(string Name, double[] A, double[] B, double RadiusM, double ClearanceM, string NearestPlane);
public sealed record ReachJointDto(string Name, double[] Position);
public sealed record ReachSolutionDto(double[] JointsDeg, bool PostureOk, double MarginDeg, string Limiting, bool WristDown,
    double MinClearanceM, double[] Focus, IReadOnlyList<ReachPartDto> Parts, IReadOnlyList<ReachJointDto> Joints);
public sealed record ReachStageDto(string Stage, double StandoffMm, double[] TcpTarget, int ChosenIndex,
    IReadOnlyList<ReachSolutionDto> Solutions);

/// <summary>상세 — 정차(도면)·리프트·정차 이동·대상 점·영역 용접선·차체·세 자세. 좌표는 도면 m.</summary>
public sealed record ReachDetail(
    Guid AreaId, string AreaName, string WallCode, int Level, double FloorZ,
    double StationX, double StationY, double StationYaw, int YawCount, int YawIndex,
    double StrokeMm, double OffsetForwardMm, double OffsetLeftMm,
    Guid TaskId, int Point, double[] Target, IReadOnlyList<ReachSeamLine> Seams,
    ReachBodyDto Body, IReadOnlyList<ReachStageDto> Stages, double WarnClearanceM);
