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
    private const int FormatVersion = 1;
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
                t.SectionDxfId, t.ProfileId)).ToArray();
            areaDocs.Add(new AreaDoc(a.WallCode, a.Level, a.Name,
                a.UMin, a.VMin, a.UMax, a.VMax, a.StationX, a.StationY, a.StationTheta, taskDocs));
        }

        var doc = new ProjectDoc(FormatVersion, tankId,
            new GeometryDoc(geom.LengthL, geom.WFloor, geom.ThetaLowDeg, geom.HLow,
                geom.HWall, geom.ThetaUpDeg, geom.HUp, geom.LevelZ ?? Array.Empty<double>(),
                geom.OriginOx, geom.OriginOy),
            areaDocs.ToArray());

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

        // DB 재적재: 선창 등록(면 재생성 — 기존 영역은 wall CASCADE로 정리) → 영역 → 작업
        var g = doc.Geometry;
        await _api.RegisterTankGeometryAsync(doc.TankId, g.LengthL, g.WFloor, g.ThetaLowDeg, g.HLow,
            g.HWall, g.ThetaUpDeg, g.HUp, g.LevelZ, g.OriginOx, g.OriginOy, _operatorId, ct);

        foreach (var a in doc.Areas)
        {
            var areaId = await _api.CreateAreaAsync(doc.TankId, a.WallCode, a.Level, a.Name,
                a.UMin, a.VMin, a.UMax, a.VMax, a.StationX, a.StationY, a.StationTheta, _operatorId, ct);
            foreach (var t in a.Tasks)
                await _api.CreateAreaTaskAsync(areaId, t.StartU, t.StartV, t.EndU, t.EndV,
                    t.SeamType, t.SectionDxfId, t.ProfileId, _operatorId, ct);
        }

        CurrentPath = path;
        return doc;
    }
}
