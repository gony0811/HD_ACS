using System.Text.Json;
using System.Text.Json.Serialization;
using HD.Acs.Core.Integration;
using HD.Acs.Data;
using Microsoft.EntityFrameworkCore;

namespace HD.Acs.App.Services;

/// <summary>
/// Run 조회(pull) API [SAIGE 연동 사양서 v2.6 §5.3/§5.5/§5.6] — 조회 전용, 상태를 변경하지 않는다.
/// 대외 규약: 좌표·길이 mm 정수, 층 1-based, 면은 wallId(정수)+wallCode(문자) 쌍, 시각 ISO 8601 UTC.
/// </summary>
public sealed class RunQueryService
{
    public static readonly string[] RunStates = { "RUNNING", "WAITING_FLOOR_TRANSFER", "COMPLETED", "ABORTED" };
    public static readonly string[] TaskStatuses = { TaskOutcome.Success, TaskOutcome.Failed, TaskOutcome.Skipped };

    private readonly AcsDbContext _db;
    public RunQueryService(AcsDbContext db) => _db = db;

    /// <summary>Run 목록 — 최근 시작 순 [§5.3]. 조건에 맞는 Run이 없으면 빈 목록.</summary>
    public async Task<IReadOnlyList<RunSummary>> ListRunsAsync(string? status, string? tankId, int limit, CancellationToken ct = default)
    {
        var q = from r in _db.ScenarioRuns.AsNoTracking()
                join s0 in _db.Scenarios.AsNoTracking() on r.ScenarioId equals s0.ScenarioId into sg
                from s in sg.DefaultIfEmpty()
                select new { r, ScenarioName = s != null ? s.Name : null, TankId = s != null ? s.TankId : null };
        if (status is not null) q = q.Where(x => x.r.State == status);
        if (tankId is not null) q = q.Where(x => x.TankId == tankId);

        var rows = await q.OrderByDescending(x => x.r.StartedAt).Take(limit).ToListAsync(ct);
        return rows.Select(x => new RunSummary(x.r.RunId, x.r.ScenarioId, x.ScenarioName, x.TankId,
            x.r.RobotId, x.r.State, Utc(x.r.StartedAt), Utc(x.r.EndedAt))).ToList();
    }

    /// <summary>
    /// Run 상세 — 상태 + 층별 미션 [§5.5]. 운영 UI(ScenarioRunDto/MissionDto)도 같은 엔드포인트를 쓰므로
    /// 사양 필드(tankId, missions[].level)에 기존 필드를 더한 상위집합으로 반환한다.
    /// </summary>
    public async Task<RunDetail?> GetRunAsync(Guid runId, CancellationToken ct = default)
    {
        var run = await _db.ScenarioRuns.AsNoTracking().Include(r => r.Missions)
            .FirstOrDefaultAsync(r => r.RunId == runId, ct);
        if (run is null) return null;
        var tankId = await _db.Scenarios.AsNoTracking().Where(s => s.ScenarioId == run.ScenarioId)
            .Select(s => s.TankId).FirstOrDefaultAsync(ct);

        return new RunDetail(run.RunId, run.ScenarioId, run.ScenarioVer, tankId, run.RobotId, run.State,
            Utc(run.StartedAt), Utc(run.EndedAt),
            run.Missions.OrderBy(m => m.Seq).Select(m => new RunMission(
                m.MissionId, m.RunId, m.Seq, SaigeUnits.LevelFromMapId(m.MapId), m.MapId, m.RobotId,
                m.OrderId, m.OrderUpdateId, m.State, Utc(m.StartedAt), Utc(m.EndedAt))).ToList());
    }

