using HD.Acs.App.Services;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HD.Acs.App.Tests;

/// <summary>
/// 프로젝트(.hdacs) 일괄 가져오기 — 요청 1건으로 선창 재등록+영역·작업 적재, **검증 거부 시 DB 무변경**(전부 또는 전무).
/// 종전 단건 루프(대형 선창 약 8,800 요청)는 수 분이 걸리고 중간 실패 시 지워진 DB가 반쯤 적재된 채 남았다.
/// </summary>
public class ProjectImportTests
{
    private static readonly Guid AreaA = Guid.Parse("c11e0000-0000-4000-8000-00000000000a");
    private static readonly Guid Task1 = Guid.Parse("3a9f2c14-8e51-4d7a-b2c9-1f6e0a5d3b47");
    private static readonly Guid Task2 = Guid.Parse("3a9f2c14-8e51-4d7a-b2c9-1f6e0a5d3b48");

    // 사양서 예시 선창(CT1) — PM 면 v∈[0.6,2.0] 영역은 L2로 유도된다(TankShapeApiTests와 같은 형상)
    private static CreateTankGeometryRequest Geometry() =>
        new(45, 8.2, 45, 1.9, 5.4, 45, 1.9, new[] { 0.0, 2.4, 4.8, 7.2 }, 0, 0, null, null, null, "tester");

    private static readonly double[][] PmCorners = { new[] { 3.0, 0.6 }, new[] { 4.4, 0.6 }, new[] { 4.4, 2.0 }, new[] { 3.0, 2.0 } };

    private static CreateAreaTaskRequest T(int? seq, Guid? id, double u = 3.2, string seamType = "line", string? name = null) =>
        new(seq, name, seamType, u, 0.9, u + 0.9, 0.9, "DXF-1", "PROF-1", null, id);

    private static ProjectImportArea A(string name, Guid? id, double[][] corners, params CreateAreaTaskRequest[] tasks) =>
        new("PM", name, corners, 0, 0, 0, 0, null, null, null, null, null, id, tasks);

