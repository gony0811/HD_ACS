using System.Text.Json;
using HD.Acs.Data;
using Microsoft.EntityFrameworkCore;

namespace HD.Acs.App.Services;

/// <summary>용접선(TASK) 한 개의 가장 최근 검사 결과. Status = FINISHED(성공) | FAILED(실패).</summary>
public sealed record TaskLatestResult(Guid TaskId, Guid AreaId, string Status, DateTimeOffset At,
    Guid? RunId, int Attempt, string? Reason);

/// <summary>
/// 선창 전체 누적 검사 기록 — 용접선마다 **가장 최근에 끝난 검사 결과** 한 건(run 무관).
/// 운영 화면이 현재 run과 상관없이 선창 도면을 성공·실패·미검사로 칠하는 데 쓴다.
/// run 안의 진행률 판정(TaskOutcome — 한 번 성공하면 성공)과 다르다: 재검사에서 실패하면 실패로 바뀐다.
/// </summary>
public static class TaskHistoryQuery
{
    /// <summary>
    /// 기록이 없는 용접선(미검사)은 결과에 없다. 시각은 그 액션의 발행 시각(order_action.created_at).
    /// 검사 결과가 아닌 FAILED는 뺀다 — 중단(abort)·비상정지로 끊겨 큐로 되돌아간 정차(work_item PENDING)의 액션.
    /// </summary>
    public static async Task<List<TaskLatestResult>> LatestAsync(AcsDbContext db, string tankId, CancellationToken ct = default)
    {
        var rows = await (
            from a in db.OrderActions.AsNoTracking()
            where a.TaskId != null && (a.Status == "FINISHED" || a.Status == "FAILED")
            join t in db.AreaTasks.AsNoTracking() on a.TaskId equals (Guid?)t.TaskId
            join ar in db.InspectionAreas.AsNoTracking() on t.AreaId equals ar.AreaId
            where ar.TankId == tankId
            join w0 in db.WorkItems.AsNoTracking() on a.WorkItemId equals (Guid?)w0.WorkItemId into wg
            from w in wg.DefaultIfEmpty()
            select new
            {
                TaskId = a.TaskId!.Value, t.AreaId, a.Status, a.CreatedAt, a.Result, a.Attempts,
                WorkStatus = w == null ? null : w.Status, RunId = w == null ? (Guid?)null : w.RunId,
            }).ToListAsync(ct);

        return rows
            .Where(r => !(r.Status == "FAILED" && r.WorkStatus == "PENDING"))
            .GroupBy(r => r.TaskId)
            .Select(g => g.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Attempts).First())
            .Select(r => new TaskLatestResult(r.TaskId, r.AreaId, r.Status, r.CreatedAt, r.RunId, r.Attempts,
                r.Status == "FAILED" ? Reason(r.Result) : null))
            .ToList();
    }

    private static string? Reason(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Name.Equals("resultDescription", StringComparison.OrdinalIgnoreCase))
                    return p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
        }
        catch (JsonException) { }
        return null;
    }
}
