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
/// 캘리브레이션 화면의 기준점 편집 — 행 선택→수정(PUT, id 유지)과 도면 좌표 일괄 회전.
/// 실제 <see cref="AcsApiClient"/>를 가짜 HTTP 서버(최소자승 solve 포함)에 붙여
/// 2026-10-23 현장값(CT1-L4)으로 "선미/우현 → 선수/좌현" 복귀까지 확인한다.
/// </summary>
public class CalibrationEditTests
{
    // 도면 좌표를 180° 돌아간 프레임으로 입력해 저장된 현장 기준점 3점
    private static readonly (double Dx, double Dy, double Mx, double My)[] Field =
    {
        (-2.012, -0.013, 14.125, 10.288),
        (-6.868, 5.602, 9.312, 15.826),
        (-11.258, -4.568, 5.125, 5.545),
    };
    private const double RobotMapX = 4.6536, RobotMapY = 7.4363, RobotTheta = -1.5865;

    private static async Task<(CalibrationViewModel Vm, FakeAcs Server, FakeMonitoring Mon)> BuildAsync()
    {
        var server = new FakeAcs();
        var mon = new FakeMonitoring();
        var http = new HttpClient(server) { BaseAddress = new Uri("http://acs.test") };
        var vm = new CalibrationViewModel(new AcsApiClient(http), mon,
            Options.Create(new AcsOptions { OperatorId = "tester" }));
        vm.SelectedFloor = vm.Floors.First(f => f.MapId == "CT1-L4");
        await vm.LoadCommand.ExecuteAsync(null);
        mon.Robot(new RobotStateDto("AMR-01", "CT1-L4", RobotMapX, RobotMapY, 79, null, null, false, 0, RobotTheta));
        return (vm, server, mon);
    }

    [Fact]
    public async Task SelectingRow_FillsForm_AndUpdateSendsPut_KeepingPointId()
    {
        var (vm, server, _) = await BuildAsync();
        Assert.Equal(3, vm.Points.Count);
        Assert.False(vm.IsEditingPoint);
        Assert.False(vm.UpdatePointCommand.CanExecute(null));   // 선택 전에는 수정 불가

        var target = vm.Points[0];
        vm.SelectPointForEditCommand.Execute(target);           // = 그리드 행 클릭

        Assert.True(vm.IsEditingPoint);
        Assert.Equal(target.DrawingXM, vm.DrawingX, 9);         // 값이 입력칸으로 들어온다
        Assert.Equal(target.DrawingYM, vm.DrawingY, 9);
        Assert.Equal(target.MapX, vm.MapX);
        Assert.Equal(target.MapY, vm.MapY);
        Assert.True(vm.UpdatePointCommand.CanExecute(null));

        vm.DrawingX = 2.012; vm.DrawingY = 0.013;               // 부호 정정
        await vm.UpdatePointCommand.ExecuteAsync(null);

        var put = Assert.Single(server.Writes, w => w.Method == "PUT");
        Assert.Equal($"/api/maps/CT1-L4/calibration/points/{target.Id}", put.Path);
        Assert.Equal(2.012, put.Body.GetProperty("drawingX").GetDouble(), 9);
        Assert.Equal(0.013, put.Body.GetProperty("drawingY").GetDouble(), 9);
        Assert.Equal(target.MapX, put.Body.GetProperty("mapX").GetDouble(), 9);   // 맵 좌표는 그대로
        Assert.Equal("m", put.Body.GetProperty("unit").GetString());

        Assert.Equal(target.Id, vm.Points[0].Id);               // 삭제→재등록이 아님
        Assert.Equal(2.012, vm.Points[0].DrawingXM, 9);
        Assert.Equal(3, vm.Points.Count);
        Assert.Contains(server.Writes, w => w.Path.EndsWith("/calibration/solve"));   // 수정 후 자동 재계산
    }

