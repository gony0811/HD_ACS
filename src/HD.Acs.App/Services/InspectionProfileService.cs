using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HD.Acs.Core.Reach;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HD.Acs.App.Services;

/// <summary>검사 프로필 경유점 한 점 — 작업물 좌표계 기준. X·Y·Z [mm], Theta(=Ry)·RzDeg·RxDeg [도], Surface = 표면 타입 번호.</summary>
public sealed record ProfileWaypoint(double X, double Y, double Z, double Theta, double RzDeg, double RxDeg, int Surface);

/// <summary>HD_AMR 이 올려 보내는 검사 프로필 한 건. Id = AMR 쪽 프로필 번호(이름을 바꿔도 그대로).</summary>
public sealed record ProfileUpload(int Id, string? Name, string? SeamType, bool PoseAbsolute, DateTimeOffset? UpdatedAt,
    IReadOnlyList<ProfileWaypoint>? Waypoints);

/// <summary>지정표 한 줄 — 이 유형·면·진행 방향(H 가로 / V 세로)의 용접선은 이 프로필(AMR 프로필 번호)로 순회한다.</summary>
public sealed record ProfileAssignment(string? SeamType, string? WallCode, string? Direction, int ProfileId);

/// <summary>
/// 프로필 동기화 요청. Full = 보낸 목록이 그 AMR 의 전체다(목록에 없는 프로필은 삭제된 것으로 표시).
/// Assignments = 지정표 전체(주면 그 AMR 의 표를 통째로 바꾼다, null = 건드리지 않는다).
/// Robot = AMR 이 쓰는 코봇 모델 값(d6·손목 여유·초점 거리) — ACS 설정과 다르면 응답과 로그로 알린다(저장·적용하지 않는다).
/// </summary>
public sealed record ProfileSyncRequest(string? Source, bool Full, IReadOnlyList<ProfileUpload>? Profiles,
    IReadOnlyList<ProfileAssignment>? Assignments = null, AmrRobotModel? Robot = null);

/// <param name="RobotModelMismatch">AMR 이 보낸 코봇 모델 값이 ACS 설정과 다른 항목(같거나 보내지 않았으면 null).</param>
public sealed record ProfileSyncResult(int Added, int Changed, int Unchanged, int Removed, IReadOnlyList<string> ChangedNames,
    int Assignments = 0, bool AssignmentsChanged = false, IReadOnlyList<string>? RobotModelMismatch = null);

/// <summary>용접선에 쓸 순회 프로필 — 경유점과 어느 프로필인지(이름·버전).</summary>
public sealed record SeamScanProfile(string Name, int Version, IReadOnlyList<ScanWaypoint> Waypoints);

/// <summary>프로필 동기화 요청이 잘못됨 → 400.</summary>
public sealed class ProfileSyncException(string message) : Exception(message);

/// <summary>
/// HD_AMR 검사 프로필(순회 경유점) 사본 보관. AMR 이 프로필을 저장할 때 전체 목록을 올려 보내고, ACS 는 내용이 바뀐
/// 프로필만 버전을 올려 저장한다. 리치 시뮬레이터가 순회 판정에 쓴다(<see cref="ToTiltProfile"/>).
/// 로봇 제어가 아니라 계획용 참조 데이터다 — AMR 로 가는 지령은 종전대로 VDA 5050 뿐이다.
/// </summary>
public static class InspectionProfileService
{
    public const int MaxProfiles = 500;
    public const int MaxWaypoints = 2000;
    public const int MaxAssignments = 2000;

