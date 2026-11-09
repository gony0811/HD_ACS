using System.Text.Json;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HD.Acs.App.Services;

/// <summary>재분할 적용 요청 — 묶음마다 작업·정차(도면 x·y·방향)·리프트·(선택) 새 영역 이름.</summary>
public sealed record ApplyAreaSplitRequest(List<AreaSplitGroupRequest> Groups, string? UserId = null);

public sealed record AreaSplitGroupRequest(Guid[] TaskIds, double StationX, double StationY, double StationTheta,
    double? StationLiftMm = null, string? Name = null);

/// <summary>재분할 적용 결과 — 영역마다 id·이름·층·작업 수(첫 항목 = 원래 영역).</summary>
public sealed record AreaSplitResult(List<AreaSplitResultArea> Areas);
public sealed record AreaSplitResultArea(Guid AreaId, string Name, int Level, int TaskCount, bool Created);

/// <summary>적용 거부 — HTTP 400(형식·검증) / 404(영역 없음) / 409(이름 충돌·진행 중 run).</summary>
public sealed class AreaSplitException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// 영역 재분할 적용 — 재분할 제안(<c>GET /api/reach-sim/areas/{id}/split-plan</c>)의 묶음을 한 트랜잭션으로 반영한다.
/// 가장 큰 묶음은 원래 영역에 남기고(영역 id·이름 유지 — 사각형만 그 묶음으로 축소), 나머지 묶음은 새 영역으로 만들어 작업을 옮긴다
/// (taskId 유지 — SAIGE productId·attempt 누적이 끊기지 않는다). 사각형은 TASK 묶음 분리와 같은 자동 사각형, 작업 순번은 라인 순,
/// 묶음마다 정차 덮어쓰기(x·y·방향)·리프트를 넣는다. 원래 영역이 연결된 시나리오에는 새 영역도 연결한다(부분 검사 대상이 줄지 않게).
/// 진행 중 run 의 작업 큐는 시작 시 스냅샷이라 영향이 없지만, 혼동을 막으려고 그 영역이 진행 중 run 에 있으면 거부한다.
/// </summary>
public sealed class AreaSplitService(AcsDbContext db)
{
    public async Task<AreaSplitResult> ApplyAsync(Guid areaId, ApplyAreaSplitRequest req, CancellationToken ct = default)
    {
        var area = await db.InspectionAreas.Include(a => a.Tasks).FirstOrDefaultAsync(a => a.AreaId == areaId, ct)
                   ?? throw new AreaSplitException(404, $"영역 '{areaId}' 없음");
        var groups = (req.Groups ?? new()).Where(g => g.TaskIds is { Length: > 0 }).ToList();
        if (groups.Count < 2) throw new AreaSplitException(400, "묶음이 2개 이상이어야 나눌 수 있습니다.");
        var all = area.Tasks.Select(t => t.TaskId).ToHashSet();
        var given = groups.SelectMany(g => g.TaskIds).ToList();
        if (given.Count != given.Distinct().Count()) throw new AreaSplitException(400, "한 작업이 두 묶음에 들어 있습니다.");
        if (!all.SetEquals(given)) throw new AreaSplitException(400, "묶음의 작업이 영역의 작업과 정확히 같아야 합니다(빠지거나 다른 영역 작업 포함).");
        foreach (var g in groups)
        {
            if (!double.IsFinite(g.StationX) || !double.IsFinite(g.StationY) || !double.IsFinite(g.StationTheta))
                throw new AreaSplitException(400, "정차 x·y·방향은 유한값이어야 합니다.");
            if (g.StationLiftMm is < 0 or > 1000) throw new AreaSplitException(400, "리프트 높이는 0~1000 mm 범위여야 합니다.");
        }
        var busy = await db.WorkItems.AsNoTracking()
            .Where(w => w.AreaId == areaId && (w.Status == "PENDING" || w.Status == "DISPATCHED"))
            .Join(db.ScenarioRuns.AsNoTracking().Where(r => r.State == "RUNNING" || r.State == "WAITING_FLOOR_TRANSFER"),
                w => w.RunId, r => r.RunId, (w, r) => r.RunId)
            .AnyAsync(ct);
        if (busy) throw new AreaSplitException(409, "이 영역이 진행 중인 run 에 남아 있습니다 — run 이 끝난 뒤 나누세요.");

        var wall = await db.Walls.AsNoTracking().FirstOrDefaultAsync(w => w.TankId == area.TankId && w.WallCode == area.WallCode, ct)
                   ?? throw new AreaSplitException(404, $"면이 없습니다: {area.TankId}/{area.WallCode}.");
        var geom = await db.TankGeometries.AsNoTracking().FirstOrDefaultAsync(x => x.TankId == area.TankId, ct)
                   ?? throw new AreaSplitException(400, $"선창 지오메트리가 없습니다: {area.TankId}.");

        var byId = area.Tasks.ToDictionary(t => t.TaskId);
        // 원래 영역에 남길 묶음 = 작업이 가장 많은 묶음(같으면 앞).
        var keepIdx = groups.Select((g, i) => (g.TaskIds.Length, i)).OrderByDescending(x => x.Length).ThenBy(x => x.i).First().i;
        var names = await db.InspectionAreas.AsNoTracking()
            .Where(a => a.TankId == area.TankId && a.WallCode == area.WallCode).Select(a => a.Name).ToListAsync(ct);
        var taken = names.ToHashSet();
        int maxSort = await db.InspectionAreas.AsNoTracking()
            .Where(a => a.TankId == area.TankId && a.WallCode == area.WallCode).MaxAsync(a => (int?)a.SortOrder, ct) ?? 0;

        var result = new List<AreaSplitResultArea>();
        var created = new List<InspectionAreaEntity>();
        var suffix = 2;
        for (var gi = 0; gi < groups.Count; gi++)
        {
            var g = groups[gi];
            var tasks = g.TaskIds.Select(id => byId[id]).ToList();
            var corners = AreaRules.AutoCornersForTasks(tasks, wall, geom, area.Level).ToList();
            if (AreaRules.ValidateCorners(corners, area.WallCode, wall.ULen, wall.VLen, out var bb) is { } cv)
                throw new AreaSplitException(400, $"묶음 {gi + 1} 사각형: {cv}");
            var level = AreaRules.DeriveLevel(geom, wall, bb.MinV, bb.MaxV, out var reason)
                        ?? throw new AreaSplitException(400, $"묶음 {gi + 1} 층 유도 실패: {reason}");
            var cornersJson = JsonSerializer.Serialize(corners);
            foreach (var t in tasks)
                if (AreaTaskRules.Validate(t.SeamType, cornersJson, t.StartU, t.StartV, t.EndU, t.EndV) is { } tv)
                    throw new AreaSplitException(400, $"묶음 {gi + 1}: {tv}");

            InspectionAreaEntity target;
            if (gi == keepIdx)
            {
                target = area;
            }
            else
            {
                var name = string.IsNullOrWhiteSpace(g.Name) ? null : g.Name.Trim();
                if (name is not null && taken.Contains(name))
                    throw new AreaSplitException(409, $"면 {area.WallCode} 내에 영역 '{name}'이(가) 이미 있습니다.");
                while (name is null || taken.Contains(name)) name = $"{area.Name}-{suffix++}";
                taken.Add(name);
                target = new InspectionAreaEntity
                {
                    AreaId = Guid.NewGuid(), TankId = area.TankId, WallCode = area.WallCode, Name = name,
                    StationStandoffM = area.StationStandoffM, StationMaxBackwardMm = area.StationMaxBackwardMm,
                    SortOrder = ++maxSort, CreatedBy = req.UserId,
                };
                db.InspectionAreas.Add(target);
                created.Add(target);
            }
            target.Level = level;
            target.Corners = cornersJson;
            (target.UMin, target.VMin, target.UMax, target.VMax) = (bb.MinU, bb.MinV, bb.MaxU, bb.MaxV);
            target.StationX = g.StationX; target.StationY = g.StationY; target.StationTheta = g.StationTheta;
            target.StationLiftMm = g.StationLiftMm;
            var ordered = AreaRules.OrderByLine(tasks);
            for (var i = 0; i < ordered.Count; i++)
            {
                // 영역 안 (area_id, seq) 유일 — 옮기기 전 음수로 비켜 두고 아래에서 확정한다.
                ordered[i].AreaId = target.AreaId;
                ordered[i].Name = AreaRules.RenameForSeq(ordered[i], i + 1);
                ordered[i].Seq = -(i + 1);
            }
            result.Add(new AreaSplitResultArea(target.AreaId, target.Name, level, tasks.Count, gi != keepIdx));
        }

        var links = await db.ScenarioAreas.Where(sa => sa.AreaId == areaId).ToListAsync(ct);
        foreach (var link in links)
        {
            var max = await db.ScenarioAreas.Where(sa => sa.ScenarioId == link.ScenarioId).MaxAsync(sa => (int?)sa.SortOrder, ct) ?? 0;
            foreach (var c in created)
                db.ScenarioAreas.Add(new ScenarioAreaEntity { ScenarioId = link.ScenarioId, AreaId = c.AreaId, SortOrder = ++max });
        }
        db.AuditLogs.Add(new AuditLogEntity
        {
            UserId = req.UserId ?? "", Action = "AREA_SPLIT_PLAN_APPLY", Target = areaId.ToString(),
            Detail = JsonSerializer.Serialize(new
            {
                area.Name, area.WallCode,
                groups = groups.Select((g, i) => new { area = result[i].Name, g.TaskIds, g.StationX, g.StationY, g.StationTheta, g.StationLiftMm }),
                scenarios = links.Select(l => l.ScenarioId),
            }),
        });

        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        await db.SaveChangesAsync(ct);
        foreach (var t in byId.Values)
            if (t.Seq < 0) t.Seq = -t.Seq;
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);

        result.Sort((a, b) => a.Created.CompareTo(b.Created));   // 원래 영역 먼저
        return new AreaSplitResult(result);
    }
}
