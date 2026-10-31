using System.Text.Json;
using HD.Acs.Core.Geometry;
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
    private readonly ReachRobotModel _model = ReachRobotModel.Default;

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
    public async Task<IReadOnlyList<ReachSurveyArea>?> SurveyAsync(string tankId, int? level, string? wallCode, CancellationToken ct)
    {
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
        double offsetForwardMm, double offsetLeftMm, int yawIndex, CancellationToken ct)
    {
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