    /// <summary>LINE 프로필로 볼 수 있는 Y·Rz·Rx 허용 크기 — 이보다 크면 직선 순회가 아니다.</summary>
    private const double LineTolerance = 0.01;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<ProfileSyncResult> SyncAsync(AcsDbContext db, ProfileSyncRequest req, CancellationToken ct = default)
    {
        var source = (req.Source ?? "").Trim();
        if (source.Length == 0) throw new ProfileSyncException("source(AMR 식별자)가 비어 있습니다.");
        var items = req.Profiles ?? Array.Empty<ProfileUpload>();
        if (items.Count > MaxProfiles) throw new ProfileSyncException($"프로필이 너무 많습니다({items.Count} > {MaxProfiles}).");
        if (items.Select(p => p.Id).Distinct().Count() != items.Count) throw new ProfileSyncException("프로필 id 가 중복됩니다.");
        foreach (var p in items)
        {
            if (string.IsNullOrWhiteSpace(p.Name)) throw new ProfileSyncException($"프로필 {p.Id}: 이름이 비어 있습니다.");
            var wps = p.Waypoints ?? Array.Empty<ProfileWaypoint>();
            if (wps.Count > MaxWaypoints) throw new ProfileSyncException($"프로필 {p.Name}: 경유점이 너무 많습니다({wps.Count} > {MaxWaypoints}).");
            if (wps.Any(w => !Finite(w))) throw new ProfileSyncException($"프로필 {p.Name}: 경유점에 숫자가 아닌 값이 있습니다.");
        }

        var table = new Dictionary<(string, string, string), int>();
        foreach (var a in req.Assignments ?? Array.Empty<ProfileAssignment>())
        {
            var seam = AreaTaskRules.Normalize(a.SeamType);
            var wall = (a.WallCode ?? "").Trim().ToUpperInvariant();
            var dir = (a.Direction ?? "").Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(a.SeamType) || wall.Length == 0 || dir is not ("H" or "V"))
                throw new ProfileSyncException($"지정표: 유형·면·방향(H/V)이 잘못됐습니다({a.SeamType}/{a.WallCode}/{a.Direction}).");
            if (!table.TryAdd((seam, wall, dir), a.ProfileId))
                throw new ProfileSyncException($"지정표: {seam}/{wall}/{dir} 가 중복됩니다.");
        }
        if (table.Count > MaxAssignments) throw new ProfileSyncException($"지정표가 너무 깁니다({table.Count} > {MaxAssignments}).");

        var existing = await db.InspectionProfiles.Where(x => x.Source == source).ToListAsync(ct);
        var byId = existing.ToDictionary(x => x.AmrProfileId);
        var now = DateTimeOffset.UtcNow;
        int added = 0, changed = 0, unchanged = 0, removed = 0;
        var changedNames = new List<string>();

        foreach (var p in items)
        {
            var wps = p.Waypoints ?? Array.Empty<ProfileWaypoint>();
            var seamType = string.IsNullOrWhiteSpace(p.SeamType) ? "LINE" : p.SeamType!.Trim().ToUpperInvariant();
            var hash = ContentHash(seamType, p.PoseAbsolute, wps);
            var name = p.Name!.Trim();
            if (!byId.TryGetValue(p.Id, out var e))
            {
                db.InspectionProfiles.Add(new InspectionProfileEntity
                {
                    Source = source, AmrProfileId = p.Id, Name = name, SeamType = seamType, PoseAbsolute = p.PoseAbsolute,
                    Waypoints = JsonSerializer.Serialize(wps, Json), PointCount = wps.Count, Version = 1, ContentHash = hash,
                    AmrUpdatedAt = p.UpdatedAt, ReceivedAt = now, ChangedAt = now,
                });
                added++;
                changedNames.Add(name);
                continue;
            }
            var contentChanged = e.ContentHash != hash;
            var revived = e.Deleted;
            if (contentChanged)
            {
                e.SeamType = seamType;
                e.PoseAbsolute = p.PoseAbsolute;
                e.Waypoints = JsonSerializer.Serialize(wps, Json);
                e.PointCount = wps.Count;
                e.ContentHash = hash;
                e.Version++;
                e.ChangedAt = now;
            }
            if (contentChanged || revived || e.Name != name) { changed++; changedNames.Add(name); } else unchanged++;
            e.Name = name;
            e.Deleted = false;
            e.AmrUpdatedAt = p.UpdatedAt;
            e.ReceivedAt = now;
        }

