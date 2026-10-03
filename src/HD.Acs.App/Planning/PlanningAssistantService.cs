using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HD.Acs.App.Services;
using HD.Acs.Core.Planning;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace HD.Acs.App.Planning;

public sealed record ProposeRequest(string? TankId, string Prompt, string? WallCode, int? Level, ChatMessage[]? History);
public sealed record PreviewOpsRequest(string? TankId, PlanOp[] Ops, string? Prompt);
public sealed record ApplyChangeSetRequest(string? UserId);

public sealed record ChangeSetOpDto(
    int Index, int Source, string Kind, bool Ok, string? Error,
    string? WallCode, int? Level, string? AreaName, double[][]? Corners, double[]? Segment, string Summary);

/// <summary>제안(미리보기) 응답. ChangeSetId 는 적용 가능한 변경이 있고 전 연산이 통과했을 때만 발급된다.</summary>
public sealed record ChangeSetDto(
    Guid? ChangeSetId, string TankId, string Reply, string[] Messages, PlanOp[] Ops, ChangeSetOpDto[] Results,
    int Creates, int Updates, int Deletes, int Failed, bool AllOk, bool HasChanges);

public sealed record ApplyResultDto(Guid ChangeSetId, int Applied, int Creates, int Updates, int Deletes);

/// <summary>
/// 계획 자연어 어시스턴트 [ADR-013] — 자연어 → (Ollama) 고수준 연산 JSON → 서버 전개·검증 미리보기 → 운영자 승인 시 적용.
/// LLM은 **변경안만** 만든다. 좌표 계산·검증·DB 반영은 결정적 코드(<see cref="PlanChangeSetEngine"/>)가 하고,
/// 조회 답의 수치도 서버 집계 값만 쓴다(LLM이 숫자를 지어내지 않게).
/// </summary>
public sealed class PlanningAssistantService
{
    public sealed class LlmDisabledException() : Exception("계획 어시스턴트가 비활성입니다 — 서버 appsettings.json 의 Acs:Llm:Enabled 를 true 로 하고 BaseUrl·Model 을 지정하세요.");
    public sealed class LlmReplyException(string message) : Exception(message);
    public sealed class ChangeSetNotFoundException() : Exception("변경안이 없거나 만료되었습니다 — 다시 제안받으세요.");

    private sealed record Stored(Guid Id, string TankId, string Prompt, PlanOp[] Ops, AtomicOp[] Atomics, string Fingerprint, int Creates, int Updates, int Deletes);

    public static readonly string[] OpNames =
    {
        "createArea", "createTask", "updateTask", "deleteTask",
        "gridAreas", "renameAreas", "moveAreas", "deleteAreas", "setSeamType", "shiftTasks", "deleteTasks", "setScenarioAreas", "query",
    };

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly AcsDbContext _db;
    private readonly PlanChangeSetEngine _engine;
    private readonly OllamaClient _llm;
    private readonly IMemoryCache _cache;
    private readonly ILogger<PlanningAssistantService> _log;

    public PlanningAssistantService(AcsDbContext db, PlanChangeSetEngine engine, OllamaClient llm, IMemoryCache cache, ILogger<PlanningAssistantService> log)
    { _db = db; _engine = engine; _llm = llm; _cache = cache; _log = log; }

    // ══════════════════════════════════════════════════════════════

