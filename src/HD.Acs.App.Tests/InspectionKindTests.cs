using System.Text.Json;
using HD.Acs.App.Services;
using HD.Acs.Core.Planning;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using HD.Acs.Vda5050;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HD.Acs.App.Tests;

/// <summary>
/// 검사 종류(WELD 용접 / GAS 가스) — 선창 단위 값. 기존 선창은 모두 WELD 이고, 지정하지 않은 재등록은 값을 바꾸지 않는다.
/// 지령 생성은 아직 용접검사만 구현돼 있어 GAS 선창의 run 은 시작하지 않는다.
/// </summary>
public class InspectionKindTests
{
    private static CreateTankGeometryRequest Geometry(string? kind = null) =>
        new(45, 8.2, 45, 1.9, 5.4, 45, 1.9, new[] { 0.0, 2.4, 4.8, 7.2 }, 0, 0, null, null, null, "tester", InspectionKind: kind);

    private static (ProjectImportService Import, TankGeometryService Geometry, AcsDbContext Db) Build()
    {
        var db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var geometry = new TankGeometryService(db, NullLogger<TankGeometryService>.Instance);
        return (new ProjectImportService(db, geometry, NullLogger<ProjectImportService>.Instance), geometry, db);
    }

    private static async Task<string?> KindOf(TankGeometryService svc, string tankId) =>
        JsonSerializer.SerializeToElement(await svc.GetGeometryAsync(tankId)).GetProperty("inspectionKind").GetString();

    [Fact]
    public async Task Unspecified_NewTankIsWeld_AndReimportKeepsExistingKind()
    {
        var (import, geometry, _) = Build();
        var empty = Array.Empty<ProjectImportArea>();

        await import.ImportAsync("CT1", new ProjectImportRequest(Geometry(), empty, "tester"));
        Assert.Equal("WELD", await KindOf(geometry, "CT1"));          // 구파일·구 UI(값 없음) = 용접검사

        await import.ImportAsync("CT2", new ProjectImportRequest(Geometry("gas"), empty, "tester"));
        Assert.Equal("GAS", await KindOf(geometry, "CT2"));           // 대소문자 무시, 정본 표기로 저장

        await import.ImportAsync("CT2", new ProjectImportRequest(Geometry(), empty, "tester"));
        Assert.Equal("GAS", await KindOf(geometry, "CT2"));           // 값 없이 다시 가져와도 바뀌지 않는다

        await import.ImportAsync("CT2", new ProjectImportRequest(Geometry("WELD"), empty, "tester"));
        Assert.Equal("WELD", await KindOf(geometry, "CT2"));          // 명시하면 바뀐다
    }

    [Fact]
    public async Task UnknownKind_IsRejected_WithoutChangingDb()
    {
        var (import, geometry, db) = Build();

        var ex = await Assert.ThrowsAsync<ProjectImportService.ImportRejectedException>(() =>
            import.ImportAsync("CT1", new ProjectImportRequest(Geometry("LEAK"), Array.Empty<ProjectImportArea>(), "tester")));
        Assert.Contains("LEAK", Assert.Single(ex.Reasons));
        Assert.Empty(await db.TankGeometries.ToListAsync());

        var geom = new TankGeometry(45, 8.2, Math.PI / 4, 1.9, 5.4, Math.PI / 4, 1.9, new[] { 0.0, 2.4, 4.8, 7.2 });
        await Assert.ThrowsAsync<TankGeometryService.GeometryInvalidException>(() =>
            geometry.RegisterAsync("CT1", geom, null, null, null, "tester", inspectionKind: "LEAK"));
        Assert.Empty(await db.TankGeometries.ToListAsync());
    }

    /// <summary>지령 만들기 구현이 등록된 종류만 지원 — 값 없음(검사 종류가 생기기 전 선창)은 용접검사.</summary>
    [Theory]
    [InlineData(null, true)] [InlineData("", true)] [InlineData("WELD", true)] [InlineData("weld", true)] [InlineData("GAS", false)]
    public void CommandBuilders_OnlyWeldIsRegistered(string? kind, bool expected)
    {
        var builders = new InspectionCommandBuilders(new WeldInspectionCommandBuilder(400, 400, NullLogger.Instance));
        Assert.Equal(expected, builders.Supports(kind));
        Assert.Equal(expected ? "startWeldInspection" : null, builders.Find(kind)?.ActionType);
    }

    /// <summary>GAS 선창은 run 을 만들지 않는다(409) — 용접검사 지령이 다른 장비를 단 로봇에 나가지 않게.</summary>
    [Fact]
    public async Task GasTank_RunStartIsRefused_AndNoRunIsCreated()
    {
        var db = new AcsDbContext(new DbContextOptionsBuilder<AcsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.TankGeometries.Add(new TankGeometryEntity { TankId = "GT1", LevelZ = "[0,2.4]", InspectionKind = "GAS" });
        var sc = new ScenarioEntity { ScenarioId = Guid.NewGuid(), Name = "G", Version = 1, TankId = "GT1" };
        db.Scenarios.Add(sc);
        db.RobotContexts.Add(new RobotContextEntity { RobotId = "AMR-01", ConnectionState = "ONLINE", ReportedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var dispatcher = new InspectionDispatcher(db, new Vda5050MasterClient("localhost"), new GreedyNearestPolicy(),
            hub: null!, new ConfigurationBuilder().Build(), NullLogger<InspectionDispatcher>.Instance);
        var missions = new MissionService(db, new Vda5050MasterClient("localhost"), dispatcher, NullLogger<MissionService>.Instance);

        var ex = await Assert.ThrowsAsync<RunConflictException>(() => missions.StartRunAsync(sc.ScenarioId, "AMR-01"));

        Assert.Contains("GAS", ex.Message);
        Assert.Empty(await db.ScenarioRuns.ToListAsync());
    }
}
