using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using HD.Acs.UI.Models;
using HD.Acs.UI.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace HD.Acs.UI.Core.Tests;

/// <summary>
/// 베이스 프로젝트 파일로 새 프로젝트 만들기 — 형상·영역·작업·시나리오를 새 선창 ID 로 복제한다.
/// 베이스 선창이 DB 에 남아 있어도 겹치지 않게 영역 ID·taskId 는 새로 발급하고, 캘리브레이션은 복사하지 않는다.
/// </summary>
public class ProjectFromBaseTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"hdacs-base-{Guid.NewGuid():N}.hdacs");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    private static readonly Guid AreaA = Guid.Parse("c11e0000-0000-4000-8000-00000000000a");
    private static readonly Guid AreaB = Guid.Parse("c11e0000-0000-4000-8000-00000000000b");
    private static readonly Guid Task1 = Guid.Parse("3a9f2c14-8e51-4d7a-b2c9-1f6e0a5d3b47");
    private static readonly Guid Task2 = Guid.Parse("3a9f2c14-8e51-4d7a-b2c9-1f6e0a5d3b48");

    private static ProjectDoc BaseDoc()
    {
        TaskDoc T(int seq, Guid id, bool? enabled = null) => new(seq, $"H{seq:00}", "LINE", 3.2, 0.9, 4.2, 0.9, "DXF-1", "PROF-1", id, null, enabled);
        AreaDoc A(string name, Guid id, double u0, params TaskDoc[] t) => new("PM", 2, name, u0, 0.6, u0 + 1.4, 2.0, 1.5, 2.5, 0.1, t,
            new[] { new[] { u0, 0.6 }, new[] { u0 + 1.4, 0.6 }, new[] { u0 + 1.4, 2.0 }, new[] { u0, 2.0 } }, 1.2, id, 300, 100);
        return new ProjectDoc(3, "CT1",
            new GeometryDoc(45, 8.2, 45, 1.9, 5.4, 45, 1.9, new[] { 0.0, 2.4 }, 0, 0, 0.5, 2.9),
            new[] { A("PM-L2-01", AreaA, 3.0, T(1, Task1), T(5, Task2, false)), A("PM-L2-02", AreaB, 4.4) },
            new[] { new CalibrationDoc("CT1-L1", new[] { new CalibrationPointDoc(0, 0, 1, 1), new CalibrationPointDoc(1, 0, 2, 1) }, 1, 1, 0, 0) },
            new[] { new ScenarioDoc("부분 검사", new[] { AreaB }) });
    }

    private void WriteBase()
    {
        using var fs = File.Create(_path);
        fs.Write(Encoding.ASCII.GetBytes("HDACSPRJ"));
        fs.WriteByte(3);
        using var gz = new GZipStream(fs, CompressionLevel.Fastest);
        gz.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(BaseDoc(), new JsonSerializerOptions(JsonSerializerDefaults.Web))));
    }

    private static (ProjectService Svc, FakeAcs Server) Build(params string[] tanks)
    {
        var server = new FakeAcs { Tanks = tanks };
        var http = new HttpClient(server) { BaseAddress = new Uri("http://acs.test") };
        return (new ProjectService(new AcsApiClient(http), Options.Create(new AcsOptions { OperatorId = "tester" })), server);
    }

    [Fact]
    public async Task CreatesNewTank_WithNewIds_KeepsPlanData_SkipsCalibration()
    {
        WriteBase();
        var (svc, server) = Build("CT1");   // 베이스 선창이 서버에 남아 있는 상황

        var doc = await svc.CreateFromBaseAsync(_path, " HN8250-CT2 ");

        Assert.Equal("HN8250-CT2", doc.TankId);
        Assert.Null(svc.CurrentPath);   // 저장이 베이스 파일을 덮어쓰지 않는다

        var (path, body) = Assert.Single(server.Writes, w => w.Path.EndsWith("/project-import"));
        Assert.Equal("/api/internal/tanks/HN8250-CT2/project-import", path);
        Assert.Equal(2.9, body.GetProperty("geometry").GetProperty("reachZMax").GetDouble());

        var areas = body.GetProperty("areas").EnumerateArray().ToList();
        Assert.Equal(new[] { "PM-L2-01", "PM-L2-02" }, areas.Select(a => a.GetProperty("name").GetString()));
        var newAreaIds = areas.Select(a => a.GetProperty("areaId").GetGuid()).ToList();
        Assert.DoesNotContain(AreaA, newAreaIds);
        Assert.DoesNotContain(AreaB, newAreaIds);
        Assert.Equal(2, newAreaIds.Distinct().Count());
        Assert.Equal(1.5, areas[0].GetProperty("stationX").GetDouble());          // 정차·리프트 그대로
        Assert.Equal(100, areas[0].GetProperty("stationLiftMm").GetDouble());

        var tasks = areas[0].GetProperty("tasks").EnumerateArray().ToList();
        Assert.Equal(new[] { 1, 5 }, tasks.Select(t => t.GetProperty("seq").GetInt32()));
        var newTaskIds = tasks.Select(t => t.GetProperty("taskId").GetGuid()).ToList();
        Assert.DoesNotContain(Task1, newTaskIds);
        Assert.DoesNotContain(Task2, newTaskIds);
        Assert.False(tasks[1].GetProperty("enabled").GetBoolean());               // 임시 제외 유지

        // 시나리오는 새 선창에 만들고, 영역 연결은 새 영역 ID 로 옮긴다
        var scenario = Assert.Single(server.Writes, w => w.Path == "/api/scenarios").Body;
        Assert.Equal("HN8250-CT2", scenario.GetProperty("tankId").GetString());
        var link = Assert.Single(server.Writes, w => w.Path.EndsWith("/areas") && w.Path.StartsWith("/api/scenarios/")).Body;
        Assert.Equal(newAreaIds[1], Assert.Single(link.GetProperty("areaIds").EnumerateArray()).GetGuid());

        Assert.DoesNotContain(server.Writes, w => w.Path.Contains("calibration"));   // 캘리브레이션은 복사하지 않는다
        Assert.Equal(AreaA, BaseDoc().Areas[0].SourceId);
    }

    [Theory]
    [InlineData("CT1")] [InlineData("ct1")]
    public async Task ExistingTankId_IsRejected_WithoutTouchingDb(string newId)
    {
        WriteBase();
        var (svc, server) = Build("CT1");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateFromBaseAsync(_path, newId));

        Assert.Contains("이미 있습니다", ex.Message);
        Assert.Empty(server.Writes);
    }

    [Theory]
    [InlineData("")] [InlineData("선창2")] [InlineData("-CT2")] [InlineData("CT2-L4")]
    public async Task BadTankIdFormat_IsRejected_WithoutTouchingDb(string newId)
    {
        WriteBase();
        var (svc, server) = Build("CT1");

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateFromBaseAsync(_path, newId));

        Assert.Empty(server.Writes);
        Assert.Empty(server.Gets);   // 서버에 묻기도 전에 거부
    }

    [Fact]
    public void Rebase_DropsScenarioLinksToUnknownAreas()
    {
        var source = BaseDoc() with { Scenarios = new[] { new ScenarioDoc("s", new[] { AreaA, Guid.NewGuid() }) } };

        var doc = ProjectService.Rebase(source, "CT2");

        Assert.Equal(doc.Areas[0].SourceId, Assert.Single(doc.Scenarios!.Single().AreaIds));
        Assert.Empty(doc.Calibrations!);
    }

    /// <summary>HD.Acs.App 흉내 — 쓰기(POST·PUT) 본문을 기록한다.</summary>
    private sealed class FakeAcs : HttpMessageHandler
    {
        public string[] Tanks { get; init; } = Array.Empty<string>();
        public List<(string Path, JsonElement Body)> Writes { get; } = new();
        public List<string> Gets { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Get)
            {
                Gets.Add(path);
                if (path == "/api/internal/tanks") return Json(Tanks);
                if (path == "/api/scenarios") return Json(Array.Empty<object>());
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            var body = JsonDocument.Parse(await req.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
            Writes.Add((path, body));
            if (path.EndsWith("/project-import"))
            {
                var ids = body.GetProperty("areas").EnumerateArray().Select(a => a.GetProperty("areaId").GetGuid()).ToArray();
                return Json(new { tankId = "x", areas = ids.Length, tasks = 0, areaIds = ids });
            }
            if (path == "/api/scenarios") return Json(new { scenarioId = Guid.NewGuid() });
            return Json(new { ok = true });
        }

        private static HttpResponseMessage Json(object o) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(o, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json") };
    }
}
