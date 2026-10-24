using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using HD.Acs.UI.Models;
using HD.Acs.UI.Services;
using HD.Acs.UI.ViewModels;
using Xunit;

namespace HD.Acs.UI.Core.Tests;

/// <summary>
/// 프로젝트 열기 후 화면 갱신 — 3D·전개도 오버레이 로드.
/// 종전엔 영역마다 작업 GET(대형 선창 1,276회 순차)이었고, 열기 한 번에 이 로드가 최대 3번 겹쳐 돌며
/// (Tank.LoadAsync + AreaPlanning의 PlanningChanged 2회) 같은 Overlays 컬렉션을 번갈아 Clear/Add 해 중복이 생겼다.
/// </summary>
public class OverlayLoadTests
{
    private const int AreaCount = 30;

    /// <summary>선창 CT1 — 영역 AreaCount개·영역당 작업 2개. 응답마다 비동기 지연(동시 로드 교차 재현).</summary>
    private sealed class FakeTank : HttpMessageHandler
    {
        public bool Bulk { get; init; }
        public List<string> Gets { get; } = new();
        public readonly Guid[] AreaIds = Enumerable.Range(1, AreaCount).Select(i => new Guid(i, 0, 0, new byte[8])).ToArray();

        private object Task(Guid areaId, int seq, bool withArea) => withArea
            ? new { areaId, taskId = Guid.NewGuid(), seq, name = $"W{seq}", seamType = "LINE", startU = 0.2, startV = 0.2, endU = 1.0, endV = 0.2, sectionDxfId = "D", profileId = "P" }
            : new { taskId = Guid.NewGuid(), seq, name = $"W{seq}", seamType = "LINE", startU = 0.2, startV = 0.2, endU = 1.0, endV = 0.2, sectionDxfId = "D", profileId = "P" };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            lock (Gets) Gets.Add(path);
            await System.Threading.Tasks.Task.Delay(1, ct);   // 실제 네트워크처럼 await에서 다른 로드와 교차
            if (path == "/api/internal/areas")
                return Json(AreaIds.Select((id, i) => new
                {
                    areaId = id, tankId = "CT1", wallCode = "PM", level = 1, name = $"PM-{i + 1:000}",
                    corners = new[] { new[] { 0.0, 0.0 }, new[] { 1.4, 0.0 }, new[] { 1.4, 1.4 }, new[] { 0.0, 1.4 } },
                    uMin = 0.0, vMin = 0.0, uMax = 1.4, vMax = 1.4, sortOrder = 0, taskCount = 2,
                }));
            if (path == "/api/internal/area-tasks")
                return Bulk ? Json(AreaIds.SelectMany(id => new[] { Task(id, 1, true), Task(id, 2, true) })) : new HttpResponseMessage(HttpStatusCode.NotFound);
            if (path.StartsWith("/api/internal/areas/") && path.EndsWith("/tasks"))
            {
                var id = Guid.Parse(path.Split('/')[4]);
                return Json(new[] { Task(id, 1, false), Task(id, 2, false) });
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(object o) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(o, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json") };
    }

    /// <summary>이벤트 구독만 받는 모니터링 대역.</summary>
    public class NoMonitoring : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? m, object?[]? args) =>
            m!.ReturnType == typeof(Task) ? Task.CompletedTask : null;
    }

    private static (TankViewModel Vm, FakeTank Server) Build(bool bulk)
    {
        var server = new FakeTank { Bulk = bulk };
        var api = new AcsApiClient(new HttpClient(server) { BaseAddress = new Uri("http://acs.test") });
        return (new TankViewModel(api, DispatchProxy.Create<IMonitoringClient, NoMonitoring>()) { TankId = "CT1" }, server);
    }

    [Fact]
    public async Task BulkServer_OneTaskQuery_NoPerAreaGets()
    {
        var (vm, server) = Build(bulk: true);

        await vm.LoadOverlaysAsync();

        Assert.Equal(AreaCount, vm.Overlays.Count);
        Assert.All(vm.Overlays, o => Assert.Equal(2, o.Tasks.Count));
        Assert.Equal(new[] { 1, 2 }, vm.Overlays[0].Tasks.Select(t => t.Seq));
        Assert.Single(server.Gets, p => p == "/api/internal/area-tasks");
        Assert.DoesNotContain(server.Gets, p => p.StartsWith("/api/internal/areas/"));
    }

    [Fact]
    public async Task OldServer_FallsBackToPerArea_SameResult()
    {
        var (vm, server) = Build(bulk: false);

        await vm.LoadOverlaysAsync();

        Assert.Equal(AreaCount, vm.Overlays.Count);
        Assert.All(vm.Overlays, o => Assert.Equal(2, o.Tasks.Count));
        Assert.Equal(AreaCount, server.Gets.Count(p => p.StartsWith("/api/internal/areas/")));
    }

