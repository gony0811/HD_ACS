using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HD.Acs.App.Services;

/// <summary>HD_AMR 교시 위치 한 개 — Teaching 슬롯 키(home·home2·ready·wall.T …)와 관절값 J1~J6 [도].</summary>
public sealed record TeachingJointsUpload(string? Key, double[]? Joints);

/// <summary>
/// HD_AMR 교시 위치 사본 — 프로필 전송(<c>PUT /api/inspection-profiles/sync</c>)에 함께 실려 온다. 리치 시뮬레이터 출발 자세(②)의
/// 정본이 된다: AMR 이 홈 위치_2 등을 다시 교시하면 다음 전송부터 ACS 판정이 따라간다(<c>Acs:Reach:StartJoints</c> 설정은 대체값).
/// 계획용 참조 데이터 — AMR 로 가는 지령은 종전대로 VDA 5050 뿐이다(ADR-014).
/// </summary>
public static class AmrTeachingService
{
    public const int MaxPositions = 200;

    /// <summary>출발 자세를 받는 면 코드 — 모르는 면은 버린다(AMR 쪽에서 면이 늘어도 전송 전체를 거부하지 않게).</summary>
    public static readonly string[] WallCodes = { "B", "SL", "PL", "SM", "PM", "SU", "PU", "T", "F", "A" };

    /// <summary>상부 면(T·SU·PU) — AMR 이 면별 출발 자세를 보내지 않았을 때 홈 위치_2 를, 그 외 면은 작업 준비 위치를 쓴다.</summary>
    private static readonly HashSet<string> UpperWalls = new(StringComparer.OrdinalIgnoreCase) { "T", "SU", "PU" };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 받은 값을 검사·정리한다. 관절값이 6개가 아니거나 숫자가 아니거나 ±720° 를 넘으면 <see cref="ProfileSyncException"/>(400).
    /// 면별 출발 자세가 없으면 교시 위치에서 만든다(상부 면 = home2, 그 외 = ready — HD_AMR 기본 규칙).
    /// 둘 다 없으면 null(구버전 AMR — 종전대로 설정 사용).
    /// </summary>
    public static (List<TeachingJointsUpload> Positions, Dictionary<string, double[]> Starts)? Normalize(
        IReadOnlyList<TeachingJointsUpload>? positions, IReadOnlyDictionary<string, double[]>? starts)
    {
        if (positions is null && starts is null) return null;
        var list = new List<TeachingJointsUpload>();
        foreach (var p in positions ?? Array.Empty<TeachingJointsUpload>())
        {
            var key = (p.Key ?? "").Trim();
            if (key.Length == 0) throw new ProfileSyncException("교시 위치: key 가 비어 있습니다.");
            list.Add(new TeachingJointsUpload(key, Checked(p.Joints, $"교시 위치 {key}")));
        }
        if (list.Count > MaxPositions) throw new ProfileSyncException($"교시 위치가 너무 많습니다({list.Count} > {MaxPositions}).");
        if (list.Select(p => p.Key).Distinct(StringComparer.Ordinal).Count() != list.Count)
            throw new ProfileSyncException("교시 위치 key 가 중복됩니다.");

        var result = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        if (starts is not null)
        {
            foreach (var (code, j) in starts)
            {
                var wall = (code ?? "").Trim().ToUpperInvariant();
                if (!WallCodes.Contains(wall)) continue;
                result[wall] = Checked(j, $"출발 자세 {wall}");
            }
        }
        else
        {
            var byKey = list.ToDictionary(p => p.Key!, p => p.Joints!, StringComparer.Ordinal);
            foreach (var wall in WallCodes)
                if (byKey.TryGetValue(UpperWalls.Contains(wall) ? "home2" : "ready", out var j)) result[wall] = j;
        }
        return (list, result);
    }

