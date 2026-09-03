using HD.Acs.UI.Models;
using HD.Acs.UI.Primitives;
using HD.Acs.UI.Services;
using HD.Acs.UI.ViewModels;
using Xunit;

namespace HD.Acs.UI.Core.Tests;

public class ManualMoveTests
{
    private sealed class FakeMonitoringClient : IMonitoringClient
    {
#pragma warning disable CS0067
        public HubStatus Status => HubStatus.Connected;
        public event EventHandler<HubStatus>? StatusChanged;
        public event EventHandler<RobotStateDto>? RobotStateReceived;
        public event EventHandler<RobotConnectionDto>? RobotConnectionReceived;
        public event EventHandler<MissionProgressDto>? MissionProgressReceived;
        public event EventHandler<RunProgressDto>? RunProgressReceived;
        public event EventHandler<WorkItemProgressDto>? WorkItemProgressReceived;
        public event EventHandler<TaskActionProgressDto>? TaskActionProgressReceived;
        public event EventHandler<AlarmDto>? AlarmRaised;
#pragma warning restore CS0067

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;

        public void EmitRobotState(RobotStateDto s) => RobotStateReceived?.Invoke(this, s);
    }

    private sealed class FakeAcsApiClient : IAcsApiClient
    {
        public string? LastRobotId;
        public int LastLevel;
        public double LastX, LastY;
        public double? LastTheta;
        public bool ThrowOnGoto;

