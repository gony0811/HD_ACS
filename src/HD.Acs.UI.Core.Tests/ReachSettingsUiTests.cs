using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using HD.Acs.UI.Services;
using HD.Acs.UI.ViewModels;
using Xunit;

namespace HD.Acs.UI.Core.Tests;

/// <summary>리치·간섭 화면의 리프트·차체 설정 — 바뀐 항목만 저장하고, 저장하면 다시 조사한다.</summary>
public class ReachSettingsUiTests
{
    private sealed class Server : HttpMessageHandler
    {
        public string Settings =
            "{\"liftMode\":\"Off\",\"fixedStrokeMm\":null,\"mountZMm\":927,\"amrHeightMm\":765,\"supportColumn\":true," +
            "\"maxEntryJointTravelDeg\":160,\"overridden\":[],\"overrideFile\":\"x\",\"warning\":null}";
        public JsonNode? PutBody;
        public int Surveys, Deletes;
        public HttpStatusCode PutStatus = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/settings/reach-sim")
            {
                if (req.Method == HttpMethod.Put)
                {
                    PutBody = JsonNode.Parse(await req.Content!.ReadAsStringAsync(ct));
                    if (PutStatus != HttpStatusCode.OK)
                        return new HttpResponseMessage(PutStatus) { Content = Json("{\"error\":\"진행 중인 run 이 있어 바꿀 수 없습니다\"}") };
                    Settings = "{\"liftMode\":\"Fixed\",\"fixedStrokeMm\":300,\"mountZMm\":927,\"amrHeightMm\":765,\"supportColumn\":true," +
                               "\"maxEntryJointTravelDeg\":160,\"overridden\":[\"Acs:Lift:Mode\",\"Acs:Lift:FixedStrokeMm\"]}";
                }
                if (req.Method == HttpMethod.Delete) Deletes++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(Settings) };
            }
            if (path.EndsWith("/survey")) { Surveys++; return new HttpResponseMessage(HttpStatusCode.OK) { Content = Json("[]") }; }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static StringContent Json(string s) => new(s, Encoding.UTF8, "application/json");
    }

    private static (ReachSimViewModel Vm, Server S) Make()
    {
        var s = new Server();
        var http = new HttpClient(s) { BaseAddress = new Uri("http://acs.test") };
        return (new ReachSimViewModel(new AcsApiClient(http)), s);
    }

    [Fact]
    public async Task 서버_설정을_읽어_입력칸을_채운다()
    {
        var (vm, _) = Make();
        await vm.LoadSettingsAsync();
        Assert.True(vm.SettingsAvailable);
        Assert.Equal(0, vm.LiftModeIndex);
        Assert.True(vm.IsLiftOff);
        Assert.Equal(927, vm.SettingMountZMm);
        Assert.Equal(765, vm.SettingAmrHeightMm);
        Assert.True(vm.SettingSupportColumn);
        Assert.Contains("리프트 미사용", vm.SettingsStatus);
        Assert.Contains("appsettings.json 값", vm.SettingsStatus);
    }

    [Fact]
    public void AMR_보고_값_수를_요약에_보인다()
    {
        var s = new HD.Acs.UI.Models.ReachSettingsDto("Off", null, 927, 765, true, 160,
            Overridden: new[] { "Acs:Lift:Mode" }, AmrReported: new[] { "Acs:Reach:MountZMm", "Acs:Lift:Mode" });
        var text = ReachSimViewModel.DescribeSettings(s);
        Assert.Contains("화면에서 바꾼 항목 1개", text);
        Assert.Contains("AMR 보고 값 2개", text);
        Assert.DoesNotContain("appsettings.json 값", text);
    }

    [Fact]
    public async Task 바꾼_항목만_저장하고_다시_조사한다()
    {
        var (vm, s) = Make();
        await vm.LoadSettingsAsync();
        vm.LiftModeIndex = 2;   // 고정
        vm.SettingFixedStrokeMm = 300;
        await vm.SaveSettingsAsync();

        Assert.Equal("Fixed", (string?)s.PutBody!["liftMode"]);
        Assert.Equal(300, (double?)s.PutBody!["fixedStrokeMm"]);
        Assert.Null(s.PutBody!["mountZMm"]);              // 안 바꾼 항목은 appsettings.json 값 그대로
        Assert.Null(s.PutBody!["supportColumn"]);
        Assert.Equal(1, s.Surveys);                       // 적용 뒤 다시 조사
        Assert.True(vm.IsFixedLift);
        Assert.Contains("적용됨", vm.SettingsStatus);
        Assert.Contains("리프트 고정 300 mm", vm.SettingsStatus);

        // 이미 덮어쓴 항목은 다음 저장에도 유지(BASE 만 바꿔도 리프트 고정이 풀리지 않게)
        vm.SettingMountZMm = 1635;
        var u = vm.BuildSettingsUpdate();
        Assert.Equal("Fixed", u.LiftMode);
        Assert.Equal(300, u.FixedStrokeMm);
        Assert.Equal(1635, u.MountZMm);
        Assert.Null(u.AmrHeightMm);
    }

    [Fact]
    public async Task 서버가_거부하면_사유를_보이고_조사하지_않는다()
    {
        var (vm, s) = Make();
        await vm.LoadSettingsAsync();
        s.PutStatus = HttpStatusCode.Conflict;
        vm.LiftModeIndex = 1;
        await vm.SaveSettingsAsync();
        Assert.Contains("진행 중인 run", vm.SettingsStatus);
        Assert.Equal(0, s.Surveys);
        Assert.Equal(1, vm.LiftModeIndex);   // 입력값은 그대로 남긴다
    }

    [Fact]
    public async Task 초기화하면_appsettings_값으로_되돌리고_다시_조사한다()
    {
        var (vm, s) = Make();
        await vm.LoadSettingsAsync();
        await vm.ResetSettingsAsync();
        Assert.Equal(1, s.Deletes);
        Assert.Equal(1, s.Surveys);
        Assert.Contains("화면 설정 지움", vm.SettingsStatus);
    }
}