    /// <summary>자연어 명령 → 변경안 미리보기.</summary>
    public async Task<ChangeSetDto> ProposeAsync(ProposeRequest req, CancellationToken ct)
    {
        if (!_llm.Options.Enabled) throw new LlmDisabledException();
        if (string.IsNullOrWhiteSpace(req.Prompt)) throw new LlmReplyException("명령이 비었습니다.");
        var tankId = req.TankId ?? "CT1";
        var context = await BuildContextAsync(tankId, req.WallCode, req.Level, ct);
        var wallCodes = await _db.Walls.AsNoTracking().Where(w => w.TankId == tankId).Select(w => w.WallCode).ToListAsync(ct);

        var messages = new List<ChatMessage> { new("system", SystemPrompt + "\n\n# 현재 상태\n" + context) };
        foreach (var h in (req.History ?? []).TakeLast(_llm.Options.HistoryTurns))
            if (h.Role is "user" or "assistant") messages.Add(h);
        messages.Add(new ChatMessage("user", req.Prompt));

        var schema = ReplySchema(wallCodes);
        AssistantReply? reply = null;
        string? parseError = null;
        for (int attempt = 0; attempt < 2 && reply is null; attempt++)
        {
            var raw = await _llm.ChatJsonAsync(messages, schema, ct);
            try
            {
                reply = JsonSerializer.Deserialize<AssistantReply>(raw, Web);
                if (reply is null) parseError = "빈 응답";
            }
            catch (JsonException ex)
            {
                parseError = ex.Message;
                messages.Add(new ChatMessage("assistant", raw));
                messages.Add(new ChatMessage("user", $"방금 응답이 JSON 형식이 아닙니다({ex.Message}). 스키마대로 다시 답하세요."));
            }
        }
        if (reply is null) throw new LlmReplyException($"LLM 응답을 해석하지 못했습니다: {parseError}");
        _log.LogInformation("계획 어시스턴트 제안: \"{Prompt}\" → 연산 {N}건 [{Ops}]",
            req.Prompt, reply.Ops?.Length ?? 0, string.Join(",", (reply.Ops ?? []).Select(o => o.Op)));
        return await PreviewAndStoreAsync(tankId, reply.Ops ?? [], reply.Reply ?? "", req.Prompt, ct);
    }

    /// <summary>LLM 없이 연산 JSON 을 직접 미리보기(고급 사용자·테스트·스크립트용).</summary>
    public Task<ChangeSetDto> PreviewOpsAsync(PreviewOpsRequest req, CancellationToken ct) =>
        PreviewAndStoreAsync(req.TankId ?? "CT1", req.Ops ?? [], "", req.Prompt ?? "(직접 입력 연산)", ct);

    /// <summary>미리보기한 변경안을 적용 — 전부 또는 전무. 감사로그 PLANNING_ASSISTANT_APPLY.</summary>
    public async Task<ApplyResultDto> ApplyAsync(Guid id, ApplyChangeSetRequest req, CancellationToken ct)
    {
        if (!_cache.TryGetValue<Stored>(Key(id), out var s) || s is null) throw new ChangeSetNotFoundException();
        var audit = new AuditLogEntity
        {
            UserId = req.UserId ?? "", Action = "PLANNING_ASSISTANT_APPLY", Target = s.TankId,
            Detail = JsonSerializer.Serialize(new
            { changeSetId = id, prompt = s.Prompt, ops = s.Ops, creates = s.Creates, updates = s.Updates, deletes = s.Deletes }, Web),
        };
        var (ok, results) = await _engine.ApplyAsync(s.TankId, s.Atomics, s.Fingerprint, audit, ct);
        if (!ok)
            throw new LlmReplyException("적용 중 검증 실패: " + string.Join(" / ", results.Where(r => !r.Ok).Take(3).Select(r => r.Error)));
        _cache.Remove(Key(id));
        _log.LogInformation("계획 변경안 적용 {Id}: 생성 {C}·수정 {U}·삭제 {D} (사용자 {User})", id, s.Creates, s.Updates, s.Deletes, req.UserId);
        return new ApplyResultDto(id, results.Count, s.Creates, s.Updates, s.Deletes);
    }

