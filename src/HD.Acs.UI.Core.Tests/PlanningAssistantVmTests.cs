using System.Net;
using System.Text;
using System.Text.Json;
using HD.Acs.UI.Models;
using HD.Acs.UI.Services;
using HD.Acs.UI.ViewModels;
using Microsoft.Extensions.Options;
using Xunit;

namespace HD.Acs.UI.Core.Tests;

/// <summary>
/// 계획 어시스턴트 패널 [ADR-013] — 실제 <see cref="AcsApiClient"/> + 가짜 서버로
/// 제안 요청 본문(현재 면 선택 포함) → 전개도 미리보기 → 적용 → 새로고침·미리보기 해제를 검증한다.
/// </summary>
public class PlanningAssistantVmTests
{
    private static readonly Guid ChangeSet = Guid.Parse("5e7c0000-0000-4000-8000-000000000001");

    private static async Task<(AreaPlanningViewModel Vm, FakeAcs Server)> BuildAsync(FakeAcs server)
    {
        var http = new HttpClient(server) { BaseAddress = new Uri("http://acs.test") };
        var vm = new AreaPlanningViewModel(new AcsApiClient(http), Options.Create(new AcsOptions { OperatorId = "tester" }));
        vm.SelectedWall = new WallDto("CT1", "PM", new[] { -22.5, 6.0, 1.9 }, new[] { 1.0, 0, 0 }, new[] { 0.0, 0, 1 }, new[] { 0.0, -1, 0 },
            45, 5.4, Math.PI / 2, true, null, ReachableVBand: new[] { 0.5, 2.9 });
        await Task.Delay(50);
        return (vm, server);
    }

    private static object Op(int i, string kind, bool ok, string wall, double[][]? corners = null, string? error = null) => new
    {
        index = i, source = 0, kind, ok, error, wallCode = wall, level = 2, areaName = "PM-L2-0" + i, corners, segment = (double[]?)null,
        summary = $"{kind} PM-L2-0{i}",
    };

    private static readonly double[][] Box = { new[] { 3.0, 0.6 }, new[] { 4.0, 0.6 }, new[] { 4.0, 1.6 }, new[] { 3.0, 1.6 } };

    [Fact]
    public async Task Send_ShowsPreviewOnPlot_ThenApply_ClearsAndReloads()
    {
        var server = new FakeAcs
        {
            Propose = new
            {
                changeSetId = ChangeSet, tankId = "CT1", reply = "PM 영역 1개를 만들고 1개를 지웁니다.", messages = new[] { "참고 메시지" },
                results = new[] { Op(1, "createArea", true, "PM", Box), Op(2, "deleteArea", true, "PM", Box), Op(3, "createArea", true, "SM", Box) },
                creates = 2, updates = 0, deletes = 1, failed = 0, allOk = true, hasChanges = true,
            },
        };
        var (vm, _) = await BuildAsync(server);
        var a = vm.Assistant;

        a.Input = "여기에 영역 하나 만들고 PM-L2-02 지워";
        await a.SendCommand.ExecuteAsync(null);

        var (_, path, body) = server.Writes.Single();
        Assert.Equal("/api/planning/assistant/propose", path);
        Assert.Equal("여기에 영역 하나 만들고 PM-L2-02 지워", body.GetProperty("prompt").GetString());
        Assert.Equal("PM", body.GetProperty("wallCode").GetString());        // 현재 화면 면 선택이 힌트로 전달
        Assert.Contains("PM 영역 1개를", a.Lines.Last().Text);
        Assert.Contains("참고 메시지", a.Lines.Last().Text);
        Assert.Equal("변경안: 생성 2 · 수정 0 · 삭제 1", a.ProposalSummary);
        Assert.Single(vm.ProposalAreas);                                       // 선택 면(PM)의 생성만 — SM 은 그리지 않음
        Assert.Single(vm.ProposalRemovals);
        Assert.True(a.ApplyCommand.CanExecute(null));

        int getsBefore = server.Gets;
        await a.ApplyCommand.ExecuteAsync(null);

        var (_, applyPath, applyBody) = server.Writes.Last();
        Assert.Equal($"/api/planning/changesets/{ChangeSet}/apply", applyPath);
        Assert.Equal("tester", applyBody.GetProperty("userId").GetString());
        Assert.Contains("적용 완료 — 생성 2", a.Lines.Last().Text);
        Assert.Null(a.Proposal);
        Assert.Empty(vm.ProposalAreas);
        Assert.Empty(vm.ProposalRemovals);
        Assert.True(server.Gets > getsBefore);                                 // 적용 후 계획 데이터 새로고침
    }

