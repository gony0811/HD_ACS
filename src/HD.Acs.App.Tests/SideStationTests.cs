using System.Text.Json;
using System.Text.Json.Nodes;
using HD.Acs.App.Services;
using HD.Acs.Core.Geometry;
using HD.Acs.Core.Planning;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using HD.Acs.Vda5050;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HD.Acs.App.Tests;

/// <summary>측면 정차 검사 [VDA §4.4.3 / N25] — 층+벽면 설정·배차 theta·좌우·고정 이격·시험 Order·켜기 설정.</summary>
public class SideStationTests
{
    // ── 순수 규칙 ────────────────────────────────────────────────────────────
    [Theory]
    [InlineData(-3.0, 0.0, "U_PLUS", 0.0)]          // +x 로 다가옴 → +x 방향
    [InlineData(3.0, 0.0, "U_PLUS", Math.PI)]        // −x 로 다가옴 → −x 방향
    [InlineData(-2.0, -2.5, "U_MINUS", 0.0)]         // 접선과 51° — 이동 방향 따름
    [InlineData(0.0, -3.0, "U_MINUS", Math.PI)]      // 벽과 수직으로 다가옴 → 기본 방향(U_MINUS)
    [InlineData(-0.1, 0.0, "U_PLUS", 0.0)]           // 바로 옆(0.3 m 미만) → 기본 방향
    [InlineData(-0.1, 0.0, "U_MINUS", Math.PI)]
    public void ChooseHeading_FollowsTravel_OrDefault(double rdx, double rdy, string dir, double expected)
    {
        var th = SideStation.ChooseHeading(0.0, 10, 5, 10 + rdx, 5 + rdy, dir);
        Assert.Equal(0, SideStation.Normalize(th - expected), 9);
    }

    [Fact]
    public void ChooseHeading_UnknownRobot_UsesDefault()
        => Assert.Equal(Math.PI, SideStation.ChooseHeading(0.0, 1, 1, null, null, "U_MINUS"), 9);

    /// <summary>벽이 +y 쪽(안쪽 법선 −y)일 때 +x 로 서면 벽은 왼쪽, −x 로 서면 오른쪽.</summary>
    [Fact]
    public void SideOf_WallOnLeftOrRight()
    {
        Assert.Equal("SIDE_LEFT", SideStation.SideOf(0.0, -Math.PI / 2));
        Assert.Equal("SIDE_RIGHT", SideStation.SideOf(Math.PI, -Math.PI / 2));
        Assert.Equal("SIDE_RIGHT", SideStation.SideOf(0.0, Math.PI / 2));
        Assert.Equal(90, SideStation.DeviationFromTangentDeg(Math.PI / 2, 0), 6);
        Assert.Equal(0, SideStation.DeviationFromTangentDeg(Math.PI, 0), 6);
    }

    [Theory]
    [InlineData("PM", 1, "SIDE", null, null, null)]
    [InlineData("B", 1, "SIDE", null, null, "수직벽")]
    [InlineData("T", 1, "FRONT", null, null, null)]          // 바닥·천장도 정면 설정은 허용
    [InlineData("PM", 5, "SIDE", null, null, "층 L5")]
    [InlineData("PM", 1, "LEFT", null, null, "mode")]
    [InlineData("PM", 1, "SIDE", "SIDEWAYS", null, "defaultDir")]
    [InlineData("PM", 1, "SIDE", "U_MINUS", 0.2, "standoffM")]
    [InlineData("PM", 1, "side", "u_minus", 1.5, null)]       // 대소문자 무시
    [InlineData("F", 1, "SIDE", null, null, null)]
    [InlineData("A", 1, "SIDE", null, null, null)]
    [InlineData("SM", 1, "SIDE", null, null, null)]
    [InlineData("SL", 1, "SIDE", null, null, "수직벽")]          // 챔퍼는 HD_AMR 미지원 — Order 거부 대상
    [InlineData("PU", 1, "SIDE", null, null, "수직벽")]
    [InlineData("SL", 1, "FRONT", null, null, null)]
    public void Problem_Table(string wall, int level, string mode, string? dir, double? standoff, string? expect)
    {
        var p = WallStationModeService.Problem(wall, level, 4, new WallStationModeRequest(mode, dir, standoff));
        if (expect is null) Assert.Null(p); else Assert.Contains(expect, p);
    }