    [Fact]
    public async Task Rotate180_FixesQuadrantAndHeading_AndIsReversible()
    {
        var (vm, server, _) = await BuildAsync();

        // 교정 전: 사용자가 화면에서 본 그대로
        Assert.Contains("선미/우현", vm.RobotPoseText);
        Assert.Contains("방향 우현", vm.RobotPoseText);

        await vm.RotateDrawingCommand.ExecuteAsync("180");

        var post = Assert.Single(server.Writes, w => w.Path.EndsWith("/points/rotate"));
        Assert.Equal(180, post.Body.GetProperty("deg").GetDouble());
        Assert.All(vm.Points, p => Assert.Contains(Field, f =>
            Math.Abs(f.Mx - p.MapX) < 1e-9 && Math.Abs(-f.Dx - p.DrawingXM) < 1e-9));   // 도면만 반전
        Assert.Contains("선수/좌현", vm.RobotPoseText);          // 실제 위치로 복귀
        Assert.Contains("방향 좌현", vm.RobotPoseText);
        Assert.Contains("-180", vm.StatusMessage);               // 되돌리는 법 안내

        await vm.RotateDrawingCommand.ExecuteAsync("-180");      // 되돌리기
        Assert.Contains("선미/우현", vm.RobotPoseText);
    }

    [Fact]
    public async Task CancelEdit_ReturnsToAddMode_AndDeleteClearsSelection()
    {
        var (vm, _, _) = await BuildAsync();
        vm.SelectPointForEditCommand.Execute(vm.Points[1]);
        Assert.True(vm.IsEditingPoint);

        vm.CancelPointEditCommand.Execute(null);
        Assert.False(vm.IsEditingPoint);
        Assert.False(vm.UpdatePointCommand.CanExecute(null));

        var doomed = vm.Points[1];
        vm.SelectPointForEditCommand.Execute(doomed);
        await vm.DeletePointCommand.ExecuteAsync(doomed);
        Assert.False(vm.IsEditingPoint);                         // 지운 점이 편집 대상으로 남지 않는다
        Assert.Equal(2, vm.Points.Count);
    }

    [Fact]
    public void PoseDescription_UsesTankCentreAndHeadingWords()
    {
        Assert.Equal("선수/좌현 (11.64, 2.68) · 방향 좌현(88.0°)",
            CalibrationPreview.Describe(11.64, 2.68, 88.0));
        Assert.Equal("선미/우현 (-11.64, -2.68) · 방향 선미(-179.0°)",
            CalibrationPreview.Describe(-11.64, -2.68, -179.0));
        Assert.Equal("중앙/중앙 (0.10, -0.05) · 방향 미보고", CalibrationPreview.Describe(0.1, -0.05, null));
        Assert.Contains("방향 선수", CalibrationPreview.Describe(5, 5, -10));
        Assert.Contains("방향 우현", CalibrationPreview.Describe(5, 5, -90));

        var (x, y, head) = CalibrationPreview.ToDrawing(RobotMapX, RobotMapY, RobotTheta,
            16.2387, 10.3445, -3.12194);
        Assert.Equal(11.64, x, 2);
        Assert.Equal(2.68, y, 2);
        Assert.Equal(88.0, head!.Value, 1);
    }