    private async Task<ChangeSetDto> PreviewAndStoreAsync(string tankId, PlanOp[] ops, string reply, string prompt, CancellationToken ct)
    {
        var p = await _engine.PreviewAsync(tankId, ops, _llm.Options.MaxOps, ct);
        bool hasChanges = p.Ops.Count > 0;
        Guid? id = null;
        if (hasChanges && p.AllOk)
        {
            id = Guid.NewGuid();
            _cache.Set(Key(id.Value), new Stored(id.Value, tankId, prompt, ops, p.Ops.Select(o => o.Op).ToArray(), p.Fingerprint, p.Creates, p.Updates, p.Deletes),
                TimeSpan.FromMinutes(_llm.Options.ChangeSetTtlMin));
        }
        var results = p.Ops.Select((o, i) => new ChangeSetOpDto(i, o.Op.Source, o.Op.Kind, o.Ok, o.Error,
            o.WallCode, o.Level, o.AreaName, o.Corners, o.Segment, o.Summary)).ToArray();
        return new ChangeSetDto(id, tankId, reply, p.Messages.ToArray(), ops, results,
            p.Creates, p.Updates, p.Deletes, p.Failed, p.AllOk, hasChanges);
    }

    private static string Key(Guid id) => "plan-changeset:" + id;

    private sealed record AssistantReply(string? Reply, PlanOp[]? Ops);

    // ══════════════════════════════════════════════════════════════
    // 프롬프트 · 스키마 · 컨텍스트
    // ══════════════════════════════════════════════════════════════

    internal const string SystemPrompt = """
        너는 HD현대중공업 LNG 화물창 용접검사로봇 관제 시스템(HD_ACS)의 **작업계획 편집 어시스턴트**다.
        운영자의 한국어 명령을 계획 변경 연산(JSON)으로 바꾼다. 너는 변경안만 만들고, 서버가 검증·미리보기한 뒤 운영자가 승인해야 반영된다.
        로봇 주행·검사 실행·선창 형상은 다루지 않는다. 그런 요청이면 ops 를 비우고 reply 로 할 수 없다고 답한다.

        ## 용어
        - 면(wallCode): B=바닥, SL=우현 하부경사, PL=좌현 하부경사, SM=우현 수직벽, PM=좌현 수직벽, SU=우현 상부경사, PU=좌현 상부경사, T=천장, F=선수 격벽, A=선미 격벽
        - 층(level): 1부터. L1=바닥층. 영역은 반드시 한 층에 속한다.
        - 영역(area): 면 위 사각형(최대 1.44m×1.44m), 이름은 면 안에서 유일. 작업(task): 영역 안의 용접선 1개(시작·끝점), 영역 내 순번 seq.
        - seamType: LINE(직선)·CROSS3(3갈래 교차)·CROSS4(十자 교차)·CORNER2(2면 코너)·CORNER3(3면 코너)
        - 좌표(m): u = 면 가로(긴 벽면은 선미=0 → 선수 방향), v = 그 층 안에서의 높이/폭(층 도달 구간의 아래 끝=0, **층-로컬**). 계획 화면 입력과 같다.

        ## 연산(op) — 필요한 필드만 채운다
        - query: 조회. 필터(wallCode·level·areaName·namePattern·matchSeamType) + aggregate("count"|"list"). 숫자는 서버가 계산하니 reply 에 숫자를 지어내지 말 것.
        - createArea: wallCode, level, name, corners [[u,v]×4], (standoffM)
        - gridAreas: 면·층을 격자로 영역 생성. wallCode, level, cellU, cellV(기본 1.4), gap, (uFrom,uTo,vFrom,vTo), namePrefix. 기존 영역과 겹치는 칸은 서버가 건너뛴다.
        - renameAreas: 필터 + find/replace 또는 prefix/suffix
        - moveAreas: 필터 + du,dv (영역과 그 작업을 함께 이동)
        - deleteAreas: 필터 (작업도 함께 삭제)
        - createTask: areaName(+wallCode), startU,startV,endU,endV, seamType, (seq, taskName)
        - updateTask / deleteTask: areaName(+wallCode), seq 또는 seqs, 바꿀 필드
        - setSeamType: 필터(+seqs, matchSeamType) + seamType
        - shiftTasks: 필터(+seqs, matchSeamType) + du,dv
        - deleteTasks: 필터(+seqs, matchSeamType)
        - setScenarioAreas: scenarioName, mode("add"|"remove"|"replace"), 필터, (createIfMissing)
        필터: wallCode, level, areaName(정확히), namePattern(glob, 예 "PM-L2-*"). 매크로에는 필터가 하나 이상 있어야 하며 전체 대상이면 namePattern "*".

        ## 규칙
        - 명령이 모호하면(면·층을 모름 등) ops 를 비우고 reply 로 되묻는다. 추측으로 삭제하지 않는다.
        - 여러 단계는 ops 를 순서대로 나열한다(앞 연산 결과 위에서 다음 연산이 실행된다).
        - reply 는 무엇을 하려는지 한국어 한두 문장.

        ## 예
        명령: "좌현 수직벽 2층 영역 몇 개야?" → {"reply":"PM L2 영역 수를 조회합니다.","ops":[{"op":"query","wallCode":"PM","level":2}]}
        명령: "PM 2층 영역 이름 앞에 P2- 붙여" → {"reply":"PM L2 영역 이름에 접두어 P2-를 붙입니다.","ops":[{"op":"renameAreas","wallCode":"PM","level":2,"prefix":"P2-"}]}
        명령: "바닥 1층을 1.4m 격자로 채워" → {"reply":"B L1에 1.4m 격자 영역을 만듭니다.","ops":[{"op":"gridAreas","wallCode":"B","level":1,"cellU":1.4,"cellV":1.4}]}
        명령: "SL 1층 작업 전부 CROSS4로" → {"reply":"SL L1 작업의 seamType을 CROSS4로 바꿉니다.","ops":[{"op":"setSeamType","wallCode":"SL","level":1,"seamType":"CROSS4"}]}
        """;

