using System.Collections.ObjectModel;
using System.Net;
using System.Text;
using System.Text.Json;
using HD.Acs.UI.Services;
using HD.Acs.UI.ViewModels;
using Microsoft.Extensions.Options;
using Xunit;

namespace HD.Acs.UI.Core.Tests;

/// <summary>선창 ID 변경 — 계획 화면의 입력·확인·요청과, 층 맵 ID 를 쓰는 화면들의 따라가기.</summary>
public class TankRenameUiTests
{
    private static (AreaPlanningViewModel Vm, FakeAcs Server) Make()
    {
        var server = new FakeAcs();
        var http = new HttpClient(server) { BaseAddress = new Uri("http://acs.test") };
        return (new AreaPlanningViewModel(new AcsApiClient(http), Options.Create(new AcsOptions { OperatorId = "tester" })), server);
    }

    [Fact]
    public async Task Rename_PostsNewId_AfterConfirm_AndAnnouncesIt()
    {
        var (vm, server) = Make();
        string? asked = null, announced = null;
        vm.ConfirmAsync = (message, _) => { asked = message; return Task.FromResult(true); };
        vm.TankRenamed += (_, id) => announced = id;

        Assert.False(vm.RenameTankCommand.CanExecute(null));          // 입력 전
        vm.NewTankId = "CT1";
        Assert.False(vm.RenameTankCommand.CanExecute(null));          // 지금 ID 와 같음
        vm.NewTankId = "  HN8250-CT2 ";
        Assert.True(vm.RenameTankCommand.CanExecute(null));

        await vm.RenameTankCommand.ExecuteAsync(null);

        var (method, path, body) = Assert.Single(server.Writes);
        Assert.Equal(("POST", "/api/tanks/CT1/rename"), (method, path));
        Assert.Equal(("HN8250-CT2", "tester"), (body.GetProperty("newTankId").GetString(), body.GetProperty("userId").GetString()));
        Assert.Contains("'CT1' → 'HN8250-CT2'", asked);
        Assert.Contains("파일 ▸ 저장", asked);
        Assert.Equal(("HN8250-CT2", "HN8250-CT2", ""), (vm.TankId, announced, vm.NewTankId));
        Assert.Contains("파일 ▸ 저장", vm.StatusMessage);
        Assert.False(vm.RenameTankCommand.CanExecute(null));          // 입력칸이 비워졌다
    }

    [Fact]
    public async Task Rename_DoesNothing_WhenDeclined_AndShowsServerReason_WhenRefused()
    {
        var (vm, server) = Make();
        vm.NewTankId = "CT2";
        vm.ConfirmAsync = (_, _) => Task.FromResult(false);
        await vm.RenameTankCommand.ExecuteAsync(null);
        Assert.Empty(server.Writes);
        Assert.Equal("CT1", vm.TankId);

        // 진행 중 run 이 있으면 서버가 409 로 거부 — 사유가 상태줄에 보이고 화면의 선창 ID 는 그대로
        vm.ConfirmAsync = null;
        server.Status = HttpStatusCode.Conflict;
        var renamed = false;
        vm.TankRenamed += (_, _) => renamed = true;
        await vm.RenameTankCommand.ExecuteAsync(null);
        Assert.Contains("진행 중인 run", vm.StatusMessage);
        Assert.Equal(("CT1", false, "CT2"), (vm.TankId, renamed, vm.NewTankId));

        // 경로가 없는 구빌드 서버
        server.Status = HttpStatusCode.NotFound;
        await vm.RenameTankCommand.ExecuteAsync(null);
        Assert.Contains("구빌드", vm.StatusMessage);
        Assert.Equal("CT1", vm.TankId);
    }

    [Fact]
    public void Floors_FollowTheTankId_AndKeepTheSelectedLevel()
    {
        var floors = new ObservableCollection<TankFloor>(TankLayout.Floors);
        Assert.False(TankLayout.Retarget(floors, TankLayout.DefaultTankId));   // 같은 선창이면 건드리지 않는다

        Assert.True(TankLayout.Retarget(floors, "HN8250-CT2"));
        Assert.Equal(new[] { "HN8250-CT2-L1", "HN8250-CT2-L2", "HN8250-CT2-L3", "HN8250-CT2-L4" }, floors.Select(f => f.MapId));
        Assert.Equal(new[] { "L1", "L2", "L3", "L4" }, floors.Select(f => f.Level));
        Assert.Equal("X-L3", TankLayout.FloorsFor("X")[2].MapId);
        Assert.Equal("CT1-L1", TankLayout.Floors[0].MapId);                    // 기본 목록은 그대로

        var http = new HttpClient(new FakeAcs()) { BaseAddress = new Uri("http://acs.test") };
        var swap = new BatterySwapNodesViewModel(new AcsApiClient(http));
        swap.SelectedFloor = swap.Floors[3];
        swap.SetTank("HN8250-CT2");
        Assert.Equal(("L4", "HN8250-CT2-L4"), (swap.SelectedFloor!.Level, swap.SelectedFloor.MapId));   // 같은 층을 새 맵 ID 로 다시 고른다
    }

    private sealed class FakeAcs : HttpMessageHandler
    {
        public List<(string Method, string Path, JsonElement Body)> Writes { get; } = new();
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Get) return new HttpResponseMessage(HttpStatusCode.NotFound);
            Writes.Add((req.Method.Method, path, JsonDocument.Parse(await req.Content!.ReadAsStringAsync(ct)).RootElement.Clone()));
            return Status switch
            {
                HttpStatusCode.OK => Json(new { oldTankId = "CT1", newTankId = "x" }),
                HttpStatusCode.NotFound => new HttpResponseMessage(HttpStatusCode.NotFound),   // 본문 없는 404 = 경로 없음
                _ => Json(new { error = "진행 중인 run(…)이 있습니다 — 중단(abort)하거나 끝난 뒤에 선창 ID 를 바꾸세요." }, Status),
            };
        }

        private static HttpResponseMessage Json(object o, HttpStatusCode status = HttpStatusCode.OK) => new(status)
        { Content = new StringContent(JsonSerializer.Serialize(o, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json") };
    }
}
