using System.Reflection;
using HD.Acs.UI.Models;
using HD.Acs.UI.Services;
using HD.Acs.UI.ViewModels;
using Xunit;

namespace HD.Acs.UI.Core.Tests;

/// <summary>
/// 배터리 UI 반응 가드 — RobotStatusViewModel.BatteryLevel 판정과 AlarmsViewModel.Describe 분기.
/// 사양서 §1 "ACS는 상태머신을 복제하지 않는다"에 따라 errors[]가 정본이고 수치만 낮으면 "추정" 톤.
/// </summary>
public class BatteryStatusTests
{
    // ── FakeMonitoring: BatterySwapDispatched event 포함(인터페이스 contract) ──
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

    public class NullApi : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? m, object?[]? args)
        {
            var rt = m!.ReturnType;
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

    private static RobotStateDto State(double? pct, params string[] errors) =>
        new("AMR-01", "CT1-L1", 1, 2, pct, "o", null, false, errors.Length, null, "AUTOMATIC", "NONE", errors);

    private static (RobotStatusViewModel vm, FakeMonitoring mon) Build()
    {
        var api = DispatchProxy.Create<IAcsApiClient, NullApi>();
        var mon = new FakeMonitoring();
        var vm = new RobotStatusViewModel(api, mon);
        vm.Robots.Add(new RobotDto("AMR-01", "AMR-01", "HD", "AMR-01", "2.0", true));
        vm.SelectedRobot = vm.Robots[0];
        return (vm, mon);
    }

    // ── BatteryLevel 판정 ──

    [Fact]
    public void CriticalError_SetsCriticalLevel()
    {
        var (vm, mon) = Build();
        mon.EmitState(State(pct: 8, "batteryCritical: SoC 8%"));
        Assert.Equal(BatteryLevel.Critical, vm.BatteryLevel);
        Assert.Contains("위험", vm.BatteryStatusLabel);
    }

    [Fact]
    public void LowError_SetsLowLevel()
    {
        var (vm, mon) = Build();
        mon.EmitState(State(pct: 18, "batteryLow: SoC 18%"));
        Assert.Equal(BatteryLevel.Low, vm.BatteryLevel);
        Assert.Contains("저전력", vm.BatteryStatusLabel);
    }

    [Fact]
    public void NumericOnly_LowRange_IsLow_WithEstimatedLabel()
    {
        var (vm, mon) = Build();
        mon.EmitState(State(pct: 15));   // errors 없이 수치만 낮음
        Assert.Equal(BatteryLevel.Low, vm.BatteryLevel);
        Assert.Contains("추정", vm.BatteryStatusLabel);
    }

    [Fact]
    public void NumericOnly_CriticalRange_IsCritical_WithEstimatedLabel()
    {
        var (vm, mon) = Build();
        mon.EmitState(State(pct: 7));
        Assert.Equal(BatteryLevel.Critical, vm.BatteryLevel);
        Assert.Contains("추정", vm.BatteryStatusLabel);
    }

    [Fact]
    public void Normal_WhenAboveThresholdAndNoError()
    {
        var (vm, mon) = Build();
        mon.EmitState(State(pct: 85));
        Assert.Equal(BatteryLevel.Normal, vm.BatteryLevel);
        Assert.Contains("정상", vm.BatteryStatusLabel);
    }

    // ── AlarmsViewModel.Describe 우선순위 — 배터리 분기 ──

    [Fact]
    public void Describe_BatteryCritical_Precedes_RunState_AndIsFailKind()
    {
        var s = State(pct: 8, "batteryCritical: SoC 8%");
        var r = AlarmsViewModel.Describe("ONLINE", s, "RUNNING", "F-01", 0, true, null);
        Assert.Equal("fail", r.Kind);
        Assert.Contains("배터리 위험", r.Headline);
    }

    [Fact]
    public void Describe_BatteryLow_IsWarn_AndSuggestsSwap()
    {
        var s = State(pct: 18, "batteryLow: SoC 18%");
        var r = AlarmsViewModel.Describe("ONLINE", s, "RUNNING", "F-01", 0, false, null);
        Assert.Equal("warn", r.Kind);
        Assert.Contains("배터리", r.Headline);
        Assert.Contains("교체", r.Detail);
    }

    [Fact]
    public void Describe_EStop_StillPrecedesBattery()
    {
        // 사양서 §9.5: CRITICAL이어도 그 자리 안전정지 — 비상정지는 더 위 우선순위 유지(사용자가 즉시 다룰 상황).
        var s = new RobotStateDto("AMR-01", "CT1-L1", 1, 2, 5, "o", null, false, 1, null, "AUTOMATIC", "MANUAL",
            new[] { "batteryCritical: SoC 5%" });
        var r = AlarmsViewModel.Describe("ONLINE", s, "RUNNING", "F-01", 0, false, null);
        Assert.Equal("fail", r.Kind);
        Assert.Contains("비상정지", r.Headline);
    }

    [Fact]
    public void Describe_OrderRejectedBatteryLow_IsNotTreatedAsFailure()
    {
        // AlarmsViewModel.Describe는 상태 카드만 — orderRejectedBatteryLow가 errors에 섞여 있어도 FATAL이 아니다.
        // (상태 카드는 Describe가 가장 적합한 상위 상태만 노출 — '로봇 오류 보고 중' warn으로 떨어짐은 허용)
        var s = State(pct: 15, "orderRejectedBatteryLow: 저전력으로 거부", "batteryLow: 18%");
        var r = AlarmsViewModel.Describe("ONLINE", s, "RUNNING", "F-01", 0, false, null);
        Assert.NotEqual("fail", r.Kind);
    }

    // ── ClassifyErrorKind — 이벤트 로그 색 분류 ──

    [Theory]
    [InlineData("batteryCritical: SoC 8%", "fail")]
    [InlineData("batteryLow: SoC 18%", "warn")]
    [InlineData("orderRejectedBatteryLow: 저전력으로 거부", "warn")]
    [InlineData("drivingFailed: 경로 생성 실패", "warn")]
    public void ClassifyErrorKind_MapsPerSpec(string err, string expected)
    {
        Assert.Equal(expected, AlarmsViewModel.ClassifyErrorKind(err));
    }

    // ── HasErrorType — 접두 매칭 ──

    [Fact]
    public void HasErrorType_MatchesWithOrWithoutColon()
    {
        var s = State(pct: 50, "batteryLow", "equipmentError: cam");
        Assert.True(AlarmsViewModel.HasErrorType(s, "batteryLow"));
        Assert.True(AlarmsViewModel.HasErrorType(s, "equipmentError"));
        Assert.False(AlarmsViewModel.HasErrorType(s, "batteryCritical"));
    }
}
