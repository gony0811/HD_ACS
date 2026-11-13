using System.Text.Json;
using HD.Acs.App.Services;
using HD.Acs.Core.Geometry;
using HD.Acs.Core.Planning;
using HD.Acs.Core.Reach;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using HD.Acs.Vda5050;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HD.Acs.App.Tests;

/// <summary>현장 실적 대조 — 로그의 실제 정차 자세(맵 좌표)·리프트로 시뮬레이터를 다시 돌려 현장 결과와 비교한다.</summary>
public class ReachFieldCompareTests
{
    private static readonly DrawingTransform Twd = new(10, 5, 0.3);

    private static async Task<(ReachSimService Sim, InspectionAreaEntity Area, InspectionDispatcher Dispatcher, WallEntity Wall)> Seed(
        bool calibrated = true, Action<AcsDbContext, InspectionAreaEntity>? extra = null)
    {
        var db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var geom = new TankGeometry(45, 8.2, Math.PI / 4, 1.9, 5.4, Math.PI / 4, 1.9, new[] { 0.0, 2.4, 4.8, 7.2 });
        db.TankGeometries.Add(new TankGeometryEntity { TankId = "CT1", LevelZ = "[0,2.4,4.8,7.2]" });
        WallEntity? f = null;
        foreach (var w in geom.GenerateWalls())
        {
            var e = new WallEntity
            {
                TankId = "CT1", WallCode = w.WallCode, Origin = JsonSerializer.Serialize(w.Pose.Origin),
                UAxis = JsonSerializer.Serialize(w.Pose.U), VAxis = JsonSerializer.Serialize(w.Pose.V),
                Normal = JsonSerializer.Serialize(w.Normal), ULen = w.ULen, VLen = w.VLen, FacingYaw = w.FacingYaw,
            };
            db.Walls.Add(e);
            if (w.WallCode == "F") f = e;
        }
        static AreaTaskEntity Task(int seq, double u) => new()
        {
            TaskId = Guid.NewGuid(), Seq = seq, StartU = u, StartV = 1.64, EndU = u, EndV = 2.0,
            SectionDxfId = "DXF-1", ProfileId = "PROF-1",
        };
        var area = new InspectionAreaEntity
        {
            AreaId = Guid.NewGuid(), TankId = "CT1", WallCode = "F", Level = 1, Name = "F-01", SortOrder = 0,
            Corners = "[[2.9,1.5],[3.6,1.5],[3.6,2.1],[2.9,2.1]]", StationStandoffM = 1.4,
            Tasks = { Task(1, 3.1), Task(2, 3.3) },
        };
        db.InspectionAreas.Add(area);
        db.Maps.Add(new MapEntity { MapId = "CT1-L1", TankId = "CT1", Level = 1, Name = "L1", Version = 2 });
        if (calibrated)
            db.MapCalibrations.Add(new MapCalibrationEntity { MapId = "CT1-L1", MapVersion = 2, Tx = Twd.Tx, Ty = Twd.Ty, YawRad = Twd.YawRad, PointCount = 3 });
        extra?.Invoke(db, area);
        await db.SaveChangesAsync();

        var dispatcher = new InspectionDispatcher(db, new Vda5050MasterClient("localhost"), new GreedyNearestPolicy(),
            null!, new ConfigurationBuilder().Build(), NullLogger<InspectionDispatcher>.Instance);
        return (new ReachSimService(db, dispatcher), area, dispatcher, f!);
    }

    /// <summary>도면 정차 (x, y, yaw) → AMR 이 보고했을 맵 자세.</summary>
    private static FieldPose MapPose(double x, double y, double yaw)
    {
        var (mx, my) = Twd.DrawingToMap(x, y);
        return new FieldPose(mx, my, Twd.DrawingYawToMap(yaw));
    }

    private static FieldAttempt Attempt(AreaTaskEntity? t, string job, FieldOutcome outcome, FieldPose? arrived, FieldPose? target = null,
        string? step = null) =>
        new("03:49:03", job, t?.TaskId, 1, "F", "LINE", 0, target ?? arrived, arrived, outcome, step, outcome == FieldOutcome.Ok ? null : "사유", "o1");