        if (req.Full)
        {
            var sent = items.Select(p => p.Id).ToHashSet();
            foreach (var e in existing.Where(x => !x.Deleted && !sent.Contains(x.AmrProfileId)))
            {
                e.Deleted = true;
                e.ChangedAt = now;
                removed++;
                changedNames.Add(e.Name);
            }
        }

        var assignmentsChanged = false;
        if (req.Assignments is not null)
        {
            var old = await db.InspectionProfileAssignments.Where(x => x.Source == source).ToListAsync(ct);
            assignmentsChanged = old.Count != table.Count
                || old.Any(o => !table.TryGetValue((o.SeamType, o.WallCode, o.Direction), out var id) || id != o.AmrProfileId);
            if (assignmentsChanged)
            {
                db.InspectionProfileAssignments.RemoveRange(old);
                db.InspectionProfileAssignments.AddRange(table.Select(kv => new InspectionProfileAssignmentEntity
                {
                    Source = source, SeamType = kv.Key.Item1, WallCode = kv.Key.Item2, Direction = kv.Key.Item3, AmrProfileId = kv.Value,
                }));
            }
        }

        var result = new ProfileSyncResult(added, changed, unchanged, removed, changedNames, table.Count, assignmentsChanged);
        if (added + changed + removed > 0 || assignmentsChanged)
            db.AuditLogs.Add(new AuditLogEntity
            {
                UserId = source, Action = "INSPECTION_PROFILE_SYNC", Target = $"{added + changed + removed} profiles",
                Detail = JsonSerializer.Serialize(result),
            });
        await db.SaveChangesAsync(ct);
        return result;
    }

    /// <summary>이름으로 찾은 프로필(삭제 표시 제외) — 같은 이름이 여럿이면 가장 최근에 바뀐 것.</summary>
    public static async Task<InspectionProfileEntity?> FindByNameAsync(AcsDbContext db, string name, CancellationToken ct = default,
        string? source = null, IReadOnlyCollection<string>? excludeSources = null)
    {
        var rows = await db.InspectionProfiles.AsNoTracking().Where(x => x.Name == name && !x.Deleted).ToListAsync(ct);
        return rows.Where(x => SourceMatches(x.Source, source, excludeSources)).OrderByDescending(x => x.ChangedAt).FirstOrDefault();
    }

    /// <summary>
    /// 이 source 의 행인가 — <paramref name="source"/> 를 주면 그 AMR 만(대소문자 무시), 없으면 <paramref name="excludeSources"/>
    /// (다른 검사 종류에 지정된 AMR)를 뺀 전부.
    /// </summary>
    public static bool SourceMatches(string rowSource, string? source, IReadOnlyCollection<string>? excludeSources) =>
        source is not null
            ? string.Equals(rowSource, source, StringComparison.OrdinalIgnoreCase)
            : excludeSources is null || !excludeSources.Contains(rowSource, StringComparer.OrdinalIgnoreCase);

    /// <summary>용접선의 진행 방향 — 면 좌표에서 세로(v) 변화가 가로(u) 변화보다 크면 "V", 아니면 "H".</summary>
    public static string DirectionOf(double startU, double startV, double endU, double endV) =>
        Math.Abs(endV - startV) > Math.Abs(endU - startU) ? "V" : "H";

    /// <summary>
    /// 지정표를 읽어 (유형, 면, 방향) → 순회 프로필 찾기 함수를 만든다. AMR 하나의 표를 쓴다 — <paramref name="source"/> 를 주면
    /// 그 AMR(검사 종류에 지정된 source), 없으면 <paramref name="excludeSources"/>(다른 검사 종류의 AMR)를 뺀 가장 최근에 받은 AMR.
    /// 지정된 프로필이 없거나 지워졌거나 절대 자세 프로필이 아니면 그 조합은 null(호출측이 종전 방식으로 판정).
    /// </summary>
    public static async Task<Func<string, string, string, SeamScanProfile?>> LoadSeamProfilesAsync(AcsDbContext db, CancellationToken ct = default,
        string? source = null, IReadOnlyCollection<string>? excludeSources = null)
    {
        var profiles = (await db.InspectionProfiles.AsNoTracking().Where(x => !x.Deleted).ToListAsync(ct))
            .Where(x => SourceMatches(x.Source, source, excludeSources)).ToList();
        source = profiles.OrderByDescending(x => x.ReceivedAt).FirstOrDefault()?.Source;
        if (source is null) return (_, _, _) => null;
        var rows = await db.InspectionProfileAssignments.AsNoTracking().Where(x => x.Source == source).ToListAsync(ct);
        var scans = new Dictionary<int, SeamScanProfile?>();
        SeamScanProfile? Scan(int id)
        {
            if (scans.TryGetValue(id, out var hit)) return hit;
            var e = profiles.FirstOrDefault(x => x.Source == source && x.AmrProfileId == id);
            var wps = e is { PoseAbsolute: true } ? WaypointsOf(e) : null;
            return scans[id] = e is null || wps is not { Count: > 0 } ? null
                : new SeamScanProfile(e.Name, e.Version, wps.Select(w => new ScanWaypoint(w.X, w.Y, w.Z, w.RxDeg, w.Theta, w.RzDeg)).ToList());
        }
        var map = rows.ToDictionary(r => (r.SeamType, r.WallCode, r.Direction), r => Scan(r.AmrProfileId));
        return (seamType, wallCode, direction) =>
            map.TryGetValue((AreaTaskRules.Normalize(seamType), wallCode.ToUpperInvariant(), direction), out var p) ? p : null;
    }

    public static IReadOnlyList<ProfileWaypoint> WaypointsOf(InspectionProfileEntity e) =>
        JsonSerializer.Deserialize<List<ProfileWaypoint>>(e.Waypoints, Json) ?? new List<ProfileWaypoint>();

    /// <summary>
    /// 리치 시뮬레이터 순회 프로필([[거리 mm, 기울기 도, Z mm], …])로 바꾼다. 직선 프로필(LINE · 절대 자세 · 모든 점의 Y·Rz·Rx 가 0)만
    /// 바꿀 수 있고, 아니면 null 과 사유를 돌려준다 — 교차(CROSS) 프로필은 시뮬레이터가 아직 풀지 못한다.
    /// </summary>
    public static (IReadOnlyList<double[]>? Profile, string? Reason) ToTiltProfile(InspectionProfileEntity e)
    {
        if (!e.SeamType.StartsWith("LINE", StringComparison.OrdinalIgnoreCase)) return (null, $"직선 프로필이 아닙니다({e.SeamType}).");
        if (!e.PoseAbsolute) return (null, "절대 자세 프로필이 아닙니다.");
        var wps = WaypointsOf(e);
        if (wps.Count < 2) return (null, "경유점이 2개 미만입니다.");
        if (wps.Any(w => Math.Abs(w.Y) > LineTolerance || Math.Abs(w.RzDeg) > LineTolerance || Math.Abs(w.RxDeg) > LineTolerance))
            return (null, "Y·Rz·Rx 가 0 이 아닌 경유점이 있습니다.");
        return (wps.Select(w => new[] { w.X, w.Theta, w.Z }).ToList(), null);
    }

    /// <summary>내용 지문 — 유형·자세 방식·경유점 값. 이름·수정 시각은 넣지 않는다(이름만 바꾸면 버전이 오르지 않는다).</summary>
    public static string ContentHash(string seamType, bool poseAbsolute, IReadOnlyList<ProfileWaypoint> wps)
    {
        var sb = new StringBuilder().Append(seamType).Append('|').Append(poseAbsolute ? '1' : '0');
        foreach (var w in wps)
            sb.Append('|').Append(N(w.X)).Append(',').Append(N(w.Y)).Append(',').Append(N(w.Z)).Append(',').Append(N(w.Theta))
                .Append(',').Append(N(w.RzDeg)).Append(',').Append(N(w.RxDeg)).Append(',').Append(w.Surface);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static bool Finite(ProfileWaypoint w) =>
        double.IsFinite(w.X) && double.IsFinite(w.Y) && double.IsFinite(w.Z) && double.IsFinite(w.Theta)
        && double.IsFinite(w.RzDeg) && double.IsFinite(w.RxDeg);
}