    /// <summary>
    /// 종결 TASK별 최종 결과 [§5.6] — 재시도가 있었어도 taskId당 1건. Run이 없으면 null.
    /// position은 촬영 지점·로봇 위치가 아니라 그 TASK가 담당하는 용접선 구간(면-로컬, mm)이다.
    /// </summary>
    public async Task<RunResults?> GetResultsAsync(Guid runId, string? status, int limit, int offset, CancellationToken ct = default)
    {
        if (!await _db.ScenarioRuns.AsNoTracking().AnyAsync(r => r.RunId == runId, ct)) return null;

        var workItems = await _db.WorkItems.AsNoTracking().Where(w => w.RunId == runId)
            .Select(w => new { w.WorkItemId, w.AreaId, w.Status }).ToListAsync(ct);
        var wiIds = workItems.Select(w => w.WorkItemId).ToList();
        var wiById = workItems.ToDictionary(w => w.WorkItemId);

        var actions = await _db.OrderActions.AsNoTracking()
            .Where(a => a.TaskId != null && a.WorkItemId != null && wiIds.Contains(a.WorkItemId.Value))
            .Select(a => new { TaskId = a.TaskId!.Value, WorkItemId = a.WorkItemId!.Value, a.Status, a.Result, a.CreatedAt })
            .ToListAsync(ct);

        var taskIds = actions.Select(a => a.TaskId).Distinct().ToList();
        var tasks = await _db.AreaTasks.AsNoTracking().Where(t => taskIds.Contains(t.TaskId))
            .ToDictionaryAsync(t => t.TaskId, ct);
        var areaIds = workItems.Select(w => w.AreaId).Distinct().ToList();
        var areas = await _db.InspectionAreas.AsNoTracking().Where(a => areaIds.Contains(a.AreaId))
            .Select(a => new { a.AreaId, a.Name, a.WallCode, a.Level }).ToDictionaryAsync(a => a.AreaId, ct);
        // 종결 시각 = 로봇 보고 기록 시각(hist.inspection_result). Order 거부처럼 보고 없이 종결된 시도는 발행 시각으로 폴백.
        var occurred = (await _db.InspectionResults.AsNoTracking()
                .Where(r => r.RunId == runId && r.TaskId != null)
                .Select(r => new { TaskId = r.TaskId!.Value, r.OccurredAt }).ToListAsync(ct))
            .GroupBy(r => r.TaskId).ToDictionary(g => g.Key, g => g.Max(r => r.OccurredAt));

        var items = new List<TaskResult>();
        foreach (var g in actions.GroupBy(a => a.TaskId))
        {
            var attempts = g.OrderBy(a => a.CreatedAt).ToList();
            var wi = wiById[attempts[^1].WorkItemId];
            var outcome = TaskOutcome.Classify(attempts.Select(a => a.Status), wi.Status);
            if (outcome is null) continue;   // 미종결(수행 중·재시도 대기) — 결과 이력에 싣지 않음

            areas.TryGetValue(wi.AreaId, out var area);
            tasks.TryGetValue(g.Key, out var task);
            var decisive = outcome == TaskOutcome.Success
                ? attempts.Last(a => a.Status == "FINISHED") : attempts[^1];
            int wallId = WallIds.FromCode(area?.WallCode);

            items.Add(new TaskResult(
                TaskId: g.Key, AreaId: wi.AreaId, AreaName: area?.Name, WallId: wallId, WallCode: area?.WallCode,
                Level: area?.Level, Status: outcome,
                Attempts: attempts.Count(a => a.Status is "FINISHED" or "FAILED"),
                OccurredAt: (occurred.TryGetValue(g.Key, out var at) ? at : decisive.CreatedAt).ToUniversalTime(),
                Description: ResultDescription(decisive.Result),
                Position: task is null ? null : new TaskSeamPosition(
                    wallId,
                    new UvMm(SaigeUnits.ToMm(task.StartU), SaigeUnits.ToMm(task.StartV)),
                    new UvMm(SaigeUnits.ToMm(task.EndU), SaigeUnits.ToMm(task.EndV)),
                    // 면-로컬 (u,v)는 정규직교 프레임이라 평면 거리가 곧 3D 용접선 길이
                    SaigeUnits.ToMm(Math.Sqrt(Math.Pow(task.EndU - task.StartU, 2) + Math.Pow(task.EndV - task.StartV, 2))),
                    task.SeamType)));
        }

        var filtered = (status is null ? items : items.Where(i => i.Status == status))
            .OrderBy(i => i.OccurredAt).ThenBy(i => i.TaskId).ToList();
        return new RunResults(runId, filtered.Count, filtered.Skip(offset).Take(limit).ToList());
    }

    /// <summary>order_action.result jsonb({ActionStatus, ResultDescription})에서 로봇 보고 사유만 추출.</summary>
    private static string? ResultDescription(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            return doc.RootElement.TryGetProperty("ResultDescription", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static DateTimeOffset? Utc(DateTimeOffset? t) => t?.ToUniversalTime();
}

public sealed record RunSummary(Guid RunId, Guid ScenarioId, string? ScenarioName, string? TankId,
    string RobotId, string State, [property: JsonConverter(typeof(UtcZConverter))] DateTimeOffset? StartedAt, [property: JsonConverter(typeof(UtcZConverter))] DateTimeOffset? EndedAt);

public sealed record RunDetail(Guid RunId, Guid ScenarioId, int ScenarioVer, string? TankId, string RobotId, string State,
    [property: JsonConverter(typeof(UtcZConverter))] DateTimeOffset? StartedAt, [property: JsonConverter(typeof(UtcZConverter))] DateTimeOffset? EndedAt, IReadOnlyList<RunMission> Missions);

public sealed record RunMission(Guid MissionId, Guid RunId, int Seq, int? Level, string MapId, string RobotId,
    string OrderId, int OrderUpdateId, string State, [property: JsonConverter(typeof(UtcZConverter))] DateTimeOffset? StartedAt, [property: JsonConverter(typeof(UtcZConverter))] DateTimeOffset? EndedAt);

public sealed record RunResults(Guid RunId, int Total, IReadOnlyList<TaskResult> Items);

public sealed record TaskResult(Guid TaskId, Guid AreaId, string? AreaName, int WallId, string? WallCode, int? Level,
    string Status, int Attempts, [property: JsonConverter(typeof(UtcZConverter))] DateTimeOffset OccurredAt, string? Description, TaskSeamPosition? Position);

public sealed record TaskSeamPosition(int WallId, UvMm SeamStart, UvMm SeamEnd, int SeamLength, string SeamType);

public sealed record UvMm(int U, int V);

/// <summary>시각 직렬화 — ISO 8601 UTC "Z" 표기 [§5.1] (기본 직렬화는 "+00:00"). 읽기는 표준 파서.</summary>
public sealed class UtcZConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetDateTimeOffset();

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFF'Z'", System.Globalization.CultureInfo.InvariantCulture));
}
