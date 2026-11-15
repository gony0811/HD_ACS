using System.Text.Json;
using System.Text.RegularExpressions;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HD.Acs.App.Services;

/// <summary>선창 ID 변경 결과 — 옮긴 행 수와 층 맵 ID 대응(옛 → 새).</summary>
public sealed record TankRenameResult(string OldTankId, string NewTankId, IReadOnlyDictionary<string, string> Maps,
    int Walls, int Areas, int Scenarios, int Calibrations, int CalibrationPoints, int Missions, int WorkItems, int Robots);

/// <summary>
/// 선창 ID(tank_id) 변경 — 선창 형상·면·영역·시나리오·용접선 원본·층 맵(`{선창}-L{층}`)과 그 맵을 가리키는
/// 캘리브레이션·기준점·노드·엣지·존·미션·정차 큐·로봇 보고 층을 한 트랜잭션으로 옮긴다.
/// 영역 ID·taskId 는 그대로라 검사 기록과 세이지 쪽 용접선 연결이 유지된다.
/// 고치지 않는 것: 지난 배차 기록(work_item.actions)·AMR 로그 안의 jobRef·anchorGroupId 문자열(기록이다).
/// 키를 직접 바꿀 수 없어(외래 키에 ON UPDATE CASCADE 없음) 새 행 추가 → 자식 갱신 → 옛 행 삭제 순으로 한다.
/// </summary>
public sealed class TankRenameService(AcsDbContext db, ILogger<TankRenameService> log)
{
    public sealed class NotFoundException(string m) : Exception(m);
    public sealed class ConflictException(string m) : Exception(m);
    public sealed class InvalidException(string m) : Exception(m);

    /// <summary>선창 ID 형식 — 영문·숫자로 시작, 영문·숫자·`_`·`-`, 1~32자. MQTT 토픽·URL·파일 이름에 그대로 쓰인다.</summary>
    public static readonly Regex IdPattern = new(@"^[A-Za-z0-9][A-Za-z0-9_-]{0,31}$", RegexOptions.Compiled);

    /// <summary>형식 문제를 사유로 돌려준다(없으면 null).</summary>
    public static string? IdProblem(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "새 선창 ID 가 비어 있습니다.";
        if (!IdPattern.IsMatch(id)) return "선창 ID 는 영문·숫자로 시작하고 영문·숫자·'_'·'-' 만 쓸 수 있습니다(1~32자).";
        // 층 맵 ID 는 `{선창}-L{층}` 이고 끝의 `-L{n}` 으로 층을 읽는다 — 선창 ID 자체가 그 꼴로 끝나면 층 번호와 헷갈린다
        if (Regex.IsMatch(id, @"-[Ll]\d+$")) return "선창 ID 는 '-L숫자' 로 끝날 수 없습니다(층 표시와 겹칩니다).";
        return null;
    }

    /// <summary>옛 맵 ID → 새 맵 ID. 표준 꼴(`{옛 선창}-…`)이면 접두사만 바꾸고, 아니면 그대로 둔다(선창 소속만 바뀐다).</summary>
    public static string MapIdFor(string oldMapId, string oldTankId, string newTankId) =>
        oldMapId.StartsWith(oldTankId + "-", StringComparison.Ordinal) ? newTankId + oldMapId[oldTankId.Length..] : oldMapId;

