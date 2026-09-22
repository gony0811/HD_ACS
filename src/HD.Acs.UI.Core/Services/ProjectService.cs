using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using HD.Acs.UI.Models;
using Microsoft.Extensions.Options;

namespace HD.Acs.UI.Services;

/// <summary>
/// 프로젝트 파일 입출력 구현. 컨테이너 = 매직 헤더("HDACSPRJ" + 버전 바이트) + GZip(UTF-8 JSON).
/// 매직/버전이 맞지 않으면 예외 — 이 프로그램에서만 열 수 있는 전용 포맷.
/// </summary>
public sealed class ProjectService : IProjectService
{
    public const string Extension = ".hdacs";
    private const int FormatVersion = 3;   // v3: 캘리브레이션 + 시나리오/영역 연결 포함
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("HDACSPRJ"); // 8 bytes
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private readonly IAcsApiClient _api;
    private readonly string _operatorId;

    public ProjectService(IAcsApiClient api, IOptions<AcsOptions> options)
    {
        _api = api;
        _operatorId = options.Value.OperatorId;
    }

    public string? CurrentPath { get; private set; }

    public async Task SaveAsync(string tankId, string path, CancellationToken ct = default)
    {
        var geom = await _api.GetTankGeometryAsync(tankId, ct)
            ?? throw new InvalidOperationException($"저장할 선창이 없습니다: {tankId} (먼저 선창을 등록하세요).");

        var areas = await _api.GetAreasAsync(tankId, ct: ct);
        var areaDocs = new List<AreaDoc>(areas.Count);
        foreach (var a in areas)
        {
            var tasks = await _api.GetAreaTasksAsync(a.AreaId, ct);
            var taskDocs = tasks.Select(t => new TaskDoc(
                t.Seq, t.Name, t.SeamType, t.StartU, t.StartV, t.EndU, t.EndV,
                t.SectionDxfId, t.ProfileId, t.TaskId)).ToArray();
            areaDocs.Add(new AreaDoc(a.WallCode, a.Level, a.Name,
                a.UMin, a.VMin, a.UMax, a.VMax, a.StationX, a.StationY, a.StationTheta, taskDocs, a.Corners,
                a.StationStandoffM, a.AreaId));
        }

        var calibrations = new List<CalibrationDoc>();
        for (var level = 1; level <= (geom.LevelZ?.Length ?? 0); level++)
        {
            var mapId = $"{tankId}-L{level}";
            var cal = await _api.GetCalibrationAsync(mapId, ct);
            if (cal is null) continue;
            var points = await _api.GetCalibrationPointsAsync(mapId, ct);
            calibrations.Add(new CalibrationDoc(mapId,
                points.Select(p => new CalibrationPointDoc(p.DrawingXM, p.DrawingYM, p.MapX, p.MapY)).ToArray(),
                cal.Tx, cal.Ty, cal.YawRad, cal.RmsM));
        }

        var scenarios = new List<ScenarioDoc>();
        foreach (var scenario in (await _api.GetScenariosAsync(ct)).Where(s => s.TankId == tankId))
        {
            var links = await _api.GetScenarioAreasAsync(scenario.ScenarioId, ct);
            scenarios.Add(new ScenarioDoc(scenario.Name, links.OrderBy(x => x.SortOrder).Select(x => x.AreaId).ToArray()));
        }

        var doc = new ProjectDoc(FormatVersion, tankId,
            new GeometryDoc(geom.LengthL, geom.WFloor, geom.ThetaLowDeg, geom.HLow,
                geom.HWall, geom.ThetaUpDeg, geom.HUp, geom.LevelZ ?? Array.Empty<double>(),
                geom.OriginOx, geom.OriginOy, geom.ReachZMin, geom.ReachZMax),
            areaDocs.ToArray(), calibrations.ToArray(), scenarios.ToArray());

        await using (var fs = File.Create(path))
        {
            await fs.WriteAsync(Magic, ct);
            fs.WriteByte(FormatVersion);
            await using var gz = new GZipStream(fs, CompressionLevel.Optimal);
            await JsonSerializer.SerializeAsync(gz, doc, Json, ct);
        }
        CurrentPath = path;
    }

