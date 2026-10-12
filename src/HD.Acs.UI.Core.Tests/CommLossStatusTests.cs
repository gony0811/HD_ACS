using System.Reflection;
using HD.Acs.UI.Models;
using HD.Acs.UI.Services;
using HD.Acs.UI.ViewModels;
using Xunit;

namespace HD.Acs.UI.Core.Tests;

/// <summary>
/// 통신 단절 Fail-Safe UI 반응 가드 [HD_AMR 통신단절 Fail-Safe §6/§7].
///  · 로봇 상태 카드: communicationLostPause → "통신 복구 — 재개 확인 필요" 배너 + '작업 재개'(stopPause) 활성
///  · 현재 상태 판정 우선순위: 비상정지 > 배터리 CRITICAL > 통신 단절 PAUSE > 배터리 LOW > 정상
/// </summary>
public class CommLossStatusTests
{
    private sealed class FakeMonitoring : IMonitoringClient
    {
#pragma warning disable CS0067
        public HubStatus Status => HubStatus.Connected;
        public event EventHandler<HubStatus>? StatusChanged;
        public event EventHandler<RobotStateDto>? RobotStateReceived;
        public event EventHandler<RobotConnectionDto>? RobotConnectionReceived;
        public event EventHandler<MissionProgressDto>? MissionProgressReceived;
        public event EventHandler<RunProgressDto>? RunProgressReceived;
        public event EventHandler<RunStateDto>? RunStateReceived;
        public event EventHandler<WorkItemProgressDto>? WorkItemProgressReceived;
        public event EventHandler<TaskActionProgressDto>? TaskActionProgressReceived;
        public event EventHandler<AlarmDto>? AlarmRaised;
        public event EventHandler<BatterySwapDispatchedDto>? BatterySwapDispatched;
#pragma warning restore CS0067
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public void EmitState(RobotStateDto s) => RobotStateReceived?.Invoke(this, s);
    }

    /// <summary>호출된 API 메서드 이름을 기록하는 기본값 대역.</summary>
    public class RecordingApi : DispatchProxy
    {
        public static readonly List<string> Calls = new();
        protected override object? Invoke(MethodInfo? m, object?[]? args)
        {
            lock (Calls) Calls.Add(m!.Name);
            var rt = m.ReturnType;
            if (rt == typeof(Task)) return Task.CompletedTask;
            if (rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var t = rt.GetGenericArguments()[0];
                var v = t.IsValueType ? Activator.CreateInstance(t) : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(t).Invoke(null, new[] { v });
            }
            return rt.IsValueType ? Activator.CreateInstance(rt) : null;
        }
    }

    private static RobotStateDto State(params string[] errors) =>
        new("AMR-01", "CT1-L1", 1, 2, 80, "o", null, false, errors.Length, null, "AUTOMATIC", "NONE", errors, Paused: errors.Length > 0);

    private static (RobotStatusViewModel vm, FakeMonitoring mon) Build()
    {
        var api = DispatchProxy.Create<IAcsApiClient, RecordingApi>();
        var mon = new FakeMonitoring();
        var vm = new RobotStatusViewModel(api, mon);
        vm.Robots.Add(new RobotDto("AMR-01", "AMR-01", "HD", "AMR-01", "2.0", true));
        vm.SelectedRobot = vm.Robots[0];
        return (vm, mon);
    }

    [Fact]
    public void CommLossPause_ShowsBanner_AndEnablesResume()
    {
        var (vm, mon) = Build();
        Assert.False(vm.CommLossPaused);
        Assert.False(vm.ResumePauseCommand.CanExecute(null));

        mon.EmitState(State("communicationLostPause: HOLD t0=10:00:00 경과 60s"));
        Assert.True(vm.CommLossPaused);
        Assert.StartsWith("통신 복구 — 재개 확인 필요", vm.CommLossLabel);
        Assert.Contains("HOLD", vm.CommLossLabel);
        Assert.True(vm.ResumePauseCommand.CanExecute(null));

        // 재개 완료(AMR이 에러 클리어) → 배너·버튼 소멸
        mon.EmitState(State());
        Assert.False(vm.CommLossPaused);
        Assert.Equal("", vm.CommLossLabel);
        Assert.False(vm.ResumePauseCommand.CanExecute(null));
    }

    [Fact]
    public void ResumeRejected_ReasonAppearsInBanner()
    {
        var (vm, mon) = Build();
        mon.EmitState(State("communicationLostPause: PAUSE", "resumeRejected: 비상정지 해제 필요"));
        Assert.Contains("재개 거부: 비상정지 해제 필요", vm.CommLossLabel);
    }

    [Fact]
    public async Task ResumePause_CallsApi()
    {
        var (vm, mon) = Build();
        mon.EmitState(State("communicationLostPause: PAUSE"));
        lock (RecordingApi.Calls) RecordingApi.Calls.Clear();
        await vm.ResumePauseAsync();
        lock (RecordingApi.Calls) Assert.Contains(nameof(IAcsApiClient.ResumePauseAsync), RecordingApi.Calls);
        Assert.Contains("stopPause", vm.StatusMessage);
    }

    [Fact]
    public void Describe_CommLoss_IsWarn_AndNotFailure_EvenWhileRunning()
    {
        var s = AlarmsViewModel.Describe("ONLINE", State("communicationLostPause: PAUSE"), "RUNNING", "F-01", 0, true, null);
        Assert.Equal("warn", s.Kind);
        Assert.Equal("통신 복구 — 재개 확인 필요", s.Headline);
        Assert.Contains("작업 재개", s.Detail);
    }

    [Fact]
    public void Describe_Priority_EStop_BatteryCritical_CommLoss_BatteryLow()
    {
        var eStop = AlarmsViewModel.Describe("ONLINE",
            State("emergencyStopActive: x", "batteryCritical: 8%", "communicationLostPause: PAUSE"), "RUNNING", null, 0, false, null);
        Assert.Equal("비상정지 상태", eStop.Headline);

        var critical = AlarmsViewModel.Describe("ONLINE",
            State("batteryCritical: 8%", "communicationLostPause: PAUSE"), "RUNNING", null, 0, false, null);
        Assert.StartsWith("배터리 위험", critical.Headline);

        var comm = AlarmsViewModel.Describe("ONLINE",
            State("batteryLow: 18%", "communicationLostPause: PAUSE"), "RUNNING", null, 0, false, null);
        Assert.Equal("통신 복구 — 재개 확인 필요", comm.Headline);
    }

    [Fact]
    public void Describe_ResumeRejected_ShowsReason()
    {
        var s = AlarmsViewModel.Describe("ONLINE",
            State("communicationLostPause: PAUSE", "resumeRejected: 배터리 LOW"), "RUNNING", null, 0, false, null);
        Assert.Contains("배터리 LOW", s.Detail);
    }

    [Theory]
    [InlineData("communicationLostPause: PAUSE", "warn")]
    [InlineData("resumeRejected: 비상정지", "warn")]
    public void ClassifyErrorKind_CommLoss_IsWarn(string err, string expected) =>
        Assert.Equal(expected, AlarmsViewModel.ClassifyErrorKind(err));
}