    public async Task<TankRenameResult> RenameAsync(string oldId, string? newIdRaw, string? userId, CancellationToken ct = default)
    {
        var newId = newIdRaw?.Trim() ?? "";
        if (IdProblem(newId) is { } problem) throw new InvalidException(problem);
        if (string.Equals(oldId, newId, StringComparison.Ordinal)) throw new InvalidException("새 선창 ID 가 지금 ID 와 같습니다.");

        var geom = await db.TankGeometries.FirstOrDefaultAsync(g => g.TankId == oldId, ct)
                   ?? throw new NotFoundException($"선창 '{oldId}' 없음");
        // 새 ID 를 쓰는 데이터가 하나라도 있으면 두 선창이 섞이므로 거부
        if (await db.TankGeometries.AnyAsync(g => g.TankId == newId, ct)
            || await db.Maps.AnyAsync(m => m.TankId == newId, ct)
            || await db.InspectionAreas.AnyAsync(a => a.TankId == newId, ct)
            || await db.Scenarios.AnyAsync(s => s.TankId == newId, ct))
            throw new ConflictException($"선창 ID '{newId}' 는 이미 쓰이고 있습니다.");

        var scenarios = await db.Scenarios.Where(s => s.TankId == oldId).ToListAsync(ct);
        var scenarioIds = scenarios.Select(s => s.ScenarioId).ToList();
        var active = await db.ScenarioRuns.AsNoTracking()
            .Where(r => (r.State == "RUNNING" || r.State == "WAITING_FLOOR_TRANSFER") && scenarioIds.Contains(r.ScenarioId))
            .Select(r => (Guid?)r.RunId).FirstOrDefaultAsync(ct);
        if (active is not null)
            throw new ConflictException($"진행 중인 run({active})이 있습니다 — 중단(abort)하거나 끝난 뒤에 선창 ID 를 바꾸세요.");

        var oldWalls = await db.Walls.Where(w => w.TankId == oldId).ToListAsync(ct);
        var oldMaps = await db.Maps.Where(m => m.TankId == oldId).ToListAsync(ct);
        var mapIds = oldMaps.ToDictionary(m => m.MapId, m => MapIdFor(m.MapId, oldId, newId));
        var renamedMaps = mapIds.Where(kv => kv.Key != kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);
        if (await db.Maps.AnyAsync(m => renamedMaps.Values.Contains(m.MapId), ct))
            throw new ConflictException("새 선창 ID 로 만들 층 맵 ID 가 이미 있습니다.");
        var oldMapIds = renamedMaps.Keys.ToList();
        var oldCals = await db.MapCalibrations.Where(c => oldMapIds.Contains(c.MapId)).ToListAsync(ct);

        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;

        // ① 새 부모 행 — 형상·면·층 맵·캘리브레이션(키에 맵 ID 가 들어 있다)
        db.TankGeometries.Add(Clone(geom, g => g.TankId = newId));
        foreach (var w in oldWalls) db.Walls.Add(Clone(w, x => x.TankId = newId));
        foreach (var m in oldMaps)
        {
            if (renamedMaps.TryGetValue(m.MapId, out var nm))
                db.Maps.Add(Clone(m, x =>
                {
                    x.MapId = nm; x.TankId = newId;
                    if (x.Name == $"{oldId} L{x.Level}") x.Name = $"{newId} L{x.Level}";   // 자동 생성 이름만 따라 바꾼다
                }));
            else m.TankId = newId;   // 표준 꼴이 아닌 맵 ID 는 그대로 두고 소속만 바꾼다
        }
        foreach (var c in oldCals) db.MapCalibrations.Add(Clone(c, x => x.MapId = renamedMaps[c.MapId]));
        await db.SaveChangesAsync(ct);

        // ② 자식 행을 새 부모로
        var areas = await db.InspectionAreas.Where(a => a.TankId == oldId).ToListAsync(ct);
        foreach (var a in areas) a.TankId = newId;
        foreach (var s in scenarios) s.TankId = newId;
        foreach (var s in await db.WeldSeams.Where(s => s.TankId == oldId).ToListAsync(ct)) s.TankId = newId;
        // 측면 정차 층+벽면 설정 — 키에 선창 ID 가 들어 있어 새 행 추가 + 옛 행 삭제(외래 키 없음)
        var sideModes = await db.WallStationModes.Where(m => m.TankId == oldId).ToListAsync(ct);
        foreach (var m in sideModes) db.WallStationModes.Add(Clone(m, x => x.TankId = newId));
        db.WallStationModes.RemoveRange(sideModes);

        var points = await db.MapCalibrationPoints.Where(p => oldMapIds.Contains(p.MapId)).ToListAsync(ct);
        foreach (var p in points) p.MapId = renamedMaps[p.MapId];
        foreach (var n in await db.Nodes.Where(n => oldMapIds.Contains(n.MapId)).ToListAsync(ct)) n.MapId = renamedMaps[n.MapId];
        foreach (var e in await db.Edges.Where(e => oldMapIds.Contains(e.MapId)).ToListAsync(ct)) e.MapId = renamedMaps[e.MapId];
        foreach (var z in await db.Zones.Where(z => oldMapIds.Contains(z.MapId)).ToListAsync(ct)) z.MapId = renamedMaps[z.MapId];
        var missions = await db.Missions.Where(m => oldMapIds.Contains(m.MapId)).ToListAsync(ct);
        foreach (var m in missions) m.MapId = renamedMaps[m.MapId];
        var items = await db.WorkItems.Where(w => oldMapIds.Contains(w.MapId)).ToListAsync(ct);
        foreach (var w in items) w.MapId = renamedMaps[w.MapId];
        // 로봇이 마지막으로 보고한 층 — AMR 이 새 이름을 받기 전에도 화면·배차가 새 맵을 가리키게 한다
        var robots = await db.RobotContexts
            .Where(c => (c.ReportedMapId != null && oldMapIds.Contains(c.ReportedMapId))
                        || (c.ManualMapId != null && oldMapIds.Contains(c.ManualMapId))).ToListAsync(ct);
        foreach (var c in robots)
        {
            if (c.ReportedMapId is { } r && renamedMaps.TryGetValue(r, out var nr)) c.ReportedMapId = nr;
            if (c.ManualMapId is { } mm && renamedMaps.TryGetValue(mm, out var nmm)) c.ManualMapId = nmm;
        }
        await db.SaveChangesAsync(ct);

        // ③ 옛 부모 행 삭제(자식이 모두 옮겨 간 뒤). 면을 먼저 지우고 저장한 다음 형상을 지운다 —
        //    DB 의 면 → 형상 외래 키가 ON DELETE CASCADE 라, 형상을 먼저 지우면 면이 함께 사라져
        //    뒤따르는 면 삭제가 "0행"으로 실패한다(EF 는 이 관계를 몰라 삭제 순서를 보장하지 않는다).
        db.MapCalibrations.RemoveRange(oldCals);
        db.Walls.RemoveRange(oldWalls);
        await db.SaveChangesAsync(ct);
        db.Maps.RemoveRange(oldMaps.Where(m => renamedMaps.ContainsKey(m.MapId)));
        db.TankGeometries.Remove(geom);
        var result = new TankRenameResult(oldId, newId, mapIds, oldWalls.Count, areas.Count, scenarios.Count,
            oldCals.Count, points.Count, missions.Count, items.Count, robots.Count);
        db.AuditLogs.Add(new AuditLogEntity
        {
            UserId = userId ?? "", Action = "TANK_RENAME", Target = newId,
            Detail = JsonSerializer.Serialize(result),
        });
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);