    [Fact]
    public async Task ArrivedPose_IsConvertedToDrawing_AndJudgedLikeTheSurveyAtThatStation()
    {
        var (sim, area, dispatcher, wall) = await Seed();
        var (sx, sy, yaw) = InspectionDispatcher.StationDrawingOf(area, wall, dispatcher.DefaultStandoffM(wall));
        var tasks = area.Tasks.OrderBy(t => t.Seq).ToList();
        var survey = (await sim.SurveyAsync("CT1", 1, "F", CancellationToken.None, strokeMm: 0))!.Single().Seams;
        Assert.Equal("Clear", survey[0].Verdict);   // 저장된 정차에서는 닿는다(아래 대조의 전제)

        // 벽 안쪽 법선 방향으로 2.1 m 더 물러난 자리(벽에서 3.5 m) — 팔이 닿지 않는다
        var n = JsonSerializer.Deserialize<double[]>(wall.Normal)!;
        var atStation = MapPose(sx, sy, yaw!.Value);
        var far = MapPose(sx + n[0] * 2.1, sy + n[1] * 2.1, yaw.Value);
        var target = MapPose(sx + 0.03, sy - 0.04, yaw.Value + 0.02);

        var r = (await sim.FieldCompareAsync("CT1", new[]
        {
            Attempt(tasks[0], "JOB-CT1-L1-F-F-01-1", FieldOutcome.Ok, atStation, target),          // 성공 · 가능 → 일치
            Attempt(tasks[1], "JOB-CT1-L1-F-F-01-2", FieldOutcome.Reach, far, step: "peak2Approach"), // 도달 실패 · 불가 → 일치
            Attempt(tasks[0], "JOB-CT1-L1-F-F-01-1", FieldOutcome.Ok, far),                         // 성공인데 불가 → 과잉
            Attempt(tasks[0], "JOB-CT1-L1-F-F-01-1", FieldOutcome.Reach, atStation, step: "precheck"), // 도달 실패인데 가능 → 놓침
            Attempt(tasks[0], "JOB-CT1-L1-F-F-01-1", FieldOutcome.Sensor, atStation),                // 센서 실패 → 대조 대상 아님
        }, "F", CancellationToken.None))!;

        var rows = r.Rows;
        Assert.Equal(new[] { "Agree", "Agree", "SimTooStrict", "SimMissed", "NotJudged" }, rows.Select(x => x.Match));
        // 맵 자세가 T_W_D 역변환으로 저장된 정차(도면)와 같은 자리로 돌아온다 → 판정도 그 정차의 조사와 같다
        Assert.Equal((Math.Round(sx, 3), Math.Round(sy, 3)), (rows[0].StationX, rows[0].StationY));
        Assert.Equal(Math.Round(yaw.Value * 180 / Math.PI, 1), rows[0].StationYawDeg);
        Assert.Equal((survey[0].Verdict, survey[0].TraverseMarginDeg), (rows[0].SimVerdict, rows[0].TraverseMarginDeg));
        Assert.Equal("도착", rows[0].PoseSource);
        Assert.Equal((50.0, 1.1), (rows[0].ArrivalDevMm, rows[0].ArrivalDevDeg));   // 지령 대비 3·4 cm, 0.02 rad
        // 지령 자세였다면의 판정도 싣는다 — 도착 자세 판정과 다른 건수가 요약에 나온다(도착 오차가 판정을 바꾸는지)
        Assert.All(rows, x => Assert.NotNull(x.SimVerdictAtTarget));
        Assert.Equal(rows[2].SimVerdict, rows[2].SimVerdictAtTarget);   // 지령 = 도착이면 같은 판정
        Assert.Equal(rows.Count(x => x.SimVerdictAtTarget != x.SimVerdict), r.Summary.ArrivalFlips);
        Assert.Equal("Unreachable", rows[1].SimVerdict);
        Assert.False(string.IsNullOrEmpty(rows[1].SimBlocker));
        Assert.True(rows[1].ReachM > 3.4);

        var s = r.Summary;
        Assert.Equal((5, 0, 2, 1, 2, 1), (s.Rows, s.NotSimulated, s.Ok, s.OkClear, s.Reach, s.ReachCaught));
        Assert.Contains(s.ReachSteps, x => x is { Step: "peak2Approach", Count: 1, Caught: 1 });
        Assert.Contains(s.ReachSteps, x => x is { Step: "precheck", Count: 1, Caught: 0 });
        Assert.Single(s.TooStrict);
        Assert.Contains(s.Cells, c => c is { Outcome: "Sensor", SimVerdict: "Clear", Count: 1 });
        // 건별 표는 머리줄과 칸 수가 같다
        Assert.All(rows, x => Assert.Equal(ReachSimService.FieldCompareCsvHeader.Split(',').Length,
            ReachSimService.FieldCompareCsvRow(x with { Reason = null, SimBlocker = null }).Split(',').Length));
    }

