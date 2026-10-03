using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.Acs.UI.Models;
using HD.Acs.UI.Services;

namespace HD.Acs.UI.ViewModels;

/// <summary>대화 1줄. IsUser=운영자 명령, 아니면 어시스턴트·시스템 응답.</summary>
public sealed record PlanChatLine(bool IsUser, string Text);

/// <summary>
/// 계획 자연어 어시스턴트 패널 [ADR-013] — 명령 → 서버 변경안 미리보기(전개도 점선) → [적용]으로 반영.
/// LLM은 변경안만 만들고 DB는 운영자가 [적용]을 눌러야 바뀐다(전부 또는 전무).
/// 현재 화면의 면·층 선택을 함께 보내 "이 면", "여기" 같은 명령을 해석하게 한다.
/// </summary>
public sealed partial class PlanningAssistantViewModel : ObservableObject
{
    private const int MaxListedOps = 300;   // 패널 목록 표시 상한(실패 우선) — 전체 건수는 요약에 표시
    private readonly IAcsApiClient _api;
    private readonly string _operatorId;
    private readonly AreaPlanningViewModel _planning;

    public PlanningAssistantViewModel(IAcsApiClient api, string operatorId, AreaPlanningViewModel planning)
    { _api = api; _operatorId = operatorId; _planning = planning; }

    public ObservableCollection<PlanChatLine> Lines { get; } = new();
    /// <summary>변경안 연산 목록(실패 먼저, 최대 300건).</summary>
    public ObservableCollection<PlanChangeOpDto> ProposalOps { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _input = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(ApplyCommand), nameof(DiscardCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(DiscardCommand))]
    [NotifyPropertyChangedFor(nameof(HasProposal))]
    private PlanChangeSetDto? _proposal;

    [ObservableProperty] private string? _proposalSummary;
    [ObservableProperty] private string _statusText = "명령 예: \"PM 2층 영역 이름 앞에 P2- 붙여\" · \"바닥 1층을 1.4m 격자로 채워\" · \"SL 1층 작업 몇 개?\"";

    public bool HasProposal => Proposal is { HasChanges: true };

    /// <summary>패널 표시 시 서버 LLM 설정 상태를 확인해 안내한다(비활성·모델 미설치 등).</summary>
    [RelayCommand]
    public async Task CheckStatusAsync()
    {
        try
        {
            var s = await _api.GetLlmStatusAsync();
            StatusText = s switch
            {
                null => "서버가 계획 어시스턴트를 지원하지 않습니다(서버 버전 확인).",
                { Enabled: false } => "계획 어시스턴트 비활성 — 서버 appsettings.json 의 Acs:Llm:Enabled=true, BaseUrl(Ollama 주소)·Model 을 설정하세요.",
                { Reachable: false } => $"Ollama 연결 불가({s.BaseUrl}) — 주소·방화벽·OLLAMA_HOST 를 확인하세요.",
                { ModelAvailable: false } => $"Ollama 에 모델 '{s.Model}'이(가) 없습니다 — ollama pull {s.Model}",
                _ => $"연결됨 — {s.Model} @ {s.BaseUrl}",
            };
        }
        catch (Exception ex) { StatusText = $"상태 조회 실패: {ex.Message}"; }
    }

    private bool CanSend() => !IsBusy && !string.IsNullOrWhiteSpace(Input);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var prompt = Input.Trim();
        var history = Lines.TakeLast(6).Select(l => new PlanChatMessageDto(l.IsUser ? "user" : "assistant", l.Text)).ToArray();
        Lines.Add(new PlanChatLine(true, prompt));
        Input = "";
        IsBusy = true;
        ClearProposal();
        StatusText = "LLM 이 변경안을 만드는 중… (모델·GPU에 따라 수십 초)";
        try
        {
            var cs = await _api.ProposePlanAsync(new PlanProposeRequestDto(
                _planning.TankId, prompt, _planning.SelectedWall?.WallCode, _planning.SelectedLevel?.Level, history));
            var parts = new List<string?> { cs.Reply };
            if (cs.OpsSummary is { Length: > 0 } ops)
                parts.Add("해석: " + string.Join("; ", ops.Take(5)) + (ops.Length > 5 ? $" … 외 {ops.Length - 5}건" : ""));
            parts.AddRange(cs.Messages);
            // 답장은 "변경합니다"인데 연산이 0건이면 아무 일도 안 일어난다 — 조용히 넘기지 않고 알린다.
            if (!cs.HasChanges && cs.OpsSummary is { Length: 0 })
                parts.Add("※ 실행할 변경을 만들지 못했습니다 — 표현을 바꿔 다시 요청하세요 (예: 'F-SM-A0001 영역 이름을 F-A0001로 바꿔').");
            var text = string.Join("\n", parts.Where(s => !string.IsNullOrWhiteSpace(s)));
            Lines.Add(new PlanChatLine(false, text.Length > 0 ? text : "(응답 없음)"));
            ShowProposal(cs);
        }
        catch (Exception ex)
        {
            Lines.Add(new PlanChatLine(false, $"오류: {ex.Message}"));
            StatusText = "변경안 없음";
        }
        finally { IsBusy = false; }
    }

    private bool CanApply() => !IsBusy && Proposal?.ChangeSetId is not null;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        if (Proposal?.ChangeSetId is not Guid id) return;
        IsBusy = true;
        try
        {
            var r = await _api.ApplyPlanChangeSetAsync(id, _operatorId);
            Lines.Add(new PlanChatLine(false, $"적용 완료 — 생성 {r.Creates} · 수정 {r.Updates} · 삭제 {r.Deletes}"));
            ClearProposal();
            StatusText = "적용됨";
            await _planning.LoadAsync();   // 면·영역·작업 새로고침 + 3D 오버레이 동기화(PlanningChanged)
        }
        catch (Exception ex)
        {
            Lines.Add(new PlanChatLine(false, $"적용 실패: {ex.Message}"));
            StatusText = "적용 실패 — 다시 제안받으세요.";
        }
        finally { IsBusy = false; }
    }

    private bool CanDiscard() => !IsBusy && Proposal is not null;

    [RelayCommand(CanExecute = nameof(CanDiscard))]
    private void Discard()
    {
        ClearProposal();
        StatusText = "변경안을 버렸습니다.";
    }

    private void ShowProposal(PlanChangeSetDto cs)
    {
        Proposal = cs;
        foreach (var op in cs.Results.OrderBy(o => o.Ok).ThenBy(o => o.Index).Take(MaxListedOps)) ProposalOps.Add(op);
        if (!cs.HasChanges)
        {
            ProposalSummary = null;
            StatusText = "변경 없음(조회·질의 응답)";
            return;
        }
        var more = cs.Results.Count > MaxListedOps ? $" (목록은 {MaxListedOps}건만 표시)" : "";
        ProposalSummary = $"변경안: 생성 {cs.Creates} · 수정 {cs.Updates} · 삭제 {cs.Deletes}"
                          + (cs.Failed > 0 ? $" · 실패 {cs.Failed}" : "") + more;
        StatusText = cs.ChangeSetId is not null
            ? "전개도 점선(청록=생성·수정, 빨강=삭제)으로 확인 후 [적용]을 누르세요. 다른 면은 면 선택을 바꿔 확인."
            : "검증에 실패한 연산이 있어 적용할 수 없습니다 — 명령을 고쳐 다시 요청하세요.";
        _planning.SetProposal(cs.Results);
    }

    private void ClearProposal()
    {
        Proposal = null;
        ProposalSummary = null;
        ProposalOps.Clear();
        _planning.SetProposal(null);
    }
}