        log.LogWarning("선창 ID 변경: {Old} → {New} (면 {Walls} · 영역 {Areas} · 시나리오 {Scenarios} · 층 맵 {Maps} · 정차 큐 {Items}). " +
                       "AMR 은 다음 지령부터 새 층 맵 ID 를 받는다 — 세이지 조회 주소의 tankId 도 바뀐다.",
            oldId, newId, oldWalls.Count, areas.Count, scenarios.Count, renamedMaps.Count, items.Count);
        return result;
    }

    /// <summary>엔티티 한 행을 값 그대로 복사한 뒤 키만 고친다 — 열이 늘어도 빠뜨리지 않는다.</summary>
    private T Clone<T>(T source, Action<T> setKey) where T : class, new()
    {
        var copy = new T();
        db.Entry(copy).CurrentValues.SetValues(db.Entry(source).CurrentValues);
        setKey(copy);
        return copy;
    }
}

/// <summary>
/// AMR 이 보고한 층 맵 ID 를 ACS 의 층 맵으로 맞춘다. AMR 은 층만 알고 선창 이름은 ACS 가 준 값을 되돌려 보고하므로,
/// 선창 ID 를 바꾼 직후나 AMR 재시작 직후에는 옛 이름·층만(`L4`) 보고할 수 있다 — 그대로 글자 비교하면 "층 불일치"로 지령이
/// 나가지 않고 AMR 은 새 이름을 받을 기회가 없어 서로 막힌다. 그래서 등록된 맵이 아니면 **층 번호**로 찾는다.
/// 전제: 로봇이 있는 선창이 하나(그 층 번호의 활성 맵이 하나). 여럿이면 정할 수 없어 보고값을 그대로 둔다.
/// </summary>
public static class ReportedMap
{
    /// <summary>`{선창}-L{n}` · `L{n}` · `{n}` → 층 번호(1 이상). 읽을 수 없으면 null.</summary>
    public static int? LevelOf(string? mapId)
    {
        if (string.IsNullOrWhiteSpace(mapId)) return null;
        var s = mapId.Trim();
        var i = s.LastIndexOf("-L", StringComparison.OrdinalIgnoreCase);
        var tail = i >= 0 ? s[(i + 2)..] : s.StartsWith('L') || s.StartsWith('l') ? s[1..] : s;
        return int.TryParse(tail, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n >= 1
            ? n : null;
    }

    /// <summary>보고값 → ACS 층 맵 ID. 등록된 맵이면 그대로, 아니면 층 번호가 같은 활성 맵이 하나일 때 그 맵. 못 정하면 보고값 그대로.</summary>
    public static async Task<string?> ResolveAsync(AcsDbContext db, string? reported, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reported)) return reported;
        if (await db.Maps.AsNoTracking().AnyAsync(m => m.MapId == reported, ct)) return reported;
        if (LevelOf(reported) is not int level) return reported;
        var candidates = await db.Maps.AsNoTracking().Where(m => m.Level == level && m.IsActive)
            .Select(m => m.MapId).Take(2).ToListAsync(ct);
        return candidates.Count == 1 ? candidates[0] : reported;
    }
}