    internal static JsonNode ReplySchema(IReadOnlyList<string> wallCodes)
    {
        JsonObject Num() => new() { ["type"] = "number" };
        JsonObject Str() => new() { ["type"] = "string" };
        JsonObject Int() => new() { ["type"] = "integer" };
        JsonObject Enum(IEnumerable<string> v) => new() { ["type"] = "string", ["enum"] = new JsonArray(v.Select(x => (JsonNode)x).ToArray()) };
        var walls = wallCodes.Count > 0 ? wallCodes : new[] { "B", "SL", "PL", "SM", "PM", "SU", "PU", "T", "F", "A" };
        var props = new JsonObject
        {
            ["op"] = Enum(OpNames),
            ["wallCode"] = Enum(walls), ["level"] = Int(), ["areaName"] = Str(), ["namePattern"] = Str(),
            ["matchSeamType"] = Enum(AreaTaskRules.SeamTypes), ["seqs"] = new JsonObject { ["type"] = "array", ["items"] = Int() },
            ["name"] = Str(),
            ["corners"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "array", ["items"] = Num() } },
            ["standoffM"] = Num(), ["seq"] = Int(), ["taskName"] = Str(), ["seamType"] = Enum(AreaTaskRules.SeamTypes),
            ["startU"] = Num(), ["startV"] = Num(), ["endU"] = Num(), ["endV"] = Num(),
            ["cellU"] = Num(), ["cellV"] = Num(), ["gap"] = Num(), ["uFrom"] = Num(), ["uTo"] = Num(), ["vFrom"] = Num(), ["vTo"] = Num(),
            ["namePrefix"] = Str(), ["find"] = Str(), ["replace"] = Str(), ["prefix"] = Str(), ["suffix"] = Str(),
            ["du"] = Num(), ["dv"] = Num(),
            ["scenarioName"] = Str(), ["mode"] = Enum(["add", "remove", "replace"]), ["createIfMissing"] = new JsonObject { ["type"] = "boolean" },
            ["aggregate"] = Enum(["count", "list"]),
        };
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["reply"] = Str(),
                ["ops"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = new JsonArray("op") } },
            },
            ["required"] = new JsonArray("reply", "ops"),
        };
    }

    /// <summary>
    /// LLM에 주는 현재 상태 요약 — 영역 수천 건 전체가 아니라 면×층별 개수·이름 예시·치수만(소형 모델 컨텍스트 보호).
    /// </summary>
    internal async Task<string> BuildContextAsync(string tankId, string? selWall, int? selLevel, CancellationToken ct)
    {
        var g = await _db.TankGeometries.AsNoTracking().FirstOrDefaultAsync(x => x.TankId == tankId, ct)
                ?? throw new InvalidOperationException($"선창 지오메트리가 없습니다: {tankId} (파라미터를 먼저 등록하세요).");
        var geom = new TankGeometry(g.LengthL, g.WFloor, g.ThetaLow, g.HLow, g.HWall, g.ThetaUp, g.HUp,
            JsonSerializer.Deserialize<double[]>(g.LevelZ) ?? [], g.OriginOx, g.OriginOy, g.ReachZMin, g.ReachZMax);
        var bands = geom.LevelBandList();
        var walls = await _db.Walls.AsNoTracking().Where(w => w.TankId == tankId).OrderBy(w => w.WallCode).ToListAsync(ct);
        var stats = await _db.InspectionAreas.AsNoTracking().Where(a => a.TankId == tankId)
            .Select(a => new { a.WallCode, a.Level, a.Name, Tasks = a.Tasks.Count }).ToListAsync(ct);
        var scenarios = await _db.Scenarios.AsNoTracking().Where(s => s.TankId == tankId)
            .Select(s => new { s.Name, N = _db.ScenarioAreas.Count(sa => sa.ScenarioId == s.ScenarioId) }).ToListAsync(ct);

        var sb = new StringBuilder();
        sb.AppendLine($"선창 {tankId}: 길이 {g.LengthL:0.###} m, 높이 {geom.Derived().H:0.###} m, 층 {bands.Count}개");
        sb.AppendLine("면(u길이 × 층별 v 범위):");
        foreach (var w in walls)
        {
            var origin = JsonSerializer.Deserialize<double[]>(w.Origin)!;
            var vAxis = JsonSerializer.Deserialize<double[]>(w.VAxis)!;
            var per = bands.Select(b => (b.Level, Vb: LevelBands.ReachableVBand(origin[2], vAxis[2], w.VLen, b)))
                .Where(x => x.Vb is not null).Select(x => $"L{x.Level} v 0~{x.Vb!.Value.VHi - x.Vb.Value.VLo:0.##}");
            sb.AppendLine($"- {w.WallCode}: u 0~{w.ULen:0.##} m; {string.Join(", ", per)}");
        }
        sb.AppendLine(stats.Count == 0 ? "등록된 영역 없음." : $"등록 영역 {stats.Count}개 · 작업 {stats.Sum(s => s.Tasks)}개 (면·층별):");
        foreach (var grp in stats.GroupBy(s => (s.WallCode, s.Level)).OrderBy(x => x.Key.WallCode).ThenBy(x => x.Key.Level))
            sb.AppendLine($"- {grp.Key.WallCode} L{grp.Key.Level}: 영역 {grp.Count()} · 작업 {grp.Sum(x => x.Tasks)} · 이름 예 {string.Join(", ", grp.OrderBy(x => x.Name).Take(3).Select(x => x.Name))}");
        if (scenarios.Count > 0)
            sb.AppendLine("시나리오: " + string.Join(", ", scenarios.Select(s => $"{s.Name}(대상 {(s.N == 0 ? "선창 전체" : s.N + "개")})")));
        if (selWall is not null || selLevel is not null)
            sb.AppendLine($"현재 화면 선택: 면 {selWall ?? "-"}, 층 {(selLevel is int l ? "L" + l : "-")} — 명령에 면·층이 없으면 이 선택을 뜻할 가능성이 높다.");
        return sb.ToString();
    }
}
