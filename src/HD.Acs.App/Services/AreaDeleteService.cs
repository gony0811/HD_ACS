using System.Text.Json;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HD.Acs.App.Services;

/// <summary>삭제 거부 — 작업이 옮겨 갈 영역이 없는데 함께 지우라는 지시가 없을 때(409).</summary>
public sealed class AreaDeleteBlockedException(string message, IReadOnlyList<string> orphans) : Exception(message)
{
    public IReadOnlyList<string> Orphans { get; } = orphans;
}

/// <summary>삭제 결과 — 원래 영역으로 돌아간 작업과 함께 지워진 작업.</summary>
public sealed record AreaDeleteResult(
    IReadOnlyList<AreaDeleteMove> Moved,
    IReadOnlyList<string> DeletedTasks);

public sealed record AreaDeleteMove(Guid TaskId, string? TaskName, Guid ToAreaId, string ToAreaName, int NewSeq);

/// <summary>
/// 영역 삭제 — 그 안의 작업을 <b>원래 영역으로 되돌린 뒤</b> 영역을 지운다(2026-11-18 현장: 분리 영역 L1-R03C12-S1 을 지우자
/// 작업이 CASCADE 로 함께 사라짐).
///
/// 작업마다 옮겨 갈 영역 = 같은 선창·면의 다른 영역 중 작업(시작·끝·교차 가지)이 들어가는 곳. 이 영역을 만든 TASK 묶음 분리
/// (감사로그 AREA_SPLIT_TASKS 의 sourceAreaIds)의 원래 영역을 먼저 보고, 없으면 작업을 품는 다른 영역(정렬 순서 앞).
/// taskId 는 그대로 — 검사 기록·SAIGE 이력이 이어진다. 받은 영역은 작업 순번을 "라인 순"으로 다시 매긴다(분리와 같은 규칙,
/// 도구 형식 이름 H01·V03 만 새 순번으로).
///
/// 갈 곳이 없는 작업이 있으면 <paramref name="deleteOrphanTasks"/> 가 false 일 때 지우지 않고 거부한다(되돌릴 수 없으므로).
/// </summary>
public sealed class AreaDeleteService(AcsDbContext db)
{
    public async Task<AreaDeleteResult?> DeleteAsync(Guid areaId, bool deleteOrphanTasks, string? userId, CancellationToken ct = default)
    {
        var area = await db.InspectionAreas.FirstOrDefaultAsync(a => a.AreaId == areaId, ct);
        if (area is null) return null;
        var tasks = await db.AreaTasks.Where(t => t.AreaId == areaId).ToListAsync(ct);

        var others = await db.InspectionAreas
            .Where(a => a.TankId == area.TankId && a.WallCode == area.WallCode && a.AreaId != areaId)
            .OrderBy(a => a.SortOrder).ThenBy(a => a.Name)
            .ToListAsync(ct);
        var sources = await SplitSourcesAsync(areaId, ct);
        var candidates = others.Where(a => sources.Contains(a.AreaId))
            .Concat(others.Where(a => !sources.Contains(a.AreaId)))
            .ToList();

        var homes = new Dictionary<Guid, InspectionAreaEntity>();
        var orphans = new List<AreaTaskEntity>();
        foreach (var t in tasks)
        {
            var home = candidates.FirstOrDefault(a => Fits(t, a));
            if (home is null) orphans.Add(t); else homes[t.TaskId] = home;
        }

        if (orphans.Count > 0 && !deleteOrphanTasks)
        {
            var names = orphans.OrderBy(t => t.Seq).Select(Label).ToList();
            throw new AreaDeleteBlockedException(
                $"영역 '{area.Name}'의 작업 {orphans.Count}개는 옮겨 갈 영역이 없습니다(같은 면의 어느 영역에도 들어가지 않음) — " +
                "함께 지우려면 확인하세요: " + string.Join(", ", names), names);
        }

        // 받는 영역마다 기존 작업 + 들어온 작업을 라인 순으로 다시 매긴다. (area_id, seq) 유일 — 음수로 비켜 두고 확정.
        var moved = new List<AreaDeleteMove>();
        var renumbered = new List<AreaTaskEntity>();
        foreach (var grp in homes.GroupBy(kv => kv.Value.AreaId))
        {
            var dest = grp.First().Value;
            var incoming = tasks.Where(t => homes.TryGetValue(t.TaskId, out var h) && h.AreaId == dest.AreaId).ToList();
            var existing = await db.AreaTasks.Where(t => t.AreaId == dest.AreaId).ToListAsync(ct);
            var ordered = AreaRules.OrderByLine(existing.Concat(incoming));
            for (var i = 0; i < ordered.Count; i++)
            {
                var t = ordered[i];
                t.AreaId = dest.AreaId;
                t.Name = AreaRules.RenameForSeq(t, i + 1);
                t.Seq = -(i + 1);
                renumbered.Add(t);
                if (incoming.Contains(t)) moved.Add(new AreaDeleteMove(t.TaskId, t.Name, dest.AreaId, dest.Name, i + 1));
            }
        }

        db.AreaTasks.RemoveRange(orphans);   // 관계형 DB 는 CASCADE 로도 지워지지만 InMemory·명시성을 위해
        db.AuditLogs.Add(new AuditLogEntity
        {
            UserId = userId ?? "", Action = "AREA_DELETE", Target = areaId.ToString(),
            Detail = JsonSerializer.Serialize(new
            {
                area.Name, area.WallCode, area.Level, area.Corners,
                moved = moved.Select(m => new { m.TaskId, m.TaskName, m.ToAreaName, m.NewSeq }),
                deletedTasks = orphans.Select(t => new { t.TaskId, t.Name, t.Seq, t.SeamType, t.StartU, t.StartV, t.EndU, t.EndV }),
            }),
        });

        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        await db.SaveChangesAsync(ct);          // 작업을 옮긴 뒤(음수 순번) 영역 삭제 — 옮긴 작업은 CASCADE 대상이 아니다
        db.InspectionAreas.Remove(area);
        await db.SaveChangesAsync(ct);
        foreach (var t in renumbered) t.Seq = -t.Seq;
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);