    [Fact]
    public async Task WeldIsFoundByJobRef_WhenLogHasNoTaskId_AndUnknownOrPoselessRowsAreReportedNotGuessed()
    {
        var (sim, area, dispatcher, wall) = await Seed();
        var (sx, sy, yaw) = InspectionDispatcher.StationDrawingOf(area, wall, dispatcher.DefaultStandoffM(wall));
        var pose = MapPose(sx, sy, yaw!.Value);
        var t1 = area.Tasks.Single(t => t.Seq == 1);

        var r = (await sim.FieldCompareAsync("CT1", new[]
        {
            Attempt(null, "JOB-CT1-L1-F-F-01-1", FieldOutcome.Rule, arrived: null, target: pose),   // 주행 전 거부 — 지령 자세로
            Attempt(null, "JOB-CT1-L1-F-NOPE-1", FieldOutcome.Ok, pose),                            // 지금 계획에 없는 용접선
            Attempt(t1, "JOB-CT1-L1-F-F-01-1", FieldOutcome.Ok, arrived: null, target: null),       // 자세 없음
            Attempt(t1, "JOB-CT1-L1-F-F-01-1", FieldOutcome.Ok, pose) with { WallCode = "T" },      // 로그의 면 표기가 달라도 DB 의 면을 따른다
        }, null, CancellationToken.None))!;

        Assert.Equal((t1.TaskId, "jobRef(현재 계획)", "지령", "Clear", "NotJudged"),
            (r.Rows[0].TaskId!.Value, r.Rows[0].ResolvedBy, r.Rows[0].PoseSource, r.Rows[0].SimVerdict, r.Rows[0].Match));
        Assert.Null(r.Rows[0].ArrivalDevMm);
        Assert.Null(r.Rows[1].SimVerdict);
        Assert.Contains("찾지 못했습니다", r.Rows[1].Note);
        Assert.Null(r.Rows[2].SimVerdict);
        Assert.Contains("정차 자세", r.Rows[2].Note);
        Assert.Equal(("F", "taskId"), (r.Rows[3].WallCode, r.Rows[3].ResolvedBy));
        Assert.Equal(2, r.Summary.NotSimulated);
        // 면 필터 — 다른 면을 고르면 그 면 실적만
        Assert.Empty((await sim.FieldCompareAsync("CT1", new[] { Attempt(t1, "J", FieldOutcome.Ok, pose) }, "T", CancellationToken.None))!.Rows);
        Assert.Null(await sim.FieldCompareAsync("NOPE", Array.Empty<FieldAttempt>(), null, CancellationToken.None));
    }

    [Fact]
    public async Task WithoutValidCalibration_RowsAreNotSimulated_InsteadOfUsingRawMapPose()
    {
        var (sim, area, _, _) = await Seed(calibrated: false);
        var t1 = area.Tasks.First();

        var r = (await sim.FieldCompareAsync("CT1", new[] { Attempt(t1, "J", FieldOutcome.Ok, new FieldPose(1, 2, 0)) }, null,
            CancellationToken.None))!;

        Assert.Null(r.Rows[0].SimVerdict);
        Assert.Contains("캘리브레이션", r.Rows[0].Note);
        Assert.Equal(1, r.Summary.NotSimulated);
    }

    [Fact]
    public async Task JobRefWithoutTaskId_FollowsDispatchRecord_AndIsNotGuessedWhenItPointedAtSeveralWelds()
    {
        // 영역을 다시 나눠 jobRef(영역 이름+순번)가 지금은 다른 용접선을 가리키는 경우 — 그때 보낸 배차 기록(work_item 액션)을 따른다
        static WorkItemEntity Sent(InspectionAreaEntity a, string job, Guid taskId) => new()
        {
            WorkItemId = Guid.NewGuid(), RunId = Guid.NewGuid(), AreaId = a.AreaId, MapId = "CT1-L1",
            Actions = JsonSerializer.Serialize(new[] { new { jobRef = job, taskId = taskId.ToString(), actionType = "startWeldInspection" } }),
        };
        var (sim, area, dispatcher, wall) = await Seed(extra: (db, a) =>
        {
            var (t1, t2) = (a.Tasks.Single(t => t.Seq == 1), a.Tasks.Single(t => t.Seq == 2));
            db.WorkItems.Add(Sent(a, "JOB-CT1-L1-F-F-01-1", t2.TaskId));   // 그때는 순번 1 이 지금의 2번 용접선이었다
            db.WorkItems.Add(Sent(a, "JOB-CT1-L1-F-OLD-1", t1.TaskId));
            db.WorkItems.Add(Sent(a, "JOB-CT1-L1-F-OLD-1", t2.TaskId));   // 같은 jobRef 가 두 용접선으로 나갔다
        });
        var (sx, sy, yaw) = InspectionDispatcher.StationDrawingOf(area, wall, dispatcher.DefaultStandoffM(wall));
        var pose = MapPose(sx, sy, yaw!.Value);

        var r = (await sim.FieldCompareAsync("CT1", new[]
        {
            Attempt(null, "JOB-CT1-L1-F-F-01-1", FieldOutcome.Reach, pose),
            Attempt(null, "JOB-CT1-L1-F-OLD-1", FieldOutcome.Reach, pose),
        }, null, CancellationToken.None))!;

        Assert.Equal((area.Tasks.Single(t => t.Seq == 2).TaskId, 2, "jobRef(배차 기록)"),
            (r.Rows[0].TaskId!.Value, r.Rows[0].Seq!.Value, r.Rows[0].ResolvedBy));
        Assert.Null(r.Rows[1].SimVerdict);
        Assert.Contains("용접선 2개", r.Rows[1].Note);
        Assert.Equal("NotJudged", r.Rows[1].Match);
    }
}
