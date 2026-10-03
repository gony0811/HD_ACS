using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HD.Acs.App.Planning;

/// <summary>
/// 계획 어시스턴트 LLM 설정 [Acs:Llm]. Enabled=false(기본)면 어시스턴트 API가 503을 돌려준다.
/// BaseUrl 하나로 위치를 정한다 — 같은 서버에 설치: http://127.0.0.1:11434 / 별도 GPU PC: http://{IP}:11434
/// (그 PC에서 OLLAMA_HOST=0.0.0.0:11434 로 외부 수신을 열어야 한다). Ollama를 부르는 쪽은 UI가 아니라 이 서버다.
/// </summary>
public sealed class LlmOptions
{
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "http://127.0.0.1:11434";
    public string Model { get; set; } = "qwen2.5:14b-instruct";
    public int TimeoutSec { get; set; } = 120;
    public int MaxOps { get; set; } = 5000;          // 변경안 1건의 원자 연산 상한
    public int HistoryTurns { get; set; } = 6;       // LLM에 넘기는 직전 대화 수
    public int NumCtx { get; set; } = 8192;          // Ollama 컨텍스트 길이
    public int ChangeSetTtlMin { get; set; } = 10;   // 미리보기 보관 시간 — 지나면 다시 제안받아야 한다
}

public sealed record ChatMessage(string Role, string Content);

/// <summary>LLM 연결 상태 스냅샷 — GET /api/integrations/llm.</summary>
public sealed record LlmStatus(
    bool Enabled, string BaseUrl, string Model,
    DateTimeOffset? LastOkAt, string? LastError, DateTimeOffset? LastErrorAt, long TotalRequests,
    bool? Reachable = null, bool? ModelAvailable = null, string[]? InstalledModels = null)
{
    /// <summary>
    /// 계획 어시스턴트 서버 기능 개정 번호 — UI가 "서버가 구빌드"인지 판별한다(UI.Core PlanningAssistantViewModel.RequiredServerRevision 과 맞출 것).
    /// 1=초판, 2=renameAreas name 지원, 3=빈 ops 재요청·opsSummary, 4=copyArea·선택 면 영역 좌표 컨텍스트.
    /// </summary>
    public int AssistantRevision => 4;
}

/// <summary>
/// Ollama /api/chat 최소 클라이언트 — stream=false, format=JSON Schema(structured output), temperature 0.
/// 상태는 싱글턴이 들고(불변 스냅샷 교체), HttpClient는 명명 클라이언트를 매 호출 생성한다(SaigeHealthReporter와 같은 패턴).
/// </summary>
public sealed class OllamaClient
{
    public const string HttpClientName = "ollama";
    private readonly IHttpClientFactory _http;
    private readonly LlmOptions _opt;
    private volatile LlmStatus _status;
    private long _total;

    public OllamaClient(IHttpClientFactory http, LlmOptions opt)
    {
        _http = http; _opt = opt;
        _status = new LlmStatus(opt.Enabled, opt.BaseUrl, opt.Model, null, null, null, 0);
    }

    public LlmOptions Options => _opt;
    public LlmStatus Status => _status;

    /// <summary>JSON 응답 1건. 실패(연결·HTTP 오류)는 예외 — 호출 측이 사용자에게 사유를 보여준다.</summary>
    public async Task<string> ChatJsonAsync(IReadOnlyList<ChatMessage> messages, JsonNode schema, CancellationToken ct)
    {
        Interlocked.Increment(ref _total);
        var body = new JsonObject
        {
            ["model"] = _opt.Model,
            ["stream"] = false,
            ["format"] = schema.DeepClone(),
            ["options"] = new JsonObject { ["temperature"] = 0, ["num_ctx"] = _opt.NumCtx },
            ["messages"] = new JsonArray(messages.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray()),
        };
        try
        {
            // 미리 직렬화해 Content-Length 를 명시 — PostAsJsonAsync 는 chunked 로 나가 중계 프록시·단순 수신기에서 본문이 비는 경우가 있다(SAIGE E2E 교훈).
            using var content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(body.ToJsonString()));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            using var resp = await _http.CreateClient(HttpClientName).PostAsync("/api/chat", content, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"Ollama {(int)resp.StatusCode}: {Trim(text)}");
            var reply = JsonNode.Parse(text)?["message"]?["content"]?.GetValue<string>()
                        ?? throw new HttpRequestException("Ollama 응답에 message.content 가 없습니다.");
            _status = _status with { LastOkAt = DateTimeOffset.UtcNow, TotalRequests = Interlocked.Read(ref _total) };
            return reply;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            var msg = ex is TaskCanceledException && !ct.IsCancellationRequested
                ? $"Ollama 응답 시간 초과({_opt.TimeoutSec}s) — 모델이 너무 크거나 GPU가 없을 수 있습니다."
                : ex.Message;
            _status = _status with { LastError = msg, LastErrorAt = DateTimeOffset.UtcNow, TotalRequests = Interlocked.Read(ref _total) };
            throw new HttpRequestException(msg, ex);
        }
    }

    /// <summary>연결 점검 — /api/tags 로 도달 여부와 설정 모델 설치 여부를 본다(설정 확인용, 5초 제한).</summary>
    public async Task<LlmStatus> ProbeAsync(CancellationToken ct)
    {
        var s = _status;
        if (!_opt.Enabled) return s;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var tags = await _http.CreateClient(HttpClientName).GetFromJsonAsync<JsonNode>("/api/tags", cts.Token);
            var names = tags?["models"]?.AsArray().Select(m => m?["name"]?.GetValue<string>() ?? "").Where(n => n.Length > 0).ToArray() ?? [];
            bool has = names.Any(n => n == _opt.Model || n == _opt.Model + ":latest");
            return s with { Reachable = true, ModelAvailable = has, InstalledModels = names };
        }
        catch (Exception ex)
        {
            return s with { Reachable = false, LastError = s.LastError ?? ex.Message };
        }
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] + "…" : s;
}
