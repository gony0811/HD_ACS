using System.Text.Json;
using HD.Acs.App.Services;
using HD.Acs.Core.Planning;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HD.Acs.App.Tests;

/// <summary>
/// 선창 형상 조회 API [SAIGE 연동 사양서 v2.6 §4.6] — 사양서 예시 선창(CT1: L45000·wFloor 8200·45°·1900/5400/1900)의
/// **예시 응답 값 그대로** 나오는지 검증한다.
/// </summary>
public class TankShapeApiTests
{
    private static readonly Guid AreaId = Guid.Parse("c11e0000-0000-4000-8000-00000000000a");
    private static readonly Guid TaskId = Guid.Parse("3a9f2c14-8e51-4d7a-b2c9-1f6e0a5d3b47");
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static async Task<TankShapeQueryService> SeedAsync()
    {
        var db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var geom = new TankGeometry(45, 8.2, Math.PI / 4, 1.9, 5.4, Math.PI / 4, 1.9, new[] { 0.0, 2.4, 4.8, 7.2 });
        db.TankGeometries.Add(new TankGeometryEntity
        {
            TankId = "CT1", LengthL = 45, WFloor = 8.2, ThetaLow = Math.PI / 4, HLow = 1.9, HWall = 5.4, ThetaUp = Math.PI / 4, HUp = 1.9,
            LevelZ = "[0,2.4,4.8,7.2]",
        });
        foreach (var w in geom.GenerateWalls())
            db.Walls.Add(new WallEntity
            {
                TankId = "CT1", WallCode = w.WallCode, Origin = JsonSerializer.Serialize(w.Pose.Origin),
                UAxis = JsonSerializer.Serialize(w.Pose.U), VAxis = JsonSerializer.Serialize(w.Pose.V),
                Normal = JsonSerializer.Serialize(w.Normal), ULen = w.ULen, VLen = w.VLen, FacingYaw = w.FacingYaw,
            });
        db.InspectionAreas.Add(new InspectionAreaEntity
        {
            AreaId = AreaId, TankId = "CT1", WallCode = "PM", Level = 2, Name = "PM-L2-01",
            Corners = "[[3,0.6],[6,0.6],[6,2.4],[3,2.4]]",
            Tasks =
            {
                new AreaTaskEntity { TaskId = TaskId, Seq = 1, StartU = 3.2, StartV = 0.9, EndU = 5.8, EndV = 0.9, SeamType = "LINE" },
                new AreaTaskEntity { TaskId = Guid.NewGuid(), Seq = 2, StartU = 3.0, StartV = 0.6, EndU = 6.0, EndV = 4.6, SeamType = "LINE" },
            },
        });
        await db.SaveChangesAsync();
        return new TankShapeQueryService(db);
    }

    [Fact]
    public async Task Geometry_MatchesSpecExample_MmIntegers()
    {
        var g = (await (await SeedAsync()).GetGeometryAsync("CT1"))!;

        Assert.Equal((45000, 8200, 45.0, 1900, 5400, 45.0, 1900), (g.LengthL, g.WFloor, g.ThetaLow, g.HLow, g.HWall, g.ThetaUp, g.HUp));
        Assert.Equal(new[] { 0, 2400, 4800, 7200 }, g.LevelZ);
        Assert.Equal((12000, 8200, 9200), (g.Derived.Beam, g.Derived.WCeil, g.Derived.Height));

        var json = JsonSerializer.Serialize(g, Web);
        foreach (var key in new[] { "tankId", "lengthL", "wFloor", "thetaLow", "hLow", "hWall", "thetaUp", "hUp", "levelZ", "derived", "beam", "wCeil", "height" })
            Assert.Contains($"\"{key}\":", json);
        Assert.Contains("\"lengthL\":45000,", json);   // 정수 직렬화(45000.0 아님)
        Assert.Null(await (await SeedAsync()).GetGeometryAsync("NOPE"));
    }