    [Fact]
    public async Task FailedOps_ListedFirst_ApplyDisabled()
    {
        var server = new FakeAcs
        {
            Propose = new
            {
                changeSetId = (Guid?)null, tankId = "CT1", reply = "만듭니다.", messages = Array.Empty<string>(),
                results = new[] { Op(1, "createArea", true, "PM", Box), Op(2, "createArea", false, "PM", Box, "면 PM 내에 영역 'PM-L2-02'이(가) 이미 있습니다.") },
                creates = 2, updates = 0, deletes = 0, failed = 1, allOk = false, hasChanges = true,
            },
        };
        var (vm, _) = await BuildAsync(server);
        vm.Assistant.Input = "영역 두 개";
        await vm.Assistant.SendCommand.ExecuteAsync(null);

        Assert.False(vm.Assistant.ApplyCommand.CanExecute(null));
        Assert.False(vm.Assistant.ProposalOps[0].Ok);
        Assert.Contains("실패 1", vm.Assistant.ProposalSummary);
        Assert.Contains("적용할 수 없습니다", vm.Assistant.StatusText);
    }

    [Fact]
    public async Task NoOps_ShowsWhyNothingHappened_AndInterpretationWhenPresent()
    {
        var server = new FakeAcs
        {
            Propose = new
            {
                changeSetId = (Guid?)null, tankId = "CT1", reply = "F-SM-A0001 영역의 이름을 F-A0001로 변경합니다.", messages = Array.Empty<string>(),
                results = Array.Empty<object>(), creates = 0, updates = 0, deletes = 0, failed = 0, allOk = true, hasChanges = false,
                opsSummary = Array.Empty<string>(),
            },
        };
        var (vm, _) = await BuildAsync(server);
        vm.Assistant.Input = "F-SM-A0001을 F-A0001로 변경해줘";
        await vm.Assistant.SendCommand.ExecuteAsync(null);
        Assert.Contains("실행할 변경을 만들지 못했습니다", vm.Assistant.Lines.Last().Text);

        server.Propose = new
        {
            changeSetId = ChangeSet, tankId = "CT1", reply = "바꿉니다.", messages = Array.Empty<string>(),
            results = new[] { Op(1, "updateArea", true, "PM", Box) }, creates = 0, updates = 1, deletes = 0, failed = 0, allOk = true, hasChanges = true,
            opsSummary = new[] { "renameAreas(areaName=F-SM-A0001, name=F-A0001)" },
        };
        vm.Assistant.Input = "다시";
        await vm.Assistant.SendCommand.ExecuteAsync(null);
        Assert.Contains("해석: renameAreas(areaName=F-SM-A0001, name=F-A0001)", vm.Assistant.Lines.Last().Text);
        Assert.DoesNotContain("만들지 못했습니다", vm.Assistant.Lines.Last().Text);
        Assert.True(vm.Assistant.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public async Task ServerDisabled_ShowsReasonInChat()
    {
        var server = new FakeAcs { ProposeStatus = HttpStatusCode.ServiceUnavailable };
        var (vm, _) = await BuildAsync(server);
        vm.Assistant.Input = "아무거나";
        await vm.Assistant.SendCommand.ExecuteAsync(null);

        Assert.Contains("Acs:Llm:Enabled", vm.Assistant.Lines.Last().Text);
        Assert.Null(vm.Assistant.Proposal);
        Assert.False(vm.Assistant.IsBusy);
    }

    private sealed class FakeAcs : HttpMessageHandler
    {
        public object? Propose { get; set; }
        public HttpStatusCode ProposeStatus { get; set; } = HttpStatusCode.OK;
        public List<(string Method, string Path, JsonElement Body)> Writes { get; } = new();
        public int Gets;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Get)
            {
                Interlocked.Increment(ref Gets);
                return path.StartsWith("/api/internal/") && !path.Contains("geometry") ? Json(Array.Empty<object>()) : new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            Writes.Add((req.Method.Method, path, JsonDocument.Parse(await req.Content!.ReadAsStringAsync(ct)).RootElement.Clone()));
            if (path == "/api/planning/assistant/propose")
                return ProposeStatus == HttpStatusCode.OK
                    ? Json(Propose!)
                    : Json(new { error = "계획 어시스턴트가 비활성입니다 — 서버 appsettings.json 의 Acs:Llm:Enabled 를 true 로 하세요." }, ProposeStatus);
            if (path.EndsWith("/apply"))
                return Json(new { changeSetId = ChangeSet, applied = 3, creates = 2, updates = 0, deletes = 1 });
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(object o, HttpStatusCode status = HttpStatusCode.OK) => new(status)
        { Content = new StringContent(JsonSerializer.Serialize(o, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json") };
    }
}
