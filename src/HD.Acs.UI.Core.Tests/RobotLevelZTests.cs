using System.Reflection;
using HD.Acs.UI.Models;
using HD.Acs.UI.Services;
using HD.Acs.UI.ViewModels;
using Xunit;

namespace HD.Acs.UI.Core.Tests;

/// <summary>
/// 3D 로봇 마커 높이 = 로봇 보고 층의 바닥 level_z[n−1] (도면 전역 z). 층 바닥=0 환산은 AMR로 보내는
/// seam 좌표에만 적용하며 ACS 화면에는 쓰지 않는다 [VDA §10 N17]. 모든 층(L1~L4)에 같은 규칙.
/// </summary>
public class RobotLevelZTests
{
    private static readonly double[] LevelZ = { 0.0, 3.63, 6.63, 10.53 };

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
        public void Robot(RobotStateDto s) => RobotStateReceived?.Invoke(this, s);
    }

    /// <summary>지오메트리·캘리브레이션(모든 층 항등)만 응답하는 API 대역.</summary>
    public class Api : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? m, object?[]? args)
        {
            object? value = m!.Name switch
            {
                nameof(IAcsApiClient.GetTankGeometryAsync) => new TankGeometryDto(
                    "CT1", 31.785, 10.15, 45, 3.98, 6.91, 45, 2.9, LevelZ, 0, 0,
                    new TankDerivedDto(3.98, 18.1, 2.9, 12.3, 13.785)),
                nameof(IAcsApiClient.GetCalibrationAsync) => new MapCalibrationDto(
                    (string)args![0]!, 1, 0, 0, 0, 0.01, 4, "t", DateTimeOffset.UtcNow),
                _ => null,
            };
            var rt = m.ReturnType;
            if (rt == typeof(Task)) return Task.CompletedTask;
            var t = rt.GetGenericArguments()[0];
            value ??= t.IsValueType ? Activator.CreateInstance(t)
                : t.IsGenericType && t.GetGenericArguments() is [var e] && t.IsAssignableFrom(e.MakeArrayType())
                    ? Array.CreateInstance(e, 0)   // 목록 조회 = 빈 목록(null이면 LoadAsync가 예외로 지오메트리를 비운다)
                    : null;
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(t).Invoke(null, new[] { value });
        }
    }

    private static (TankViewModel vm, FakeMonitoring mon) Create()
    {
        var mon = new FakeMonitoring();
        var vm = new TankViewModel(DispatchProxy.Create<IAcsApiClient, Api>(), mon) { TankId = "CT1" };
        return (vm, mon);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task RobotMarker_SitsOnItsLevelFloor_InDrawingGlobalZ(int level)
    {
        var (vm, mon) = Create();
        await vm.LoadAsync();

        mon.Robot(new RobotStateDto("AMR-01", $"CT1-L{level}", 5, 1, 95, null, null, false, 0, 0.0));

        Assert.Equal(LevelZ[level - 1], vm.RobotDrawingZ, 6);
        Assert.True(vm.RobotMarkerVisible);
    }

    /// <summary>state가 지오메트리보다 먼저 와도 지오메트리 로드 직후 그 층 바닥으로 올라간다(다음 state 대기 없음).</summary>
    [Fact]
    public async Task RobotMarker_IsLiftedToLevelFloor_WhenGeometryArrivesAfterState()
    {
        var (vm, mon) = Create();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        mon.Robot(new RobotStateDto("AMR-01", "CT1-L4", 5, 1, 95, null, null, false, 0, 0.0));
        Assert.Equal(0.0, vm.RobotDrawingZ);   // 지오메트리 전 — 층 높이 미상

        await vm.LoadAsync();

        Assert.Equal(LevelZ[3], vm.RobotDrawingZ, 6);
        Assert.Contains(nameof(TankViewModel.RobotDrawingZ), changed);   // 3D 뷰가 다시 그리도록 알림
    }
}
