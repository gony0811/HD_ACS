using System.Reflection;
using HD.Acs.UI.Models;
using HD.Acs.UI.Services;
using HD.Acs.UI.ViewModels;
using Xunit;

namespace HD.Acs.UI.Core.Tests;

/// <summary>로봇 상태 카드 "계획 정차점까지 거리" — 비교 대상 선택·오차 분해·허용 오차 판정.</summary>
public class StationDeviationTests
{
    // 2026-10-02 실측: CT1-L1 T_W_D, TEST-01 영역 F-AMR01-01 (우현 2 m 이동 후) 계획 정차점
    private const double Tx = 9.350131726237741, Ty = 12.298407115974946, Yaw = -0.002735882450856207;
    private static readonly Guid AreaF = Guid.NewGuid();
    private static readonly Guid AreaF2 = Guid.NewGuid();

    private static PlannedStationsDto Plan(params PlannedStationDto[] s) =>
        new(Guid.NewGuid(), "CT1", 0.08, 0.07, s.ToList());

    private static PlannedStationDto F(Guid id, double x, double y, string map = "CT1-L1") =>
        new(id, $"F-{x}", "F", 1, map, x, y, 0.0, 1.8, false, new[] { 0.0, -1.0 }, new[] { -1.0, 0.0 });

    private static (double X, double Y) MapToDrawing(double mx, double my)
    {
        double px = mx - Tx, py = my - Ty, c = Math.Cos(Yaw), s = Math.Sin(Yaw);
        return (c * px + s * py, -s * px + c * py);
    }

    [Fact]
    public void RealMeasurement_MatchesScriptCheck()
    {
        var (x, y) = MapToDrawing(7.53125, 11.861892700195312);
        var yaw = TankViewModel.MapThetaToDrawing(-0.016600292176008224, Yaw);
        var r = StationDeviation.Evaluate(x, y, yaw, "CT1-L1", Plan(F(AreaF, 1.35, -2.0)), null)!;

        Assert.Equal(3.530, r.DistanceM, 3);
        Assert.Equal(-1.559, r.AlongWallM!.Value, 3);     // u=−y(우현) 반대 = 좌현 쪽
        Assert.Equal(4.968, r.WallDistanceM!.Value, 3);   // 계획 1.8 + 3.168
        Assert.Equal(-0.8, r.HeadingErrorRad!.Value * 180 / Math.PI, 1);
        Assert.False(r.WithinTolerance);
        Assert.False(r.IsCurrentTask);
        Assert.Equal("벽 따라 좌현 쪽 1.56 m · 벽까지 4.97 m (계획 1.80) · 방향 차 -0.8°", StationDeviation.Describe(r));
    }

    [Fact]
    public void Tolerance_UsesServerLimits_ForPositionAndHeading()
    {
        var plan = Plan(F(AreaF, 1.35, -2.0));
        Assert.True(StationDeviation.Evaluate(1.40, -2.03, 0.05, "CT1-L1", plan, null)!.WithinTolerance);
        Assert.False(StationDeviation.Evaluate(1.40, -2.03, 0.10, "CT1-L1", plan, null)!.WithinTolerance);   // 방향 0.07 rad 초과
        Assert.False(StationDeviation.Evaluate(1.45, -2.0, 0.0, "CT1-L1", plan, null)!.WithinTolerance);    // 위치 0.08 m 초과
        Assert.True(StationDeviation.Evaluate(1.40, -2.0, null, "CT1-L1", plan, null)!.WithinTolerance);    // heading 미보고 → 위치만
    }