        public Task GotoAsync(string robotId, int level, double xDrawing, double yDrawing,
            double? thetaDrawing = null, CancellationToken ct = default)
        {
            if (ThrowOnGoto) throw new InvalidOperationException("API error");
            LastRobotId = robotId;
            LastLevel = level;
            LastX = xDrawing;
            LastY = yDrawing;
            LastTheta = thetaDrawing;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RobotDto>> GetRobotsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RobotContextDto?> GetRobotContextAsync(string robotId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ScenarioSummaryDto>> GetScenariosAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ScenarioRunDto?> GetRunAsync(Guid runId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RunProgressDto?> GetRunProgressAsync(Guid runId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<WorkItemDto>> GetWorkItemsAsync(Guid runId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<TaskActionDto>> GetTaskActionsAsync(Guid runId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Guid> StartRunAsync(Guid scenarioId, string robotId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task AbortRunAsync(Guid runId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ResumeRunAsync(Guid runId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ResumableRunDto?> GetResumableRunAsync(string robotId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> ReleaseNextMissionAsync(Guid runId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ManualZoneChangeAsync(string robotId, string mapId, string userId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task EmergencyStopAsync(string robotId, string userId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<CalibrationPointDto> CaptureCalibrationPointAsync(string mapId, double drawingX, double drawingY, string unit, string userId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<CalibrationPointDto>> GetCalibrationPointsAsync(string mapId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteCalibrationPointAsync(string mapId, Guid pointId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<CalibrationSolveResultDto> SolveCalibrationAsync(string mapId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<MapCalibrationDto?> GetCalibrationAsync(string mapId, CancellationToken ct = default) => Task.FromResult<MapCalibrationDto?>(null);
        public Task<Guid> CreateScenarioAsync(string name, string tankId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteScenarioAsync(Guid scenarioId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ScenarioAreaDto>> GetScenarioAreasAsync(Guid scenarioId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SetScenarioAreasAsync(Guid scenarioId, IReadOnlyList<Guid> areaIds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Guid> CreateSeamAsync(string tankId, int level, string wallCode, string seamType, double[][] pathDrawing, double[] normalDrawing, string sectionDxfId, string profileId, string userId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<SeamDto>> GetSeamsAsync(string tankId, int? level = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteSeamAsync(Guid seamId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(int Stations, int Tasks)> GenerateFromSeamsAsync(Guid scenarioId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<SlicedStationDto>> GetStationsAsync(Guid scenarioId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> RegisterTankGeometryAsync(string tankId, double lengthL, double wFloor, double thetaLowDeg, double hLow, double hWall, double thetaUpDeg, double hUp, double[] levelZ, double originOx, double originOy, string userId, double? reachZMin = null, double? reachZMax = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<TankGeometryDto?> GetTankGeometryAsync(string tankId, CancellationToken ct = default) => Task.FromResult<TankGeometryDto?>(null);
        public Task<IReadOnlyList<WallDto>> GetWallsAsync(string tankId, int? level = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<WallDto>>(Array.Empty<WallDto>());
        public Task<(Guid AreaId, int Level)> CreateAreaAsync(string tankId, string wallCode, string name, double[][] corners, double? stationX, double? stationY, double? stationTheta, string userId, double? stationStandoffM = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AreaDto>> GetAreasAsync(string tankId, string? wallCode = null, int? level = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AreaDto>>(Array.Empty<AreaDto>());
        public Task DeleteAreaAsync(Guid areaId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> CreateAreaTaskAsync(Guid areaId, double startU, double startV, double endU, double endV, string seamType, string sectionDxfId, string profileId, string userId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AreaTaskDto>> GetAreaTasksAsync(Guid areaId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AreaTaskDto>>(Array.Empty<AreaTaskDto>());
        public Task DeleteAreaTaskAsync(Guid taskId, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private static (TankViewModel vm, FakeAcsApiClient api, FakeMonitoringClient monitoring) CreateVm()
    {
        var api = new FakeAcsApiClient();
        var monitoring = new FakeMonitoringClient();
        var vm = new TankViewModel(api, monitoring)
        {
            SelectedViewMode = "L1",
            ManualMoveMode = true
        };
        // 초기 로봇 상태 주입 (L1에 위치)
        monitoring.EmitRobotState(new RobotStateDto("AMR-01", "CT1-L1", 0.0, 0.0, 95.0, null, null, false, 0, 0.0));
        return (vm, api, monitoring);
    }

    [Fact]
    public async Task RequestMoveAsync_SetsMoveMarkerAndMoveHeading()
    {
        var (vm, api, _) = CreateVm();

        await vm.RequestMoveAsync(5.0, 3.0, Math.PI / 4, zDrawing: 0.0);

        Assert.True(vm.HasMoveMarker);
        Assert.NotNull(vm.MoveMarker);
        Assert.Equal(5.0, vm.MoveMarker.Value.X, 3);
        Assert.Equal(3.0, vm.MoveMarker.Value.Y, 3);
        Assert.Equal(0.25, vm.MoveMarker.Value.Z, 3);
        Assert.Equal(Math.PI / 4, vm.MoveHeading);
        Assert.Equal("AMR-01", api.LastRobotId);
        Assert.Equal(1, api.LastLevel);
    }

    [Fact]
    public async Task CheckMoveArrival_FarFromDestination_RetainsMarker()
    {
        var (vm, _, monitoring) = CreateVm();
        await vm.RequestMoveAsync(5.0, 3.0, Math.PI / 2, zDrawing: 0.0);

        // 로봇이 아직 (2.0, 1.0)에 위치 (거리 약 3.6m > 0.15m)
        monitoring.EmitRobotState(new RobotStateDto("AMR-01", "CT1-L1", 2.0, 1.0, 95.0, null, null, true, 0, 0.0));

        Assert.True(vm.HasMoveMarker);
        Assert.NotNull(vm.MoveMarker);
        Assert.NotNull(vm.MoveHeading);
    }

    [Fact]
    public async Task CheckMoveArrival_ArrivesWithinThreshold_ClearsMarkerAndArrow()
    {
        var (vm, _, monitoring) = CreateVm();
        await vm.RequestMoveAsync(5.0, 3.0, Math.PI / 2, zDrawing: 0.0);
        Assert.True(vm.HasMoveMarker);

        // 로봇이 (5.08, 3.05)에 도달 (거리 = sqrt(0.08^2 + 0.05^2) ≈ 0.094m <= 0.15m)
        monitoring.EmitRobotState(new RobotStateDto("AMR-01", "CT1-L1", 5.08, 3.05, 95.0, null, null, false, 0, Math.PI / 2));

        // 도착 판정에 의해 목적지 마커와 화살표가 null로 자동 소멸
        Assert.False(vm.HasMoveMarker);
        Assert.Null(vm.MoveMarker);
        Assert.Null(vm.MoveHeading);
        Assert.Contains("수동 이동 완료", vm.MoveStatus);
    }

    [Fact]
    public async Task CheckMoveArrival_DifferentFloor_DoesNotClearMarker()
    {
        var (vm, _, monitoring) = CreateVm();
        await vm.RequestMoveAsync(5.0, 3.0, Math.PI / 2, zDrawing: 0.0);

        // 로봇이 다른 층(L2)에서 (5.0, 3.0)에 위치할 때는 L1 목적지가 지워지지 않아야 함
        monitoring.EmitRobotState(new RobotStateDto("AMR-01", "CT1-L2", 5.0, 3.0, 95.0, null, null, false, 0, 0.0));

        Assert.True(vm.HasMoveMarker);
        Assert.NotNull(vm.MoveMarker);
    }

    [Fact]
    public async Task ManualMoveMode_False_ClearsMoveTarget()
    {
        var (vm, _, _) = CreateVm();
        await vm.RequestMoveAsync(5.0, 3.0, Math.PI / 2, zDrawing: 0.0);
        Assert.True(vm.HasMoveMarker);

        // 수동 이동 모드 끔
        vm.ManualMoveMode = false;

        Assert.False(vm.HasMoveMarker);
        Assert.Null(vm.MoveMarker);
        Assert.Null(vm.MoveHeading);
    }

    [Fact]
    public async Task SelectedViewMode_Changed_ClearsMoveTarget()
    {
        var (vm, _, _) = CreateVm();
        await vm.RequestMoveAsync(5.0, 3.0, Math.PI / 2, zDrawing: 0.0);
        Assert.True(vm.HasMoveMarker);

        // 뷰 모드 L2로 변경
        vm.SelectedViewMode = "L2";

        Assert.False(vm.HasMoveMarker);
        Assert.Null(vm.MoveMarker);
        Assert.Null(vm.MoveHeading);
    }

    [Fact]
    public async Task RequestMoveAsync_ApiFails_ClearsMarker()
    {
        var (vm, api, _) = CreateVm();
        api.ThrowOnGoto = true;

        await vm.RequestMoveAsync(5.0, 3.0, Math.PI / 2, zDrawing: 0.0);

        Assert.False(vm.HasMoveMarker);
        Assert.Null(vm.MoveMarker);
        Assert.Null(vm.MoveHeading);
        Assert.Contains("이동 실패", vm.MoveStatus);
    }
}