    public async Task<ProjectDoc> OpenAsync(string path, CancellationToken ct = default)
    {
        ProjectDoc doc;
        await using (var fs = File.OpenRead(path))
        {
            var header = new byte[Magic.Length];
            int read = await fs.ReadAsync(header, ct);
            int ver = fs.ReadByte();
            if (read != Magic.Length || !header.AsSpan().SequenceEqual(Magic) || ver < 0)
                throw new InvalidDataException("이 프로그램의 프로젝트 파일이 아닙니다.");
            if (ver > FormatVersion)
                throw new InvalidDataException($"지원하지 않는 프로젝트 파일 버전입니다: v{ver} (이 프로그램은 v{FormatVersion}까지 지원).");

            await using var gz = new GZipStream(fs, CompressionMode.Decompress);
            doc = await JsonSerializer.DeserializeAsync<ProjectDoc>(gz, Json, ct)
                ?? throw new InvalidDataException("프로젝트 파일을 읽을 수 없습니다 (내용 없음).");
        }

        ValidateIdentities(doc);

        // DB 재적재: 선창 등록(면 재생성 — 기존 영역은 wall CASCADE로 정리) → 영역 → 작업
        var g = doc.Geometry;
        await _api.RegisterTankGeometryAsync(doc.TankId, g.LengthL, g.WFloor, g.ThetaLowDeg, g.HLow,
            g.HWall, g.ThetaUpDeg, g.HUp, g.LevelZ, g.OriginOx, g.OriginOy, _operatorId,
            g.ReachZMin, g.ReachZMax, ct);

        var restoredAreaIds = new Dictionary<Guid, Guid>();
        foreach (var a in doc.Areas)
        {
            // v3.1: level은 서버가 영역 z범위로 유도(저장된 a.Level은 무시). AreaId만 사용.
            // corners 없으면(구파일) bbox 사각형으로 폴백.
            var corners = a.Corners ?? new[]
            {
                new[] { a.UMin, a.VMin }, new[] { a.UMax, a.VMin }, new[] { a.UMax, a.VMax }, new[] { a.UMin, a.VMax },
            };
            var (areaId, _) = await _api.CreateAreaAsync(doc.TankId, a.WallCode, a.Name,
                corners, a.StationX, a.StationY, a.StationTheta, _operatorId,
                stationStandoffM: a.StationStandoffM, areaId: a.SourceId, ct: ct);
            if (a.SourceId is Guid sourceId) restoredAreaIds[sourceId] = areaId;
            foreach (var t in a.Tasks)
                await _api.CreateAreaTaskAsync(areaId, t.StartU, t.StartV, t.EndU, t.EndV,
                    t.SeamType, t.SectionDxfId, t.ProfileId, _operatorId,
                    seq: t.Seq, name: t.Name, taskId: t.SourceId, ct: ct);
        }

        // 대응점 원본을 복원한 뒤 다시 solve하여 현재 map version에 유효한 T_W_D를 만든다.
        foreach (var cal in doc.Calibrations ?? Array.Empty<CalibrationDoc>())
        {
            foreach (var old in await _api.GetCalibrationPointsAsync(cal.MapId, ct))
                await _api.DeleteCalibrationPointAsync(cal.MapId, old.Id, ct);
            foreach (var p in cal.Points)
                await _api.CaptureCalibrationPointAsync(cal.MapId, p.DrawingXM, p.DrawingYM, "m", _operatorId,
                    ct, p.MapX, p.MapY);
            if (cal.Points.Length >= 2)
                await _api.SolveCalibrationAsync(cal.MapId, ct);

            // 가져오기 API가 다른 서버를 향하거나 구버전 서버에서 일부 요청이 누락돼도
            // 성공으로 가장하지 않는다. 실제 서버 상태를 다시 읽어 파일 내용과 대조한다.
            var restoredPoints = await _api.GetCalibrationPointsAsync(cal.MapId, ct);
            var restoredCalibration = await _api.GetCalibrationAsync(cal.MapId, ct);
            if (restoredPoints.Count != cal.Points.Length || (cal.Points.Length >= 2 && restoredCalibration is null))
                throw new InvalidDataException(
                    $"캘리브레이션 복원 검증 실패: {cal.MapId} — 파일 {cal.Points.Length}점, " +
                    $"서버 {restoredPoints.Count}점, T_W_D {(restoredCalibration is null ? "없음" : "있음")}. " +
                    "다른 PC의 HD.Acs.App 주소와 실행 버전을 확인하세요.");
        }

        // 같은 선창/이름의 시나리오는 재사용하여 중복 생성을 피하고, 없으면 새로 만든다.
        var existingScenarios = (await _api.GetScenariosAsync(ct)).Where(s => s.TankId == doc.TankId).ToList();
        foreach (var scenario in doc.Scenarios ?? Array.Empty<ScenarioDoc>())
        {
            var existing = existingScenarios.FirstOrDefault(s => s.Name == scenario.Name);
            var scenarioId = existing?.ScenarioId ?? await _api.CreateScenarioAsync(scenario.Name, doc.TankId, ct);
            var areaIds = scenario.AreaIds.Where(restoredAreaIds.ContainsKey).Select(id => restoredAreaIds[id]).ToArray();
            await _api.SetScenarioAreasAsync(scenarioId, areaIds, ct);
        }

        CurrentPath = path;
        return doc;
    }

    private static void ValidateIdentities(ProjectDoc doc)
    {
        var areaIds = new HashSet<Guid>();
        var taskIds = new HashSet<Guid>();

        foreach (var area in doc.Areas)
        {
            if (area.SourceId == Guid.Empty)
                throw new InvalidDataException("프로젝트 파일의 영역 식별자가 비어 있습니다.");
            if (area.SourceId is Guid areaId && !areaIds.Add(areaId))
                throw new InvalidDataException($"프로젝트 파일에 중복 영역 식별자가 있습니다: {areaId}");

            var seqs = new HashSet<int>();
            foreach (var task in area.Tasks)
            {
                if (!seqs.Add(task.Seq))
                    throw new InvalidDataException($"영역 '{area.Name}'에 중복 작업 순번이 있습니다: {task.Seq}");
                if (task.SourceId == Guid.Empty)
                    throw new InvalidDataException("프로젝트 파일의 작업 식별자가 비어 있습니다.");
                if (task.SourceId is Guid taskId && !taskIds.Add(taskId))
                    throw new InvalidDataException($"프로젝트 파일에 중복 작업 식별자가 있습니다: {taskId}");
            }
        }
    }
}