    // ── 서버 ───────────────────────────────────────────────────────────────
    private static AcsDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AcsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static IConfiguration Config(bool enabled) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Acs:Dispatch:SideStation:Enabled"] = enabled ? "true" : "false" }).Build();

    private static InspectionDispatcher Dispatcher(AcsDbContext db, bool enabled) =>
        new(db, new Vda5050MasterClient("localhost"), new GreedyNearestPolicy(), hub: null!, Config(enabled),
            NullLogger<InspectionDispatcher>.Instance);

    private const double Tx = 9.35, Ty = 12.3;   // T_W_D 회전 0 — 맵 각도 = 도면 각도

    private static async Task<(Guid Scenario, InspectionAreaEntity Pm, WallEntity Wall)> Seed(AcsDbContext db)
    {
        var geom = new TankGeometry(45, 8.2, Math.PI / 4, 1.9, 5.4, Math.PI / 4, 1.9, new[] { 0.0, 2.4, 4.8, 7.2 });
        db.TankGeometries.Add(new TankGeometryEntity { TankId = "CT1", LevelZ = "[0,2.4,4.8,7.2]" });
        WallEntity? pmWall = null;
        foreach (var w in geom.GenerateWalls())
        {
            var e = new WallEntity
            {
                TankId = "CT1", WallCode = w.WallCode, Origin = JsonSerializer.Serialize(w.Pose.Origin),
                UAxis = JsonSerializer.Serialize(w.Pose.U), VAxis = JsonSerializer.Serialize(w.Pose.V),
                Normal = JsonSerializer.Serialize(w.Normal), ULen = w.ULen, VLen = w.VLen, FacingYaw = w.FacingYaw,
            };
            db.Walls.Add(e);
            if (w.WallCode == "PM") pmWall = e;
        }
        db.Maps.Add(new MapEntity { MapId = "CT1-L1", TankId = "CT1", Level = 1, Version = 1 });
        db.MapCalibrations.Add(new MapCalibrationEntity { MapId = "CT1-L1", MapVersion = 1, Tx = Tx, Ty = Ty, YawRad = 0 });
        var pm = new InspectionAreaEntity
        {
            AreaId = Guid.NewGuid(), TankId = "CT1", WallCode = "PM", Level = 1, Name = "PM-01",
            Corners = "[[4.91,1.54],[5.11,1.54],[5.11,2.1],[4.91,2.1]]", StationStandoffM = 0.9,
            Tasks = { new AreaTaskEntity { TaskId = Guid.NewGuid(), Seq = 1, StartU = 5.01, StartV = 1.64, EndU = 5.01, EndV = 2.0,
                SectionDxfId = "DXF-1", ProfileId = "PROF-1" } },
        };
        db.InspectionAreas.Add(pm);
        var sc = new ScenarioEntity { ScenarioId = Guid.NewGuid(), Name = "T", Version = 1, TankId = "CT1" };
        db.Scenarios.Add(sc);
        await db.SaveChangesAsync();
        return (sc.ScenarioId, pm, pmWall!);
    }

    private static async Task<WorkItemEntity> Queue(AcsDbContext db, InspectionDispatcher d, Guid scId)
    {
        var run = new ScenarioRunEntity { RunId = Guid.NewGuid(), ScenarioId = scId, RobotId = "AMR-01" };
        db.ScenarioRuns.Add(run);
        await d.BuildQueueAsync(run, "CT1", default);
        await db.SaveChangesAsync();
        return Assert.Single(await db.WorkItems.AsNoTracking().ToListAsync());
    }