    [Fact]
    public void Target_PrefersCurrentTask_ElseNearestOnSameFloor()
    {
        var plan = Plan(F(AreaF, 1.35, -2.0), F(AreaF2, 1.35, 2.0), F(Guid.NewGuid(), 1.35, 0.1, "CT1-L2"));
        Assert.Equal(AreaF2, StationDeviation.Evaluate(1.3, 1.5, null, "CT1-L1", plan, null)!.Station.AreaId);
        var cur = StationDeviation.Evaluate(1.3, 1.5, null, "CT1-L1", plan, AreaF)!;
        Assert.Equal(AreaF, cur.Station.AreaId);
        Assert.True(cur.IsCurrentTask);
        Assert.Null(StationDeviation.Evaluate(0, 0, null, "CT1-L3", plan, null));   // 이 층 정차점 없음
        Assert.Null(StationDeviation.Evaluate(0, 0, null, "CT1-L1", null, null));
    }

    // ── 로봇 상태 카드 연동 ──

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
#pragma warning restore CS0067
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public void Robot(RobotStateDto s) => RobotStateReceived?.Invoke(this, s);
    }

    /// <summary>필요한 조회만 응답하는 API 대역 — 나머지는 기본값.</summary>
    public class Api : DispatchProxy
    {
        public static PlannedStationsDto? PlanToReturn;
        public static int PlanCalls;

        protected override object? Invoke(MethodInfo? m, object?[]? args)
        {
            object? value = m!.Name switch
            {
                nameof(IAcsApiClient.GetCalibrationAsync) => new MapCalibrationDto("CT1-L1", 1, Tx, Ty, Yaw, 0.1, 4, "t", DateTimeOffset.Now),
                nameof(IAcsApiClient.GetPlannedStationsAsync) => (PlanCalls++, PlanToReturn).PlanToReturn,
                _ => null,
            };
            var rt = m.ReturnType;
            if (rt == typeof(Task)) return Task.CompletedTask;
            var t = rt.GetGenericArguments()[0];
            value ??= t.IsValueType ? Activator.CreateInstance(t) : null;
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(t).Invoke(null, new[] { value });
        }
    }

    [Fact]
    public async Task RobotCard_ShowsDistanceToPlannedStation()
    {
        Api.PlanToReturn = Plan(F(AreaF, 1.35, -2.0));
        var api = DispatchProxy.Create<IAcsApiClient, Api>();
        var mon = new FakeMonitoring();
        var mission = new MissionViewModel(api, mon);
        var vm = new RobotStatusViewModel(api, mon, mission);
        vm.SelectedRobot = new RobotDto("AMR-01", "AMR-01", "HHI", "AMR-01", "2.0", true);
        await Task.Delay(50);
        Assert.Equal("시나리오 미선택", vm.StationTarget);

        mission.SelectedScenario = new ScenarioSummaryDto(Guid.NewGuid(), "TEST-01", 1, "CT1", "DRAFT", 1);
        mon.Robot(new RobotStateDto("AMR-01", "CT1-L1", 7.53125, 11.861892700195312, 95, null, null, false, 0,
            -0.016600292176008224));
        await Task.Delay(50);

        Assert.StartsWith("F-1.35 (가장 가까운 계획) · 면 F", vm.StationTarget);
        Assert.Equal("3.53 m · 허용 오차 밖", vm.StationDistance);
        Assert.Contains("좌현 쪽 1.56 m", vm.StationDetail);
        Assert.Equal("warn", vm.StationKind);

        // 정차점에 거의 도착 → 허용 오차 이내 (맵 = 계획 정차점 + 3 cm)
        double c = Math.Cos(Yaw), s = Math.Sin(Yaw);
        double mx = c * 1.38 - s * -2.0 + Tx, my = s * 1.38 + c * -2.0 + Ty;
        mon.Robot(new RobotStateDto("AMR-01", "CT1-L1", mx, my, 95, null, null, false, 0, Yaw));
        await Task.Delay(50);
        Assert.Equal("0.03 m · 허용 오차 이내", vm.StationDistance);
        Assert.Equal("done", vm.StationKind);

        // 다른 층 → 비교 대상 없음
        mon.Robot(new RobotStateDto("AMR-01", "CT1-L2", mx, my, 95, null, null, false, 0, Yaw));
        await Task.Delay(50);
        Assert.Equal("이 층(CT1-L2)에 계획 정차점 없음", vm.StationTarget);
    }
}