    [Fact]
    public async Task Walls_TenFaces_OrderedByWallId_RectangleAndOctagon()
    {
        var walls = (await (await SeedAsync()).GetWallsAsync("CT1", null))!;

        Assert.Equal(Enumerable.Range(1, 10), walls.Select(w => w.WallId));
        Assert.Equal(new[] { "B", "T", "PM", "SM", "F", "A", "PL", "SL", "PU", "SU" }, walls.Select(w => w.WallCode));
        Assert.All(walls, w => Assert.Null(w.ReachableVBand));   // level 미지정 = 필드 없음

        // §4.6.2 예시 — 좌현벽 PM
        var pm = walls.Single(w => w.WallCode == "PM");
        Assert.Equal((3, 45000, 5400, "RECTANGLE"), (pm.WallId, pm.UMax, pm.VMax, pm.Shape));
        Assert.Equal(new[] { new[] { 0, 0 }, new[] { 45000, 0 }, new[] { 45000, 5400 }, new[] { 0, 5400 } }, pm.Outline);
        Assert.Equal(new[] { -22500, 6000, 1900 }, pm.Origin);
        Assert.Equal(new[] { 1.0, 0, 0 }, pm.UAxis);
        Assert.Equal(new[] { 0.0, 0, 1 }, pm.VAxis);

        // §4.6.2 예시 — 후벽 A 팔각 8정점 (사양서 값 그대로)
        var aft = walls.Single(w => w.WallCode == "A");
        Assert.Equal((6, 12000, 9200, "POLYGON"), (aft.WallId, aft.UMax, aft.VMax, aft.Shape));
        Assert.Equal(new[]
        {
            new[] { 1900, 0 }, new[] { 0, 1900 }, new[] { 0, 7300 }, new[] { 1900, 9200 },
            new[] { 10100, 9200 }, new[] { 12000, 7300 }, new[] { 12000, 1900 }, new[] { 10100, 0 },
        }, aft.Outline);
        Assert.Equal(aft.Outline, walls.Single(w => w.WallCode == "F").Outline);   // 좌우 대칭 단면 → F·A 동일 윤곽

        // 챔퍼 세로 치수 = 높이가 아니라 경사면을 따라간 길이 [§4.3]: 1900/sin45° = 2687
        Assert.Equal(2687, walls.Single(w => w.WallCode == "PL").VMax);
    }

    [Fact]
    public async Task Walls_LevelFilter_OnlyReachable_WithVBandMm()
    {
        var svc = await SeedAsync();

        var l1 = (await svc.GetWallsAsync("CT1", 1))!;
        Assert.Contains(l1, w => w.WallCode == "B");        // 바닥은 1층에서만
        Assert.DoesNotContain(l1, w => w.WallCode == "T");
        var l4 = (await svc.GetWallsAsync("CT1", 4))!;
        Assert.Contains(l4, w => w.WallCode == "T");
        Assert.DoesNotContain(l4, w => w.WallCode == "B");

        // L2 = z∈[2400,4800]. PM은 z=1900에서 시작 → v∈[500,2900] (§4.6.2 예시 값)
        var pm = (await svc.GetWallsAsync("CT1", 2))!.Single(w => w.WallCode == "PM");
        Assert.Equal(new[] { 500, 2900 }, pm.ReachableVBand);
        Assert.Contains("\"reachableVBand\":[500,2900]", JsonSerializer.Serialize(pm, Web));
        Assert.DoesNotContain("reachableVBand", JsonSerializer.Serialize((await svc.GetWallsAsync("CT1", null))![0], Web));

        Assert.Empty((await svc.GetWallsAsync("CT1", 9))!);     // 없는 층 = 도달 가능 면 없음
        Assert.Null(await svc.GetWallsAsync("NOPE", null));      // 없는 선창 = 404
    }

    /// <summary>10면 × 2층에 영역 1개·TASK 1개씩 — 필터·정렬 검증용.</summary>
    private static async Task<TankShapeQueryService> SeedAllWallsAsync()
    {
        var db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.TankGeometries.Add(new TankGeometryEntity
        {
            TankId = "CT1", LengthL = 45, WFloor = 8.2, ThetaLow = Math.PI / 4, HLow = 1.9, HWall = 5.4, ThetaUp = Math.PI / 4, HUp = 1.9,
            LevelZ = "[0,2.4,4.8,7.2]",
        });
        foreach (var code in new[] { "B", "T", "PM", "SM", "F", "A", "PL", "SL", "PU", "SU" })
            foreach (var level in new[] { 1, 2 })
                db.InspectionAreas.Add(new InspectionAreaEntity
                {
                    AreaId = Guid.NewGuid(), TankId = "CT1", WallCode = code, Level = level, Name = $"{code}-L{level}-01",
                    Corners = "[[0,0],[1,0],[1,1],[0,1]]",
                    Tasks = { new AreaTaskEntity { TaskId = Guid.NewGuid(), Seq = 1, StartU = 0.1, StartV = 0.1, EndU = 0.9, EndV = 0.1, SeamType = "LINE" } },
                });
        await db.SaveChangesAsync();
        return new TankShapeQueryService(db);
    }