    /// <summary>진행 중 run 채택 API 대역 — work_item 3개(영역 A·B·외부 선창 X), 작업 조회 경로를 센다.</summary>
    public class RunApi : DispatchProxy
    {
        public static readonly Guid RunId = Guid.NewGuid(), A = Guid.NewGuid(), B = Guid.NewGuid(), X = Guid.NewGuid();
        public static int BulkCalls, PerAreaCalls;
        public static readonly List<Guid> PerAreaIds = new();

        private static TankAreaTaskDto T(Guid area, int seq) =>
            new(area, Guid.NewGuid(), seq, $"W{seq}", "LINE", 0, 0, 1, 0, "D", "P");

        protected override object? Invoke(MethodInfo? m, object?[]? args)
        {
            object? v = m!.Name switch
            {
                nameof(IAcsApiClient.GetRunsAsync) => new[] { new RunSummaryDto(RunId, Guid.NewGuid(), "S", "CT1", "R1", "RUNNING", null, null) },
                nameof(IAcsApiClient.GetRunAsync) => new ScenarioRunDto(RunId, Guid.NewGuid(), 1, "R1", "RUNNING", null, null, new List<MissionDto>()),
                nameof(IAcsApiClient.GetWorkItemsAsync) => new[]
                {
                    new WorkItemDto(Guid.NewGuid(), A, "A", 1, "CT1-L1", 1, "PENDING", 0),
                    new WorkItemDto(Guid.NewGuid(), B, "B", 1, "CT1-L1", 2, "PENDING", 0),
                    new WorkItemDto(Guid.NewGuid(), X, "X", 1, "CT2-L1", 3, "PENDING", 0),   // 다른 선창 — 일괄 결과에 없음
                },
                nameof(IAcsApiClient.GetTankAreaTasksAsync) => Count(ref BulkCalls, new[] { T(A, 2), T(A, 1), T(B, 1) }),
                nameof(IAcsApiClient.GetAreaTasksAsync) => PerArea((Guid)args![0]!),
                _ => null,
            };
            var t = m.ReturnType.IsGenericType ? m.ReturnType.GetGenericArguments()[0] : null;
            if (t is null) return Task.CompletedTask;
            if (v is null && t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
                v = Array.CreateInstance(t.GetGenericArguments()[0], 0);
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(t).Invoke(null, new[] { v });
        }

        private static object Count(ref int n, object v) { n++; return v; }
        private static object PerArea(Guid id)
        {
            PerAreaCalls++; PerAreaIds.Add(id);
            return new[] { new AreaTaskDto(Guid.NewGuid(), 1, "X1", "LINE", 0, 0, 1, 0, "D", "P") };
        }
    }

    [Fact]
    public async Task ActiveRun_WorkItemLines_FromOneBulkQuery()
    {
        RunApi.BulkCalls = 0; RunApi.PerAreaCalls = 0; RunApi.PerAreaIds.Clear();
        var vm = new MissionViewModel(DispatchProxy.Create<IAcsApiClient, RunApi>(), DispatchProxy.Create<IMonitoringClient, NoMonitoring>())
        { TankId = "CT1" };

        await vm.LoadAsync();   // 진행 중 run 채택 → RefreshRunAsync

        Assert.Equal(1, RunApi.BulkCalls);
        Assert.Equal(new[] { RunApi.X }, RunApi.PerAreaIds);   // 일괄 결과에 없는 다른 선창 영역만 개별 조회
        var rows = vm.WorkItems.ToDictionary(w => w.AreaId);
        Assert.Equal(new[] { 1, 2 }, rows[RunApi.A].Tasks.Select(t => t.Seq));   // seq 순
        Assert.Single(rows[RunApi.B].Tasks);
        Assert.Single(rows[RunApi.X].Tasks);
    }

    /// <summary>열기 재현 — 같은 시점에 3번 요청돼도 오버레이 중복 없음, 실제 조회는 최대 2번(진행 중 1 + 끝난 뒤 1).</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConcurrentLoads_NoDuplicates_Coalesced(bool bulk)
    {
        var (vm, server) = Build(bulk);

        await Task.WhenAll(vm.LoadOverlaysAsync(), vm.LoadOverlaysAsync(), vm.LoadOverlaysAsync());

        Assert.Equal(AreaCount, vm.Overlays.Count);
        Assert.Equal(AreaCount, vm.Overlays.Select(o => o.Area.AreaId).Distinct().Count());
        Assert.InRange(server.Gets.Count(p => p == "/api/internal/areas"), 1, 2);
    }
}