        moved.Sort((a, b) => string.CompareOrdinal(a.ToAreaName, b.ToAreaName) is var c and not 0 ? c : a.NewSeq.CompareTo(b.NewSeq));
        return new AreaDeleteResult(moved, orphans.OrderBy(t => t.Seq).Select(Label).ToList());
    }

    private static string Label(AreaTaskEntity t) => t.Name ?? $"#{t.Seq}";

    /// <summary>작업의 시작·끝·교차 가지가 영역 폴리곤 안에 모두 들어가는지(등록 규칙과 같은 판정).</summary>
    private static bool Fits(AreaTaskEntity t, InspectionAreaEntity a)
    {
        if (AreaTaskRules.Validate(t.SeamType, a.Corners, t.StartU, t.StartV, t.EndU, t.EndV) is not null) return false;
        var pts = string.IsNullOrWhiteSpace(t.Points) ? null : JsonSerializer.Deserialize<double[][]>(t.Points);
        return AreaTaskRules.ValidatePoints(t.SeamType, a.Corners, pts) is null;
    }

    /// <summary>이 영역을 만든 TASK 묶음 분리의 원래 영역들(감사로그 AREA_SPLIT_TASKS).</summary>
    private async Task<HashSet<Guid>> SplitSourcesAsync(Guid areaId, CancellationToken ct)
    {
        var target = areaId.ToString();
        var details = await db.AuditLogs.AsNoTracking()
            .Where(l => l.Action == "AREA_SPLIT_TASKS" && l.Target == target)
            .Select(l => l.Detail).ToListAsync(ct);
        var set = new HashSet<Guid>();
        foreach (var d in details)
        {
            if (string.IsNullOrWhiteSpace(d)) continue;
            try
            {
                using var doc = JsonDocument.Parse(d);
                if (doc.RootElement.TryGetProperty("sourceAreaIds", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var e in arr.EnumerateArray())
                        if (e.TryGetGuid(out var g)) set.Add(g);
            }
            catch (JsonException) { }
        }
        return set;
    }
}
