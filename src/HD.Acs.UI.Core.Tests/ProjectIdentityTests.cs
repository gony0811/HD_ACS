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
/// .hdacs 식별자 보존 [SAIGE v2.6 §2.5] — taskId는 용접선 1구간의 영구 식별자라 저장→열기 왕복에서 바뀌면 안 된다.
/// 실제 <see cref="AcsApiClient"/>를 가짜 HTTP 서버에 붙여 **전송되는 JSON 본문**까지 검증한다.
/// </summary>
public class ProjectIdentityTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"hdacs-test-{Guid.NewGuid():N}.hdacs");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    private static readonly Guid AreaA = Guid.Parse("c11e0000-0000-4000-8000-00000000000a");
    private static readonly Guid Task1 = Guid.Parse("3a9f2c14-8e51-4d7a-b2c9-1f6e0a5d3b47");
    private static readonly Guid Task2 = Guid.Parse("3a9f2c14-8e51-4d7a-b2c9-1f6e0a5d3b48");

    private static (ProjectService Svc, FakeAcs Server) Build()
    {
        var server = new FakeAcs();
        var http = new HttpClient(server) { BaseAddress = new Uri("http://acs.test") };
        return (new ProjectService(new AcsApiClient(http), Options.Create(new AcsOptions { OperatorId = "tester" })), server);
    }

    [Fact]
    public async Task SaveThenOpen_PreservesAreaIdTaskIdSeqAndName()
    {
        var (svc, server) = Build();

        await svc.SaveAsync("CT1", _path);
        server.Posts.Clear();
        await svc.OpenAsync(_path);

        var area = Assert.Single(server.Posts, p => p.Path == "/api/areas").Body;
        Assert.Equal(AreaA, area.GetProperty("areaId").GetGuid());

        var tasks = server.Posts.Where(p => p.Path == $"/api/areas/{AreaA}/tasks").Select(p => p.Body).ToList();
        Assert.Equal(new[] { Task1, Task2 }, tasks.Select(t => t.GetProperty("taskId").GetGuid()));
        Assert.Equal(new[] { 1, 5 }, tasks.Select(t => t.GetProperty("seq").GetInt32()));          // seq 비연속도 그대로(재부여 금지)
        Assert.Equal(new[] { "W1", "W5-cross" }, tasks.Select(t => t.GetProperty("name").GetString()));   // 종전엔 name 유실
        Assert.Equal("CROSS4", tasks[1].GetProperty("seamType").GetString());
    }

    /// <summary>구파일(v2) — 식별자 없음 → 필드를 null로 보내 서버가 새로 발급(열기 자체는 성공).</summary>
    [Fact]
    public async Task LegacyV2File_OpensWithServerIssuedIds()
    {
        var (svc, server) = Build();
        var v2 = """
        {"version":2,"tankId":"CT1",
         "geometry":{"lengthL":45,"wFloor":8.2,"thetaLowDeg":45,"hLow":1.9,"hWall":5.4,"thetaUpDeg":45,"hUp":1.9,"levelZ":[0,2.4],"originOx":0,"originOy":0},
         "areas":[{"wallCode":"PM","level":1,"name":"PM-L1-01","uMin":3,"vMin":0.2,"uMax":6,"vMax":1.5,
                   "tasks":[{"seq":1,"name":null,"seamType":"LINE","startU":3.2,"startV":0.5,"endU":5.8,"endV":0.5,"sectionDxfId":"","profileId":""}]}]}
        """;
        WriteContainer(_path, formatVersion: 2, v2);

        await svc.OpenAsync(_path);

        var task = Assert.Single(server.Posts, p => p.Path.EndsWith("/tasks")).Body;
        Assert.Equal(JsonValueKind.Null, task.GetProperty("taskId").ValueKind);
        Assert.Equal(JsonValueKind.Null, Assert.Single(server.Posts, p => p.Path == "/api/areas").Body.GetProperty("areaId").ValueKind);
    }

    /// <summary>중복·빈 식별자는 DB를 건드리기 전에 거부 — 서버 409로 중간에 멈춰 반쯤 적재되는 것을 막는다.</summary>
    [Theory]
    [InlineData("dupTask")] [InlineData("emptyTask")] [InlineData("dupArea")] [InlineData("dupSeq")]
    public async Task CorruptIdentities_RejectedBeforeTouchingServer(string kind)
    {
        var (svc, server) = Build();
        TaskDoc T(int seq, Guid? id) => new(seq, null, "LINE", 1, 1, 2, 1, "", "", id);
        AreaDoc A(string name, Guid? id, params TaskDoc[] t) => new("PM", 1, name, 0, 0, 1, 1, null, null, null, t, null, null, id);
        var areas = kind switch
        {
            "dupTask" => new[] { A("a", AreaA, T(1, Task1)), A("b", Guid.NewGuid(), T(1, Task1)) },   // 영역을 넘는 중복도 검출
            "emptyTask" => new[] { A("a", AreaA, T(1, Guid.Empty)) },
            "dupArea" => new[] { A("a", AreaA), A("b", AreaA) },
            _ => new[] { A("a", AreaA, T(1, Task1), T(1, Task2)) },
        };
        var doc = new ProjectDoc(3, "CT1", new GeometryDoc(45, 8.2, 45, 1.9, 5.4, 45, 1.9, new[] { 0.0 }, 0, 0), areas);
        WriteContainer(_path, 3, JsonSerializer.Serialize(doc, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        await Assert.ThrowsAsync<InvalidDataException>(() => svc.OpenAsync(_path));
        Assert.Empty(server.Posts);   // 선창 재등록(=기존 영역 CASCADE 삭제)조차 시작하지 않음
    }

    [Fact]
    public async Task NewerFormat_IsRejected()
    {
        var (svc, _) = Build();
        WriteContainer(_path, formatVersion: 4, "{}");
        await Assert.ThrowsAsync<InvalidDataException>(() => svc.OpenAsync(_path));
    }

    private static void WriteContainer(string path, int formatVersion, string json)
    {
        using var fs = File.Create(path);
        fs.Write(Encoding.ASCII.GetBytes("HDACSPRJ"));
        fs.WriteByte((byte)formatVersion);
        using var gz = new GZipStream(fs, CompressionLevel.Fastest);
        gz.Write(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>HD.Acs.App 흉내 — GET은 고정 스냅샷, POST는 본문을 기록하고 요청의 식별자를 그대로 응답.</summary>
    private sealed class FakeAcs : HttpMessageHandler
    {
        public List<(string Path, JsonElement Body)> Posts { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Post)
            {
                var body = JsonDocument.Parse(await req.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
                Posts.Add((path, body));
                if (path == "/api/areas")
                    return Json(new { areaId = body.TryGetProperty("areaId", out var a) && a.ValueKind == JsonValueKind.String ? a.GetGuid() : Guid.NewGuid(), level = 1 });
                if (path.EndsWith("/tasks"))
                    return Json(new { taskId = Guid.NewGuid(), seq = body.GetProperty("seq").ValueKind == JsonValueKind.Number ? body.GetProperty("seq").GetInt32() : 1 });
                return Json(new { tankId = "CT1", wallsGenerated = 10 });
            }

            if (path == "/api/internal/tanks/CT1/geometry")
                return Json(new
                {
                    tankId = "CT1", lengthL = 45.0, wFloor = 8.2, thetaLowDeg = 45.0, hLow = 1.9, hWall = 5.4, thetaUpDeg = 45.0, hUp = 1.9,
                    levelZ = new[] { 0.0, 2.4, 4.8, 7.2 }, originOx = 0.0, originOy = 0.0,
                    derived = new { wLow = 1.9, b = 12.0, wUp = 1.9, wCeil = 8.2, h = 9.2 },
                });
            if (path == "/api/internal/areas")
                return Json(new[]
                {
                    new
                    {
                        areaId = AreaA, tankId = "CT1", wallCode = "PM", level = 2, name = "PM-L2-01",
                        corners = new[] { new[] { 3.0, 0.6 }, new[] { 6.0, 0.6 }, new[] { 6.0, 2.4 }, new[] { 3.0, 2.4 } },
                        uMin = 3.0, vMin = 0.6, uMax = 6.0, vMax = 2.4, sortOrder = 0, taskCount = 2,
                    },
                });
            if (path == $"/api/internal/areas/{AreaA}/tasks")
                return Json(new object[]
                {
                    new { taskId = Task1, seq = 1, name = "W1", seamType = "LINE", startU = 3.2, startV = 0.9, endU = 5.8, endV = 0.9, sectionDxfId = "DXF-1", profileId = "PROF-1" },
                    new { taskId = Task2, seq = 5, name = "W5-cross", seamType = "CROSS4", startU = 4.0, startV = 1.2, endU = 4.4, endV = 1.2, sectionDxfId = "DXF-1", profileId = "PROF-1" },
                });
            if (path == "/api/scenarios")
                return Json(Array.Empty<object>());
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(object o) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(o, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json") };
    }
}