    private static double[] Checked(double[]? j, string what) =>
        j is { Length: 6 } && j.All(v => double.IsFinite(v) && Math.Abs(v) <= 720)
            ? j.ToArray()
            : throw new ProfileSyncException($"{what}: 관절값은 숫자 6개(±720° 이내)여야 합니다.");

    /// <summary>저장 — 값이 바뀌었으면 true(감사로그·응답 표시용). 저장(SaveChanges)은 호출측이 한다.</summary>
    public static async Task<bool> UpsertAsync(AcsDbContext db, string source,
        List<TeachingJointsUpload> positions, Dictionary<string, double[]> starts, CancellationToken ct = default)
    {
        var posJson = JsonSerializer.Serialize(positions.Select(p => new { key = p.Key, joints = p.Joints }), Json);
        var startJson = JsonSerializer.Serialize(starts.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(kv => kv.Key, kv => kv.Value), Json);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(posJson + "|" + startJson)));
        var now = DateTimeOffset.UtcNow;
        var e = await db.AmrTeachings.FirstOrDefaultAsync(x => x.Source == source, ct);
        if (e is null)
        {
            db.AmrTeachings.Add(new AmrTeachingEntity
            {
                Source = source, TeachingPositions = posJson, StartJoints = startJson, ContentHash = hash, ReceivedAt = now, ChangedAt = now,
            });
            return true;
        }
        e.ReceivedAt = now;
        if (e.ContentHash == hash) return false;
        (e.TeachingPositions, e.StartJoints, e.ContentHash, e.ChangedAt) = (posJson, startJson, hash, now);
        return true;
    }

    public static Dictionary<string, double[]> StartsOf(AmrTeachingEntity e) =>
        new(JsonSerializer.Deserialize<Dictionary<string, double[]>>(e.StartJoints, Json) ?? new(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 리치 시뮬레이터가 쓸 AMR 출발 자세 — <paramref name="source"/> 를 주면 그 AMR, 없으면 <paramref name="excludeSources"/>
    /// (다른 검사 종류의 AMR)를 뺀 가장 최근에 받은 AMR(프로필 지정표와 같은 규칙). 받은 적이 없으면 null.
    /// </summary>
    public static async Task<(string Source, Dictionary<string, double[]> Starts)?> LoadStartJointsAsync(AcsDbContext db,
        CancellationToken ct = default, string? source = null, IReadOnlyCollection<string>? excludeSources = null)
    {
        var rows = await db.AmrTeachings.AsNoTracking().ToListAsync(ct);
        var row = rows.Where(x => InspectionProfileService.SourceMatches(x.Source, source, excludeSources))
            .OrderByDescending(x => x.ReceivedAt).FirstOrDefault();
        if (row is null) return null;
        var starts = StartsOf(row);
        return starts.Count == 0 ? null : (row.Source, starts);
    }

    /// <summary>
    /// AMR 출발 자세가 ACS 설정(<c>Acs:Reach:StartJoints</c>)과 다른 면 — 면마다 한 줄. ACS 는 이 면들에서 설정 대신 AMR 값을 쓴다.
    /// 0.01° 이내 차이는 같은 값으로 본다.
    /// </summary>
    public static IReadOnlyList<string> Differences(IReadOnlyDictionary<string, double[]> configured, IReadOnlyDictionary<string, double[]> amr)
    {
        var list = new List<string>();
        foreach (var wall in WallCodes)
        {
            if (!amr.TryGetValue(wall, out var a)) continue;
            if (configured.TryGetValue(wall, out var c) && c.Length == 6 && Enumerable.Range(0, 6).All(i => Math.Abs(a[i] - c[i]) <= 0.01))
                continue;
            list.Add($"{wall}: AMR [{Fmt(a)}] ≠ 설정 {(configured.TryGetValue(wall, out var cc) ? $"[{Fmt(cc)}]" : "없음")}");
        }
        return list;
    }

    private static string Fmt(double[] j) => string.Join(", ", j.Select(v => v.ToString("0.###", CultureInfo.InvariantCulture)));
}
