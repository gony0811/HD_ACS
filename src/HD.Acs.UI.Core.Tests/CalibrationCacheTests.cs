using System.Reflection;
using HD.Acs.UI.Models;
using HD.Acs.UI.Services;
using HD.Acs.UI.ViewModels;
using Xunit;

namespace HD.Acs.UI.Core.Tests;

/// <summary>
/// 3D·전개도 마커의 T_W_D 캐시 수명 — 서버에서 캘리브레이션을 고치면(재캘리브레이션·교정 도구)
/// UI를 재시작하지 않아도 화면이 따라와야 한다. 종전에는 프로세스 수명 내내 캐시했다.
/// </summary>
public class CalibrationCacheTests
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
        public void Robot(RobotStateDto s) => RobotStateReceived?.Invoke(this, s);
    }

    /// <summary>캘리브레이션 조회만 응답하는 API 대역 — 호출 수를 세고, 서버 측 값 교체를 흉내낸다.</summary>
    public class Api : DispatchProxy
    {
        public static MapCalibrationDto? Current;
        public static int Calls;
        public static TaskCompletionSource? Gate;   // null 아니면 조회가 이 신호까지 대기(동시 조회 검증)

        public static void Set(double tx, double ty, double yaw) =>
            Current = new MapCalibrationDto("CT1-L1", 1, tx, ty, yaw, 0.01, 4, "t", DateTimeOffset.UtcNow);

        protected override object? Invoke(MethodInfo? m, object?[]? args)
        {
            object? value = null;
            if (m!.Name == nameof(IAcsApiClient.GetCalibrationAsync))
            {
                Calls++;
                var cal = Current;
                var gate = Gate;
                value = gate is null
                    ? Task.FromResult(cal)
                    : gate.Task.ContinueWith(_ => cal);
                return value;
            }
            var rt = m.ReturnType;
            if (rt == typeof(Task)) return Task.CompletedTask;
            var t = rt.GetGenericArguments()[0];
            value = t.IsValueType ? Activator.CreateInstance(t) : null;
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(t).Invoke(null, new[] { value });
        }
    }

    private static (TankViewModel vm, FakeMonitoring mon) Create(TimeSpan ttl)
    {
        Api.Calls = 0;
        Api.Gate = null;
        var api = DispatchProxy.Create<IAcsApiClient, Api>();
        var mon = new FakeMonitoring();
        var vm = new TankViewModel(api, mon) { SelectedViewMode = "L1", CalibrationTtl = ttl };
        return (vm, mon);
    }

    /// <summary>drawing = R(−yaw)·(map − t) — 기대값 계산용(VM 구현과 같은 식).</summary>
    private static (double X, double Y) Drawing(double mx, double my, double tx, double ty, double yaw)
    {
        double px = mx - tx, py = my - ty, c = Math.Cos(-yaw), s = Math.Sin(-yaw);
        return (c * px - s * py, s * px + c * py);
    }

    [Fact]
    public async Task ServerSideFix_IsPickedUp_AfterTtl_WithoutRestart()
    {
        // 저장된 yaw가 180° 어긋난 상태 — 교정 도구가 고치는 그 증상.
        Api.Set(10, 10, Math.PI);
        var (vm, mon) = Create(TimeSpan.FromMilliseconds(30));

        mon.Robot(new RobotStateDto("AMR-01", "CT1-L1", 15, 18, 95, null, null, false, 0, 0.0));
        await Task.Delay(50);
        var bad = Drawing(15, 18, 10, 10, Math.PI);
        Assert.Equal(bad.X, vm.RobotDrawingX, 6);
        Assert.Equal(bad.Y, vm.RobotDrawingY, 6);
        Assert.True(vm.RobotMarkerVisible);

        // 운영자가 교정 도구로 yaw를 돌려놓았다(= 서버 값 교체). UI 재시작 없이 TTL 안에 반영돼야 한다.
        Api.Set(10, 10, 0.0);
        mon.Robot(new RobotStateDto("AMR-01", "CT1-L1", 15, 18, 95, null, null, false, 0, 0.0));
        await Task.Delay(80);
        mon.Robot(new RobotStateDto("AMR-01", "CT1-L1", 15, 18, 95, null, null, false, 0, 0.0));
        await Task.Delay(50);

        var good = Drawing(15, 18, 10, 10, 0.0);
        Assert.Equal(good.X, vm.RobotDrawingX, 6);   // 선수(+x)·좌현(+y) 사분면으로 복귀
        Assert.Equal(good.Y, vm.RobotDrawingY, 6);
        Assert.Equal(5.0, vm.RobotDrawingX, 6);
        Assert.Equal(8.0, vm.RobotDrawingY, 6);
        Assert.Equal(0.0, vm.RobotDrawingTheta!.Value, 6);   // 방향도 함께(theta − yaw)
    }

    [Fact]
    public async Task WithinTtl_DoesNotRefetch_AndKeepsMarkerStable()
    {
        Api.Set(10, 10, 0.0);
        var (vm, mon) = Create(TimeSpan.FromSeconds(30));

        for (int i = 0; i < 5; i++)
        {
            mon.Robot(new RobotStateDto("AMR-01", "CT1-L1", 15, 18, 95, null, null, false, 0, 0.0));
            await Task.Delay(10);
        }

        Assert.Equal(1, Api.Calls);   // 2초 주기 state마다 조회하면 안 된다
        Assert.Equal(5.0, vm.RobotDrawingX, 6);
    }

    [Fact]
    public async Task StaleRefresh_DoesNotDuplicateInFlightRequests_NorFlickerMarker()
    {
        Api.Set(10, 10, 0.0);
        var (vm, mon) = Create(TimeSpan.FromMilliseconds(1));
        mon.Robot(new RobotStateDto("AMR-01", "CT1-L1", 15, 18, 95, null, null, false, 0, 0.0));
        await Task.Delay(30);
        Assert.Equal(1, Api.Calls);

        // 조회를 붙잡아 둔 상태에서 state가 여러 번 들어와도 요청은 1건, 마커는 옛 값 유지(튀지 않음)
        Api.Gate = new TaskCompletionSource();
        for (int i = 0; i < 4; i++)
        {
            mon.Robot(new RobotStateDto("AMR-01", "CT1-L1", 15, 18, 95, null, null, false, 0, 0.0));
            await Task.Delay(5);
        }
        Assert.Equal(2, Api.Calls);
        Assert.Equal(5.0, vm.RobotDrawingX, 6);
        Assert.True(vm.RobotMarkerVisible);

        Api.Gate.SetResult();
        await Task.Delay(30);
    }

    [Fact]
    public async Task InvalidateCalibrationCache_RefreshesImmediately()
    {
        Api.Set(10, 10, Math.PI);
        var (vm, mon) = Create(TimeSpan.FromHours(1));   // TTL을 기다릴 수 없는 상황
        mon.Robot(new RobotStateDto("AMR-01", "CT1-L1", 15, 18, 95, null, null, false, 0, 0.0));
        await Task.Delay(30);
        Assert.Equal(-5.0, vm.RobotDrawingX, 6);

        Api.Set(10, 10, 0.0);
        vm.InvalidateCalibrationCache();   // 앱 안에서 재캘리브레이션한 직후(Shell이 호출)
        await Task.Delay(50);

        Assert.Equal(5.0, vm.RobotDrawingX, 6);
        Assert.Equal(8.0, vm.RobotDrawingY, 6);
    }

    [Fact]
    public async Task Uncalibrated_RetriesAfterTtl_AndMarkerStaysHidden()
    {
        Api.Current = null;   // 미보정 층
        var (vm, mon) = Create(TimeSpan.FromMilliseconds(30));
        mon.Robot(new RobotStateDto("AMR-01", "CT1-L1", 15, 18, 95, null, null, false, 0, 0.0));
        await Task.Delay(50);
        Assert.False(vm.RobotMarkerVisible);
        Assert.Equal(15.0, vm.RobotDrawingX, 6);   // 원시 좌표 폴백
        int afterFirst = Api.Calls;

        // 캘리브레이션이 등록되면(최초 캘리브레이션) 재조회로 따라붙는다
        Api.Set(10, 10, 0.0);
        await Task.Delay(50);
        mon.Robot(new RobotStateDto("AMR-01", "CT1-L1", 15, 18, 95, null, null, false, 0, 0.0));
        await Task.Delay(50);

        Assert.True(Api.Calls > afterFirst);
        Assert.True(vm.RobotMarkerVisible);
        Assert.Equal(5.0, vm.RobotDrawingX, 6);
    }
}