    private static (ProjectImportService Svc, AcsDbContext Db) Build(string? dbName = null)
    {
        var db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>()
            .UseInMemoryDatabase(dbName ?? Guid.NewGuid().ToString()).Options);
        var geometry = new TankGeometryService(db, NullLogger<TankGeometryService>.Instance);
        return (new ProjectImportService(db, geometry, NullLogger<ProjectImportService>.Instance), db);
    }

    [Fact]
    public async Task Import_StoresAll_PreservingIdsSeqNames_DerivingLevel()
    {
        var (svc, db) = Build();
        var shifted = PmCorners.Select(p => new[] { p[0] + 2, p[1] }).ToArray();
        var req = new ProjectImportRequest(Geometry(), new[]
        {
            A("PM-L2-01", AreaA, PmCorners, T(1, Task1, name: "W1"), T(5, Task2, u: 3.3, name: "W5")),
            A("PM-L2-02", null, shifted, T(null, null, u: 5.2)),   // 구파일 — 식별자·seq 없음 → 서버 발급·1부터
        }, "tester");

        var r = await svc.ImportAsync("CT1", req);

        Assert.Equal((2, 3), (r.Areas, r.Tasks));
        Assert.Equal(AreaA, r.AreaIds[0]);
        Assert.NotEqual(Guid.Empty, r.AreaIds[1]);
        var areas = await db.InspectionAreas.Include(a => a.Tasks).OrderBy(a => a.Name).ToListAsync();
        Assert.All(areas, a => Assert.Equal(2, a.Level));   // 층은 코너 v로 유도
        Assert.Equal(r.AreaIds[1], areas[1].AreaId);
        var t = areas[0].Tasks.OrderBy(x => x.Seq).ToList();
        Assert.Equal(new[] { Task1, Task2 }, t.Select(x => x.TaskId));
        Assert.Equal(new[] { 1, 5 }, t.Select(x => x.Seq));
        Assert.Equal(new[] { "W1", "W5" }, t.Select(x => x.Name));
        Assert.All(t, x => Assert.Equal("LINE", x.SeamType));   // canonical 정규화
        Assert.Equal(1, Assert.Single(areas[1].Tasks).Seq);
        Assert.Equal(10, await db.Walls.CountAsync(w => w.TankId == "CT1"));
        Assert.Single(db.AuditLogs.Where(x => x.Action == "PROJECT_IMPORT"));
    }

    /// <summary>영역별 후진 상한(N23)도 파일에서 그대로 복원 — 구파일(null)은 서버 기본, 음수는 거부.</summary>
    [Fact]
    public async Task Import_KeepsStationMaxBackward_RejectsNegative()
    {
        var (svc, db) = Build();
        await svc.ImportAsync("CT1", new ProjectImportRequest(Geometry(),
            new[] { A("PM-L2-01", AreaA, PmCorners, T(1, Task1)) with { StationMaxBackwardMm = 150 } }, "tester"));
        Assert.Equal(150, (await db.InspectionAreas.SingleAsync()).StationMaxBackwardMm);

        var bad = new ProjectImportRequest(Geometry(),
            new[] { A("PM-L2-02", Guid.NewGuid(), PmCorners, T(1, Guid.NewGuid())) with { StationMaxBackwardMm = -1 } }, "tester");
        var ex = await Assert.ThrowsAsync<ProjectImportService.ImportRejectedException>(() => svc.ImportAsync("CT1", bad));
        Assert.Contains("stationMaxBackwardMm", ex.Message);
    }

    [Fact]
    public async Task Reject_OneBadTask_LeavesExistingDataUntouched()
    {
        var (svc, db) = Build();
        await svc.ImportAsync("CT1", new ProjectImportRequest(Geometry(), new[] { A("PM-L2-01", AreaA, PmCorners, T(1, Task1)) }, "tester"));

        // 두 번째 파일: 작업 하나가 영역 밖 → 전체 거부
        var bad = new ProjectImportRequest(Geometry(), new[]
        {
            A("PM-L2-09", Guid.NewGuid(), PmCorners, T(1, Guid.NewGuid()), T(2, Guid.NewGuid(), u: 9.0)),
        }, "tester");
        var ex = await Assert.ThrowsAsync<ProjectImportService.ImportRejectedException>(() => svc.ImportAsync("CT1", bad));

        Assert.Contains(ex.Reasons, r => r.Contains("PM-L2-09") && r.Contains("seq 2") && r.Contains("내부가 아닙니다"));
        db.ChangeTracker.Clear();
        var left = Assert.Single(await db.InspectionAreas.Include(a => a.Tasks).ToListAsync());
        Assert.Equal(AreaA, left.AreaId);   // 첫 가져오기 결과 그대로
        Assert.Equal(Task1, Assert.Single(left.Tasks).TaskId);
    }

    [Fact]
    public async Task Reject_DuplicatesInsideFile_AndOversizeArea()
    {
        var (svc, _) = Build();
        var big = new[] { new[] { 3.0, 0.6 }, new[] { 4.6, 0.6 }, new[] { 4.6, 2.0 }, new[] { 3.0, 2.0 } };   // u 폭 1.6m
        var req = new ProjectImportRequest(Geometry(), new[]
        {
            A("PM-L2-01", AreaA, PmCorners, T(1, Task1), T(1, Task1, u: 3.3)),   // seq·taskId 중복
            A("PM-L2-01", Guid.NewGuid(), PmCorners),                            // 같은 면 같은 이름
            A("PM-BIG", Guid.NewGuid(), big),
        }, "tester");

        var ex = await Assert.ThrowsAsync<ProjectImportService.ImportRejectedException>(() => svc.ImportAsync("CT1", req));

        Assert.Contains(ex.Reasons, r => r.Contains("seq 는 1 이상이고 영역 안에서 유일"));
        Assert.Contains(ex.Reasons, r => r.Contains("taskId") && r.Contains("중복"));
        Assert.Contains(ex.Reasons, r => r.Contains("같은 이름의 영역"));
        Assert.Contains(ex.Reasons, r => r.Contains("PM-BIG") && r.Contains("1.44"));
    }

    [Fact]
    public async Task Reject_IdUsedByAnotherTank_ButSameTankReimportIsAllowed()
    {
        var (svc, db) = Build();
        var file = new ProjectImportRequest(Geometry(), new[] { A("PM-L2-01", AreaA, PmCorners, T(1, Task1)) }, "tester");
        await svc.ImportAsync("CT1", file);

        // 같은 선창에 같은 파일 재가져오기 = 교체(같은 ID 허용)
        var again = await svc.ImportAsync("CT1", file);
        Assert.Equal((1, 1), (again.Areas, again.Tasks));
        Assert.Equal(1, await db.InspectionAreas.CountAsync());

        // 다른 선창으로 같은 ID → 영구 식별자 재사용 거부, CT2는 만들어지지 않음
        var ex = await Assert.ThrowsAsync<ProjectImportService.ImportRejectedException>(() => svc.ImportAsync("CT2", file));
        Assert.Contains(ex.Reasons, r => r.Contains($"areaId '{AreaA}'") && r.Contains("다른 선창"));
        Assert.Contains(ex.Reasons, r => r.Contains($"taskId '{Task1}'") && r.Contains("다른 선창"));
        Assert.False(await db.TankGeometries.AnyAsync(g => g.TankId == "CT2"));
    }

    [Fact]
    public async Task Reject_InvalidGeometry_BeforeAnything()
    {
        var (svc, db) = Build();
        var badGeom = Geometry() with { LengthL = -1 };
        await Assert.ThrowsAsync<ProjectImportService.ImportRejectedException>(() =>
            svc.ImportAsync("CT1", new ProjectImportRequest(badGeom, Array.Empty<ProjectImportArea>(), "tester")));
        Assert.False(await db.TankGeometries.AnyAsync());
    }
}