    /// <summary>벽면 PM 에서 정차점까지의 수평 거리(= 이격) — 맵 좌표를 도면으로 되돌려 잰다.</summary>
    private static double DistanceToWall(WorkItemEntity wi, InspectionAreaEntity area, WallEntity wall)
    {
        var (dx, dy) = new DrawingTransform(Tx, Ty, 0).MapToDrawing(wi.X, wi.Y);
        var pose = new WallPose(JsonSerializer.Deserialize<double[]>(wall.Origin)!, JsonSerializer.Deserialize<double[]>(wall.UAxis)!,
            JsonSerializer.Deserialize<double[]>(wall.VAxis)!);
        var (uc, vc) = AreaGeometry.Centroid(JsonSerializer.Deserialize<double[][]>(area.Corners)!);
        var c = pose.LocalToDrawing(uc, vc);
        return Math.Sqrt((dx - c[0]) * (dx - c[0]) + (dy - c[1]) * (dy - c[1]));
    }

    [Fact]
    public async Task Put_List_Delete_AndValidation()
    {
        var db = NewDb();
        await Seed(db);
        var svc = new WallStationModeService(db, Config(false));

        var r = await svc.PutAsync("CT1", 1, "pm", new WallStationModeRequest("side", null, 1.4, "op"), default);
        Assert.Equal(("PM", "SIDE", "U_PLUS", 1.4), (r.WallCode, r.Mode, r.DefaultDir, r.StandoffM));
        await svc.PutAsync("CT1", 1, "PM", new WallStationModeRequest("SIDE", "U_MINUS", null), default);   // 교체
        var list = await svc.ListAsync("CT1", default);
        var row = Assert.Single(list!);
        Assert.Equal(("U_MINUS", (double?)null), (row.DefaultDir, row.StandoffM));
        Assert.False(svc.Enabled);

        await Assert.ThrowsAsync<WallStationModeService.RejectedException>(() =>
            svc.PutAsync("CT1", 1, "B", new WallStationModeRequest("SIDE"), default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.PutAsync("CT1", 1, "XX", new WallStationModeRequest("SIDE"), default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.PutAsync("NONE", 1, "PM", new WallStationModeRequest("SIDE"), default));
        Assert.Null(await svc.ListAsync("NONE", default));

        Assert.True(await svc.DeleteAsync("CT1", 1, "PM", "op", default));
        Assert.False(await svc.DeleteAsync("CT1", 1, "PM", "op", default));
        Assert.Empty((await svc.ListAsync("CT1", default))!);
        Assert.Equal(3, await db.AuditLogs.CountAsync(a => a.Action.StartsWith("WALL_STATION_MODE")));
    }

    [Fact]
    public async Task Enabled_SideWall_UsesFixedStandoff_AndThetaAlongTravel()
    {
        var db = NewDb();
        var (scId, pm, wall) = await Seed(db);
        db.WallStationModes.Add(new WallStationModeEntity { TankId = "CT1", Level = 1, WallCode = "PM", Mode = "SIDE", DefaultDir = "U_PLUS", StandoffM = 1.5 });
        await db.SaveChangesAsync();
        var d = Dispatcher(db, enabled: true);

        var wi = await Queue(db, d, scId);
        Assert.Equal(1.5, DistanceToWall(wi, pm, wall), 6);   // 영역 이격 0.9 대신 층+벽면 고정 이격

        // PM(좌현 벽)은 안쪽 법선이 −y — +x(선수) 로 서면 벽이 왼쪽, −x(선미) 로 서면 오른쪽
        var fromStern = await d.SideDecisionForAsync(wi, wi.X - 3, wi.Y, default);
        Assert.Equal(0, SideStation.Normalize(fromStern!.Theta), 6);
        Assert.Equal("SIDE_LEFT", fromStern.Mode);
        var fromBow = await d.SideDecisionForAsync(wi, wi.X + 3, wi.Y, default);
        Assert.Equal(Math.PI, Math.Abs(fromBow!.Theta), 6);
        Assert.Equal("SIDE_RIGHT", fromBow.Mode);

        // 벽과 수직으로 다가오면 기본 방향(면 +u) — 그 theta 에서의 좌우와 일관
        var u = JsonSerializer.Deserialize<double[]>(wall.UAxis)!;
        var perpendicular = await d.SideDecisionForAsync(wi, wi.X, wi.Y - 3, default);
        Assert.Equal(0, SideStation.Normalize(perpendicular!.Theta - Math.Atan2(u[1], u[0])), 6);
        Assert.Equal(SideStation.SideOf(perpendicular.Theta, -Math.PI / 2), perpendicular.Mode);
    }

    [Fact]
    public async Task ManualTheta_IsKept()
    {
        var db = NewDb();
        var (scId, pm, _) = await Seed(db);
        pm.StationTheta = Math.PI;   // 운영자가 선미 방향으로 저장
        db.WallStationModes.Add(new WallStationModeEntity { TankId = "CT1", Level = 1, WallCode = "PM", Mode = "SIDE" });
        await db.SaveChangesAsync();
        var d = Dispatcher(db, enabled: true);
        var wi = await Queue(db, d, scId);

        var dec = await d.SideDecisionForAsync(wi, wi.X - 3, wi.Y, default);   // 이동 방향은 +x 지만
        Assert.Equal(Math.PI, Math.Abs(dec!.Theta), 6);                         // 저장된 theta 그대로
        Assert.Equal("SIDE_RIGHT", dec.Mode);
        Assert.Contains("수동", dec.Why);
    }

    /// <summary>켜기 설정이 꺼져 있으면 설정이 있어도 정면 — 이격·theta 모두 종전, stationMode 없음.</summary>
    [Fact]
    public async Task Disabled_IgnoresSetting()
    {
        var db = NewDb();
        var (scId, pm, wall) = await Seed(db);
        db.WallStationModes.Add(new WallStationModeEntity { TankId = "CT1", Level = 1, WallCode = "PM", Mode = "SIDE", StandoffM = 1.5 });
        await db.SaveChangesAsync();
        var d = Dispatcher(db, enabled: false);
        var wi = await Queue(db, d, scId);

        Assert.Equal(0.9, DistanceToWall(wi, pm, wall), 6);
        Assert.Null(await d.SideDecisionForAsync(wi, wi.X - 3, wi.Y, default));
        Assert.DoesNotContain("stationMode", wi.Actions);
    }

    /// <summary>계획 정차점 조회도 측면 고정 이격을 쓰고, 방향은 배차 때 정해지므로 비운다.</summary>
    [Fact]
    public async Task PlannedStations_FollowSideStandoff()
    {
        var db = NewDb();
        var (scId, _, _) = await Seed(db);
        db.WallStationModes.Add(new WallStationModeEntity { TankId = "CT1", Level = 1, WallCode = "PM", Mode = "SIDE", StandoffM = 1.5 });
        await db.SaveChangesAsync();
        var st = Assert.Single((await Dispatcher(db, enabled: true).GetPlannedStationsAsync(scId, default))!.Stations);
        Assert.Equal(1.5, st.StandoffM);
        Assert.Null(st.Yaw);
    }

    [Fact]
    public void SeamStartTestOrder_CarriesStationMode()
    {
        var pos = new JsonObject { ["seamStartW"] = new JsonArray(1.0, 2.0, 0.5) };
        var side = InspectionDispatcher.BuildSeamStartTestOrder("CT1-L1", 1, 2, 0.0, pos, "TEST-1", 0.08, 0.07, 100, "SIDE_LEFT");
        var prm = (JsonObject)side.Nodes[0].Actions[0].ActionParameters.Single(p => p.Key == "params").Value!;
        Assert.Equal("SIDE_LEFT", prm["stationMode"]!.GetValue<string>());
        var front = InspectionDispatcher.BuildSeamStartTestOrder("CT1-L1", 1, 2, 0.0, pos, "TEST-1", 0.08, 0.07);
        Assert.Null(((JsonObject)front.Nodes[0].Actions[0].ActionParameters.Single(p => p.Key == "params").Value!)["stationMode"]);
    }

    [Theory]
    [InlineData("SIDE_LEFT", true)]
    [InlineData("SIDE_RIGHT", true)]
    [InlineData("FRONT", true)]
    [InlineData("LEFT", false)]
    public void CanonicalSchemas_StationModeEnum(string mode, bool ok)
    {
        var prm = new JsonObject
        {
            ["seamType"] = "LINE", ["sectionDxfId"] = "S-1", ["inspectionProfileId"] = "P-1", ["standoffMm"] = 400,
            ["anchorGroupId"] = "G", ["seqInGroup"] = 1, ["stationMode"] = mode,
        };
        var position = new JsonObject
        {
            ["seamStartW"] = new JsonArray(1.0, 2.0, 0.5), ["seamEndW"] = new JsonArray(2.0, 2.0, 0.5),
            ["drawingPos"] = new JsonObject { ["tank"] = "CT1", ["level"] = 1, ["wall_code"] = "PM", ["u"] = 0.1, ["v"] = 0.2, ["x"] = 1.0, ["y"] = 2.0, ["z"] = 0.5 },
        };
        var weld = WeldInspectionPayload.ValidateSchema(ActionCatalogSeed.StartWeldInspectionParamSchema,
            WeldInspectionPayload.BuildActionParameters("J", position, prm));
        var move = WeldInspectionPayload.ValidateSchema(ActionCatalogSeed.MoveToSeamStartParamSchema,
            WeldInspectionPayload.BuildActionParameters("J", position, new JsonObject { ["liftStrokeMm"] = 0, ["stationMode"] = mode }));
        Assert.Equal(ok, weld.Count == 0);
        Assert.Equal(ok, move.Count == 0);
    }

    // ── orderValidationError 는 실행 전 거부일 때만 Order 거부로 본다 (HD_AMR 확인 요청 2026-11-15) ──
    private static async Task<(AcsDbContext Db, InspectionDispatcher D, string OrderId)> RejectStop(string firstActionStatus)
    {
        var db = NewDb();
        var runId = Guid.NewGuid();
        var orderId = Guid.NewGuid().ToString();
        var mission = new MissionEntity { MissionId = Guid.NewGuid(), RunId = runId, MapId = "CT1-L1", RobotId = "AMR-01", OrderId = orderId, State = "Released" };
        db.ScenarioRuns.Add(new ScenarioRunEntity { RunId = runId, ScenarioId = Guid.NewGuid(), RobotId = "AMR-01", State = "RUNNING", Missions = { mission } });
        var wi = new WorkItemEntity { WorkItemId = Guid.NewGuid(), RunId = runId, AreaId = Guid.NewGuid(), MapId = "CT1-L1", Status = "DISPATCHED", OrderId = orderId, Actions = "[]" };
        db.WorkItems.Add(wi);
        db.RobotContexts.Add(new RobotContextEntity { RobotId = "AMR-01", ReportedMapId = "CT1-L1", ReportedX = 0, ReportedY = 0 });
        foreach (var st in new[] { firstActionStatus, "WAITING" })
            db.OrderActions.Add(new OrderActionEntity { ActionId = Guid.NewGuid(), MissionId = mission.MissionId, WorkItemId = wi.WorkItemId, ActionType = "startWeldInspection", Status = st });
        await db.SaveChangesAsync();
        return (db, Dispatcher(db, enabled: true), orderId);
    }

    [Fact]
    public async Task OrderValidationError_BeforeExecution_FailsStop()
    {
        var (db, d, orderId) = await RejectStop("WAITING");
        Assert.True(await d.HandleOrderRejectedAsync("AMR-01", $"Order 거부: orderId={orderId} SIDE_LR_MISMATCH: …", default));
        Assert.All(await db.OrderActions.AsNoTracking().ToListAsync(), a => Assert.Equal("FAILED", a.Status));
        Assert.Equal("FAILED", (await db.WorkItems.AsNoTracking().SingleAsync()).Status);
    }

    /// <summary>실행 중(액션 상태 보고됨)에 같은 errorType 이 와도 정차를 닫거나 다음 Order 를 보내지 않는다 — 수행 중인 Order 보호.</summary>
    [Fact]
    public async Task OrderValidationError_DuringExecution_IsIgnored()
    {
        var (db, d, orderId) = await RejectStop("RUNNING");
        Assert.False(await d.HandleOrderRejectedAsync("AMR-01", $"orderId={orderId} SIDE_LR_MISMATCH: …", default));
        Assert.Equal(new[] { "RUNNING", "WAITING" }, (await db.OrderActions.AsNoTracking().ToListAsync()).Select(a => a.Status).OrderBy(s => s));
        Assert.Equal("DISPATCHED", (await db.WorkItems.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(await db.Alarms.AsNoTracking().ToListAsync());
    }
}
