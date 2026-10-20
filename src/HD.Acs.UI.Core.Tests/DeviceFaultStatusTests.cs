using System.Reflection;
using HD.Acs.UI.Models;
using HD.Acs.UI.Services;
using HD.Acs.UI.ViewModels;
using Xunit;

namespace HD.Acs.UI.Core.Tests;

/// <summary>
/// 장치 통신 HEAVY 알람 UI 반응 가드 [HD_AMR 장치통신 HEAVY알람 §5/§6].
///  · 로봇 상태 카드: *LinkLost → 적색 배너(장치별 AMR 설명, Latched "복구됨, RESET 대기" 그대로)
///  · 현재 상태 판정 우선순위: 비상정지 > 배터리 CRITICAL > 장치 HEAVY > 통신 단절 PAUSE > 배터리 LOW
/// </summary>
public class DeviceFaultStatusTests
{
    private static RobotStateDto State(params string[] errors) =>
        new("AMR-01", "CT1-L1", 1, 2, 80, "o", null, false, errors.Length, null, "AUTOMATIC", "NONE", errors);

    [Fact]
    public void Banner_ShowsDevices_AndClearsAfterReset()
    {
        var api = DispatchProxy.Create<IAcsApiClient, CommLossStatusTests.RecordingApi>();
        var mon = new FakeMonitoring();
        var vm = new RobotStatusViewModel(api, mon);
        vm.Robots.Add(new RobotDto("AMR-01", "AMR-01", "HD", "AMR-01", "2.0", true));
        vm.SelectedRobot = vm.Robots[0];
        Assert.False(vm.DeviceFault);

        mon.EmitState(State("cobotLinkLost: 코봇 통신 두절 t0=14:03:21 — 상태 소켓(20004) 끊김",
                            "visionLinkLost: 검사 비전카메라 통신 두절 t0=14:03:30 — 복구됨, RESET 대기",
                            "batteryLow: 18%"));
        Assert.True(vm.DeviceFault);
        Assert.StartsWith("장치 통신 두절 — 현장 RESET 필요", vm.DeviceFaultLabel);
        Assert.Contains("상태 소켓(20004) 끊김", vm.DeviceFaultLabel);
        Assert.Contains("복구됨, RESET 대기", vm.DeviceFaultLabel);
        Assert.DoesNotContain("18%", vm.DeviceFaultLabel);

        mon.EmitState(State());
        Assert.False(vm.DeviceFault);
        Assert.Equal("", vm.DeviceFaultLabel);
    }

    [Fact]
    public void Banner_FallsBackToDeviceName_WhenNoDescription()
    {
        var api = DispatchProxy.Create<IAcsApiClient, CommLossStatusTests.RecordingApi>();
        var mon = new FakeMonitoring();
        var vm = new RobotStatusViewModel(api, mon);
        vm.Robots.Add(new RobotDto("AMR-01", "AMR-01", "HD", "AMR-01", "2.0", true));
        vm.SelectedRobot = vm.Robots[0];
        mon.EmitState(State("ioModuleLinkLost"));
        Assert.Contains("I/O 모듈", vm.DeviceFaultLabel);
    }

    [Fact]
    public void Describe_DeviceFault_IsFail_WithResetGuidance()
    {
        var s = AlarmsViewModel.Describe("ONLINE", State("amrLinkLost: AMR 통신 두절 — 상태 갱신 중단"), "RUNNING", "F-01", 0, true, null);
        Assert.Equal("fail", s.Kind);
        Assert.Equal("장치 통신 두절 — 현장 RESET 필요", s.Headline);
        Assert.Contains("상태 갱신 중단", s.Detail);
        Assert.Contains("RESET", s.Detail);
    }

    [Fact]
    public void Describe_Priority_EStop_BatteryCritical_Device_CommLoss()
    {
        Assert.Equal("비상정지 상태", AlarmsViewModel.Describe("ONLINE",
            State("emergencyStopActive: x", "cobotLinkLost: c"), "RUNNING", null, 0, false, null).Headline);
        Assert.StartsWith("배터리 위험", AlarmsViewModel.Describe("ONLINE",
            State("batteryCritical: 8%", "cobotLinkLost: c"), "RUNNING", null, 0, false, null).Headline);
        Assert.Equal("장치 통신 두절 — 현장 RESET 필요", AlarmsViewModel.Describe("ONLINE",
            State("communicationLostPause: PAUSE", "cobotLinkLost: c", "batteryLow: 18%"), "RUNNING", null, 0, false, null).Headline);
    }

    [Theory]
    [InlineData("visionLinkLost: x", "fail")]
    [InlineData("depthCameraLinkLost: x", "fail")]
    [InlineData("laserSensorLinkLost: x", "fail")]
    [InlineData("ioModuleLinkLost: x", "fail")]
    [InlineData("cobotLinkLost: x", "fail")]
    [InlineData("amrLinkLost", "fail")]
    [InlineData("orderRejectedDeviceFault: 장치 HEAVY 알람", "warn")]
    public void ClassifyErrorKind_DeviceTypes(string err, string expected) =>
        Assert.Equal(expected, AlarmsViewModel.ClassifyErrorKind(err));

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
}