    // ── 가짜 서버: 기준점을 실제로 보관하고 같은 최소자승으로 solve 한다 ──
    private sealed class FakeAcs : HttpMessageHandler
    {
        public List<(string Method, string Path, JsonElement Body)> Writes { get; } = new();
        private readonly List<CalibrationPointDto> _points = Field
            .Select(f => new CalibrationPointDto(Guid.NewGuid(), "CT1-L4", 1, f.Dx, f.Dy, f.Mx, f.My,
                DateTimeOffset.UtcNow, "field")).ToList();
        private (double Tx, double Ty, double Yaw, double Rms) _cal = Solve(Field.Select(f => (f.Dx, f.Dy, f.Mx, f.My)));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Get)
            {
                if (path == "/api/internal/tanks/CT1/geometry")
                    return Json(new { tankId = "CT1", lengthL = 31.785, wFloor = 10.15, thetaLow = 0.785, hLow = 3.98,
                        hWall = 6.91, thetaUp = 0.785, hUp = 2.9, levelZ = new[] { 0.0, 3.63, 6.63, 10.53 },
                        originOx = 0.0, originOy = 0.0, beam = 17.285, wCeil = 6.0, height = 13.785 });
                if (path == "/api/maps/CT1-L4/calibration/points") return Json(_points);
                if (path == "/api/maps/CT1-L4/calibration")
                    return Json(new { mapId = "CT1-L4", mapVersion = 1, tx = _cal.Tx, ty = _cal.Ty, yawRad = _cal.Yaw,
                        rmsM = _cal.Rms, pointCount = _points.Count, registeredBy = "t", registeredAt = DateTimeOffset.UtcNow });
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            JsonElement body = default;
            if (req.Content is not null)
                body = JsonDocument.Parse(await req.Content.ReadAsStringAsync(ct)).RootElement.Clone();
            Writes.Add((req.Method.Method, path, body));

            if (req.Method == HttpMethod.Delete && path.Contains("/calibration/points/"))
            {
                _points.RemoveAll(p => p.Id == Guid.Parse(path[(path.LastIndexOf('/') + 1)..]));
                return Json(new { });
            }
            if (req.Method == HttpMethod.Put && path.Contains("/calibration/points/"))
            {
                var id = Guid.Parse(path[(path.LastIndexOf('/') + 1)..]);
                int i = _points.FindIndex(p => p.Id == id);
                _points[i] = _points[i] with
                {
                    DrawingXM = body.GetProperty("drawingX").GetDouble(),
                    DrawingYM = body.GetProperty("drawingY").GetDouble(),
                    MapX = body.GetProperty("mapX").GetDouble(),
                    MapY = body.GetProperty("mapY").GetDouble(),
                };
                return Json(_points[i]);
            }
            if (path.EndsWith("/points/rotate"))
            {
                double deg = body.GetProperty("deg").GetDouble();
                double r = deg * Math.PI / 180, c = Math.Round(Math.Cos(r)), s = Math.Round(Math.Sin(r));
                for (int i = 0; i < _points.Count; i++)
                    _points[i] = _points[i] with
                    {
                        DrawingXM = c * _points[i].DrawingXM - s * _points[i].DrawingYM,
                        DrawingYM = s * _points[i].DrawingXM + c * _points[i].DrawingYM,
                    };
                return Json(new { mapId = "CT1-L4", deg, rotated = _points.Count });
            }
            if (path.EndsWith("/calibration/solve"))
            {
                _cal = Solve(_points.Select(p => (p.DrawingXM, p.DrawingYM, p.MapX, p.MapY)));
                return Json(new { tx = _cal.Tx, ty = _cal.Ty, yawRad = _cal.Yaw, rmsM = _cal.Rms,
                    maxResidualM = _cal.Rms, pointCount = _points.Count, warning = (string?)null });
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        /// <summary>DrawingTransform.Solve 와 같은 2D 강체 최소자승(UI.Core는 도메인 Core를 참조하지 않는다).</summary>
        private static (double, double, double, double) Solve(IEnumerable<(double Dx, double Dy, double Mx, double My)> src)
        {
            var p = src.ToList();
            double cdx = p.Average(v => v.Dx), cdy = p.Average(v => v.Dy);
            double cmx = p.Average(v => v.Mx), cmy = p.Average(v => v.My);
            double sxy = 0, sxx = 0;
            foreach (var v in p)
            {
                double a = v.Dx - cdx, b = v.Dy - cdy, c = v.Mx - cmx, d = v.My - cmy;
                sxy += a * d - b * c; sxx += a * c + b * d;
            }
            double yaw = Math.Atan2(sxy, sxx), cos = Math.Cos(yaw), sin = Math.Sin(yaw);
            double tx = cmx - (cos * cdx - sin * cdy), ty = cmy - (sin * cdx + cos * cdy);
            double rms = Math.Sqrt(p.Average(v =>
                Math.Pow(cos * v.Dx - sin * v.Dy + tx - v.Mx, 2) + Math.Pow(sin * v.Dx + cos * v.Dy + ty - v.My, 2)));
            return (tx, ty, yaw, rms);
        }

        private static HttpResponseMessage Json(object o, HttpStatusCode status = HttpStatusCode.OK) => new(status)
        { Content = new StringContent(JsonSerializer.Serialize(o, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json") };
    }

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
}