    /// <summary>
    /// 면·층 단위 TASK 조회 [이노로보틱스 요청 2026-09-22] — 요청 문서의 응답 예시 그대로.
    /// 현행 영역 단건 응답과 같은 항목 + 소속 5필드.
    /// </summary>
    [Fact]
    public async Task Tasks_ByWallAndLevel_MatchesRequestedShape()
    {
        var svc = await SeedAsync();

        var tasks = (await svc.GetTasksAsync("CT1", wallId: 3, level: 2))!;
        Assert.Equal(2, tasks.Count);                       // 시드 영역(PM·L2)의 TASK 2건
        var t = tasks[0];
        Assert.Equal((TaskId, AreaId, "PM-L2-01", 3, "PM", 2), (t.TaskId, t.AreaId, t.AreaName, t.WallId, t.WallCode, t.Level));
        Assert.Equal((1, 3200, 900, 5800, 900, 2600, "LINE"), (t.Seq, t.StartU, t.StartV, t.EndU, t.EndV, t.SeamLength, t.SeamType));

        // 요청 문서 §3 응답 예시의 필드가 모두 있고, 이름·순서가 현행 영역 단건 응답과 어긋나지 않는다
        var json = JsonSerializer.Serialize(t, Web);
        foreach (var key in new[] { "taskId", "areaId", "areaName", "wallId", "wallCode", "level",
                                    "seq", "startU", "startV", "endU", "endV", "seamLength", "seamType" })
            Assert.Contains($"\"{key}\":", json);
        Assert.Contains("\"startU\":3200,", json);          // mm 정수 직렬화
    }

    [Fact]
    public async Task Tasks_Filters_And_NotFound()
    {
        var svc = await SeedAllWallsAsync();

        Assert.Equal(20, (await svc.GetTasksAsync("CT1", null, null))!.Count);       // 무필터 = 전 면·전 층
        Assert.Equal(10, (await svc.GetTasksAsync("CT1", null, 2))!.Count);          // 층만
        Assert.Equal(2, (await svc.GetTasksAsync("CT1", 3, null))!.Count);           // 면만
        var one = Assert.Single((await svc.GetTasksAsync("CT1", 3, 2))!);            // 화면 1장 = 면 1개 × 층 1개
        Assert.Equal(("PM", 2), (one.WallCode, one.Level));

        Assert.Empty((await svc.GetTasksAsync("CT1", null, 9))!);                    // 없는 층 = 빈 배열
        Assert.Null(await svc.GetTasksAsync("NOPE", null, null));                    // 없는 선창 = 404
    }

    /// <summary>정렬 = wallId → level → … — 면 순서가 Core WallIds 매핑과 일치해야 한다(정렬 CASE 식 대조).</summary>
    [Fact]
    public async Task Tasks_OrderedByWallIdThenLevel_PagesAreStable()
    {
        var svc = await SeedAllWallsAsync();

        var all = (await svc.GetTasksAsync("CT1", null, null))!;
        Assert.Equal(Enumerable.Range(1, 10).SelectMany(id => new[] { id, id }), all.Select(t => t.WallId));
        Assert.Equal(new[] { 1, 2 }, all.Where(t => t.WallId == 3).Select(t => t.Level));

        // limit/offset = 같은 정렬의 연속 구간(전량 적재 없이 나눠 받기)
        Assert.Equal(all.Take(5).Select(t => t.TaskId), (await svc.GetTasksAsync("CT1", null, null, limit: 5))!.Select(t => t.TaskId));
        Assert.Equal(all.Skip(5).Take(5).Select(t => t.TaskId),
            (await svc.GetTasksAsync("CT1", null, null, limit: 5, offset: 5))!.Select(t => t.TaskId));
        Assert.Equal(all.Skip(18).Select(t => t.TaskId), (await svc.GetTasksAsync("CT1", null, null, offset: 18))!.Select(t => t.TaskId));
    }

    [Fact]
    public async Task Areas_And_Tasks_MatchSpecExample()
    {
        var svc = await SeedAsync();

        var area = Assert.Single(await svc.GetAreasAsync("CT1", null, null));
        Assert.Equal((AreaId, "PM-L2-01", 3, "PM", 2, 2), (area.AreaId, area.AreaName, area.WallId, area.WallCode, area.Level, area.TaskCount));
        Assert.Equal(new[] { new[] { 3000, 600 }, new[] { 6000, 600 }, new[] { 6000, 2400 }, new[] { 3000, 2400 } }, area.Corners);
        Assert.Empty(await svc.GetAreasAsync("CT9", null, null));
        Assert.Empty(await svc.GetAreasAsync("CT1", 1, null));          // level 필터
        Assert.Single(await svc.GetAreasAsync(null, null, 3));          // wallId 필터, tankId 생략 = 전 선창

        var tasks = (await svc.GetAreaTasksAsync(AreaId))!;
        var t = tasks[0];
        Assert.Equal((TaskId, 1, 3200, 900, 5800, 900, 2600, "LINE"), (t.TaskId, t.Seq, t.StartU, t.StartV, t.EndU, t.EndV, t.SeamLength, t.SeamType));
        Assert.Equal(5000, tasks[1].SeamLength);                         // 3-4-5: √(3000²+4000²) — 대각 용접선 길이
        Assert.Null(await svc.GetAreaTasksAsync(Guid.NewGuid()));        // 없는 영역 = 404

        var json = JsonSerializer.Serialize(t, Web);
        foreach (var key in new[] { "taskId", "seq", "startU", "startV", "endU", "endV", "seamLength", "seamType" })
            Assert.Contains($"\"{key}\":", json);
    }
}
