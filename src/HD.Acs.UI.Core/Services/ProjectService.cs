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
    // v3: 영역·작업 식별자(areaId·taskId) 보존. v2: 영역 corners(임의 4점). v1(구파일)=bbox 사각형 폴백
    internal const int FormatVersion = 3;
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

        var doc = new ProjectDoc(FormatVersion, tankId,
            new GeometryDoc(geom.LengthL, geom.WFloor, geom.ThetaLowDeg, geom.HLow,
                geom.HWall, geom.ThetaUpDeg, geom.HUp, geom.LevelZ ?? Array.Empty<double>(),
                geom.OriginOx, geom.OriginOy, geom.ReachZMin, geom.ReachZMax),
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

        // 재적재 전에 파일 자체의 식별자 무결성 검증 — 중복 ID는 서버 409로 중간에 멈춰 DB가 반쯤 적재된 상태가 되므로
        // DB를 건드리기 전에 걸러낸다.
        ValidateIdentities(doc);

        // DB 재적재: 선창 등록(면 재생성 — 기존 영역은 wall CASCADE로 정리) → 영역 → 작업
        var g = doc.Geometry;
        await _api.RegisterTankGeometryAsync(doc.TankId, g.LengthL, g.WFloor, g.ThetaLowDeg, g.HLow,
            g.HWall, g.ThetaUpDeg, g.HUp, g.LevelZ, g.OriginOx, g.OriginOy, _operatorId,
            g.ReachZMin, g.ReachZMax, ct);

        foreach (var a in doc.Areas)
        {
            // v3.1: level은 서버가 영역 z범위로 유도(저장된 a.Level은 무시). AreaId만 사용.
            // corners 없으면(구파일) bbox 사각형으로 폴백.
            var corners = a.Corners ?? new[]
            {
                new[] { a.UMin, a.VMin }, new[] { a.UMax, a.VMin }, new[] { a.UMax, a.VMax }, new[] { a.UMin, a.VMax },
            };
            // v3: areaId·taskId를 그대로 복원한다 — taskId는 SAIGE productId·진행률·검사 이력을 잇는 영구 키이므로
            // 파일을 다시 열었다고 바뀌면 같은 용접선의 이력이 끊긴다 [SAIGE v2.6 §2.5]. 구파일(null)은 서버가 새로 발급.
            // seq·name도 저장값 그대로(종전엔 열 때 name이 사라지고 seq가 재부여됐다).
            var (areaId, _) = await _api.CreateAreaAsync(doc.TankId, a.WallCode, a.Name,
                corners, a.StationX, a.StationY, a.StationTheta, _operatorId, a.StationStandoffM, a.AreaId, ct);
            foreach (var t in a.Tasks.OrderBy(t => t.Seq))
                await _api.CreateAreaTaskAsync(areaId, t.StartU, t.StartV, t.EndU, t.EndV,
                    t.SeamType, t.SectionDxfId, t.ProfileId, _operatorId, t.Seq, t.Name, t.TaskId, ct);
        }

        CurrentPath = path;
        return doc;
    }

    /// <summary>파일 내 식별자 무결성 — 빈 GUID·중복 areaId/taskId·영역 내 중복 seq는 손상/수기 편집 파일로 보고 거부.</summary>
    internal static void ValidateIdentities(ProjectDoc doc)
    {
        var areaIds = new HashSet<Guid>();
        var taskIds = new HashSet<Guid>();
        foreach (var a in doc.Areas)
        {
            if (a.AreaId is Guid aid && (aid == Guid.Empty || !areaIds.Add(aid)))
                throw new InvalidDataException($"프로젝트 파일 손상: 영역 '{a.Name}'의 areaId가 비었거나 중복됩니다 ({aid}).");
            var seqs = new HashSet<int>();
            foreach (var t in a.Tasks)
            {
                if (t.TaskId is Guid tid && (tid == Guid.Empty || !taskIds.Add(tid)))
                    throw new InvalidDataException($"프로젝트 파일 손상: 영역 '{a.Name}' 작업 #{t.Seq}의 taskId가 비었거나 중복됩니다 ({tid}).");
                if (!seqs.Add(t.Seq))
                    throw new InvalidDataException($"프로젝트 파일 손상: 영역 '{a.Name}'에 seq {t.Seq}가 중복됩니다.");
            }
        }
    }
}
