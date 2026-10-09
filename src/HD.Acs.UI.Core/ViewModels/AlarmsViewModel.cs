using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HD.Acs.UI.Models;
using HD.Acs.UI.Services;

namespace HD.Acs.UI.ViewModels;

/// <summary>
/// 운영 화면 우측 "알람 · 이벤트" 패널 — 작업자가 지금 무슨 일이 일어나고 있는지 한눈에 알도록
/// ① 현재 상태 카드(단계 헤드라인·안내·TASK 진행바·로봇 요약·최근 실패 사유)와
/// ② 시간순 이벤트 로그(배차·검사 시작/성공/실패·재시도·스킵·run 완료/중단·로봇 모드/오류/통신 변화·명령 결과)를 제공한다.
/// 소스는 SignalR 푸시와 MissionViewModel(현재 run·작업 큐)이며, 별도 REST 조회는 하지 않는다.
/// </summary>
public sealed partial class AlarmsViewModel : ObservableObject
{
    /// <summary>이벤트 로그 보관 상한 — 오래된 것부터 버린다.</summary>
    public const int MaxEvents = 300;

    public MissionViewModel Mission { get; }

    /// <summary>최신이 위(index 0)인 이벤트 로그.</summary>
    public ObservableCollection<OperationEvent> Events { get; } = new();
    public bool HasEvents => Events.Count > 0;

    /// <summary>현재 상태 종류: run(진행)·done(완료)·wait(대기)·warn(주의)·fail(오류) — 색 지정용.</summary>
    [ObservableProperty] private string _statusKind = "wait";
    [ObservableProperty] private string _statusHeadline = "대기 — 실행 중인 작업 없음";
    [ObservableProperty] private string _statusDetail = "시나리오와 로봇을 선택하고 '미션 시작'을 누르세요.";
    [ObservableProperty] private string _robotSummary = "로봇 상태 수신 대기";
    /// <summary>현재 run의 가장 최근 실패 사유(없으면 null) — 카드에 빨간 글씨로 상시 표시.</summary>
    [ObservableProperty] private string? _lastFailure;

    private readonly Dictionary<string, RobotStateDto> _lastState = new();
    private readonly Dictionary<string, string> _connection = new();
    private Guid? _runId;

    public AlarmsViewModel(IMonitoringClient monitoring, MissionViewModel mission)
    {
        Mission = mission;
        Events.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasEvents));

        monitoring.RobotStateReceived += OnRobotState;
        monitoring.RobotConnectionReceived += OnRobotConnection;
        monitoring.RunStateReceived += OnRunState;
        monitoring.WorkItemProgressReceived += OnWorkItemProgress;
        monitoring.TaskActionProgressReceived += OnTaskActionProgress;
        monitoring.AlarmRaised += OnAlarmRaised;
        monitoring.BatterySwapDispatched += OnBatterySwapDispatched;
        monitoring.StatusChanged += OnHubStatus;

        mission.PropertyChanged += OnMissionPropertyChanged;
        mission.WorkItemsChanged += (_, _) => Recompute();
    }

    // ── 푸시 핸들러 ──────────────────────────────────────────────

    private void OnRobotState(object? sender, RobotStateDto s)
    {
        _lastState.TryGetValue(s.RobotId, out var prev);
        _lastState[s.RobotId] = s;
        var errors = s.ErrorDescriptions ?? Array.Empty<string>();

        if (prev is null)
        {
            // 첫 수신: 이미 작업을 막는 상태면 바로 알린다
            if (s.OperatingMode == "MANUAL") Add("warn", $"{s.RobotId} 수동(MANUAL) 모드 — 자동 작업 불가");
            if (IsEStop(s)) Add("fail", $"{s.RobotId} 비상정지 상태");
            foreach (var e in errors) Add(ClassifyErrorKind(e), $"{s.RobotId} 오류 보고: {e}");
        }
        else
        {
            if (s.OperatingMode is not null && prev.OperatingMode is not null && s.OperatingMode != prev.OperatingMode)
                Add(s.OperatingMode == "AUTOMATIC" ? "info" : "warn",
                    $"{s.RobotId} 운전 모드 {ModeText(prev.OperatingMode)} → {ModeText(s.OperatingMode)}");
            if (IsEStop(s) != IsEStop(prev))
                Add(IsEStop(s) ? "fail" : "info", IsEStop(s) ? $"{s.RobotId} 비상정지 발생" : $"{s.RobotId} 비상정지 해제");
            if (s.Driving != prev.Driving)
                Add("info", s.Driving ? $"{s.RobotId} 주행 시작" : $"{s.RobotId} 정지");

            var before = prev.ErrorDescriptions ?? Array.Empty<string>();
            foreach (var e in errors.Except(before))
            {
                // 배터리 관련은 kind를 세분 — critical=fail, 거부/저전력=warn 유지(사양서 §8: 거부는 WARNING, FATAL 아님)
                var kind = ClassifyErrorKind(e);
                Add(kind, $"{s.RobotId} 오류 보고: {e}");
            }
            if (errors.Length == 0 && before.Length > 0) Add("info", $"{s.RobotId} 오류 해제");
        }
        Recompute();
    }

    private void OnRobotConnection(object? sender, RobotConnectionDto c)
    {
        _connection.TryGetValue(c.RobotId, out var prev);
        _connection[c.RobotId] = c.ConnectionState;
        if (prev != c.ConnectionState)
            Add(c.ConnectionState == "ONLINE" ? "info" : "fail",
                c.ConnectionState == "ONLINE" ? $"{c.RobotId} 통신 연결됨" : $"{c.RobotId} 통신 두절 ({c.ConnectionState})");
        Recompute();
    }

    private void OnHubStatus(object? sender, HubStatus st)
    {
        if (st is HubStatus.Reconnecting or HubStatus.Disconnected or HubStatus.Failed)
            Add("fail", "관제 서버 실시간 연결 끊김 — 화면 정보가 갱신되지 않을 수 있습니다");
        else if (st == HubStatus.Connected && Events.Count > 0)
            Add("info", "관제 서버 실시간 연결 복구");
    }

    private void OnRunState(object? sender, RunStateDto p)
    {
        switch (p.State)
        {
            case "RUNNING":
                Add("run", "작업 진행 시작");
                break;
            case "WAITING_FLOOR_TRANSFER":
                Add("warn", "현재 층 작업 완료 — 다음 층으로 이동 대기");
                break;
            case "COMPLETED":
                // 건수는 직후 RunProgress 푸시로 확정되므로 카드에서 표시 — 여기서는 스킵 발생 여부만
                bool notOk = Mission.WorkItems.Any(w => w.Status is "SKIPPED" or "FAILED");
                Add(notOk ? "warn" : "done", notOk ? "작업 완료 — 실패한 작업 있음 (알람 확인)" : "작업 완료");
                break;
            case "ABORTED":
                Add("warn", "작업 중단됨 — '이어하기'로 남은 작업 재개 가능");
                break;
        }
        Recompute();
    }

    private void OnWorkItemProgress(object? sender, WorkItemProgressDto p)
    {
        if (!IsCurrentRun(p.RunId)) return;
        var area = AreaName(p.WorkItemId);
        var reason = string.IsNullOrWhiteSpace(p.Reason) ? "" : $" — 사유: {p.Reason}";
        switch (p.Status)
        {
            case "DISPATCHED":
                Add("run", p.Attempts > 0
                    ? $"{area} 재시도 지시 ({p.Attempts + 1}회차) — 정차점으로 이동"
                    : $"{area} 작업 지시 — 정차점으로 이동");
                break;
            case "DONE":
                Add("done", $"{area} 검사 완료");
                break;
            case "PENDING" when p.Attempts > 0:
                Add("warn", $"{area} 실패 ({p.Attempts}회) — 재시도 예정{reason}");
                if (!string.IsNullOrWhiteSpace(p.Reason)) LastFailure = $"{area}: {p.Reason}";
                break;
            case "FAILED":
                Add("fail", $"{area} 실패 — 자동 재시도 안 함{reason}");
                if (!string.IsNullOrWhiteSpace(p.Reason)) LastFailure = $"{area}: {p.Reason}";
                break;
            case "SKIPPED":
                Add("fail", $"{area} 건너뜀 ({p.Attempts}회 실패){reason}");
                if (!string.IsNullOrWhiteSpace(p.Reason)) LastFailure = $"{area}: {p.Reason}";
                break;
        }
        Recompute();
    }

    private void OnTaskActionProgress(object? sender, TaskActionProgressDto p)
    {
        if (!IsCurrentRun(p.RunId)) return;
        var task = TaskName(p.WorkItemId, p.TaskId);
        switch (p.Status)
        {
            case "RUNNING":
                Add("run", $"{task} 검사 시작");
                break;
            case "FINISHED":
                Add("done", $"{task} 검사 성공");
                break;
            case "FAILED":
                var why = string.IsNullOrWhiteSpace(p.ResultDescription) ? "사유 미제공" : p.ResultDescription;
                Add("fail", $"{task} 실패 — {why}");
                LastFailure = $"{task}: {why}";
                break;
        }
        Recompute();
    }

    private void OnAlarmRaised(object? sender, AlarmDto a)
    {
        var kind = a.Severity?.ToUpperInvariant() switch
        {
            "CRITICAL" or "FATAL" => "fail",
            "INFO" => "info",
            _ => "warn",
        };
        Add(kind, $"알람 — {a.Title ?? a.AlarmCode}");
    }

    private void OnBatterySwapDispatched(object? sender, BatterySwapDispatchedDto p)
    {
        var abortText = p.AbortedRunId is Guid r ? $", run {r.ToString()[..8]} 중단" : "";
        Add("info", $"{p.RobotId} 배터리 교체 이동 발행 → {p.MapId}/{p.TargetNodeId} ({p.Reason}){abortText}");
        Recompute();
    }

    /// <summary>errorDescriptions 항목(예: "batteryCritical: SoC 8%")의 kind 분류.
    /// 사양서 §8: orderRejectedBatteryLow는 WARNING(실패 아님). batteryCritical는 FATAL.</summary>
    public static string ClassifyErrorKind(string errorText)
    {
        var head = errorText;
        int colon = errorText.IndexOf(':');
        if (colon > 0) head = errorText[..colon];
        var t = head.Trim();
        if (t.Equals("batteryCritical", StringComparison.OrdinalIgnoreCase)) return "fail";
        if (t.Equals("batteryLow", StringComparison.OrdinalIgnoreCase)) return "warn";
        if (t.Equals("orderRejectedBatteryLow", StringComparison.OrdinalIgnoreCase)) return "warn";
        return "warn";   // 기존 거동 유지(알 수 없는 오류는 경고)
    }

    private void OnMissionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MissionViewModel.StatusMessage):
                // 운영자 명령 결과(시작·재개·중단 성공/실패 사유)도 로그에 남긴다 — 409 등 거부 사유가 묻히지 않게
                if (!string.IsNullOrWhiteSpace(Mission.StatusMessage))
                    Add(Mission.StatusMessage.Contains("실패") ? "fail" : "info", Mission.StatusMessage);
                break;
            case nameof(MissionViewModel.CurrentRun):
                if (Mission.CurrentRun?.RunId != _runId)
                {
                    _runId = Mission.CurrentRun?.RunId;
                    LastFailure = null;   // 새 run — 이전 run의 실패 사유는 이력에서 확인
                }
                Recompute();
                break;
            case nameof(MissionViewModel.TaskProgress):
            case nameof(MissionViewModel.SelectedRobotId):
                Recompute();
                break;
        }
    }

    // ── 현재 상태 계산 ──────────────────────────────────────────

    private void Recompute()
    {
        var robotId = Mission.CurrentRun?.RobotId ?? Mission.SelectedRobotId;
        RobotStateDto? robot = null;
        string? connection = null;
        if (robotId is not null)
        {
            _lastState.TryGetValue(robotId, out robot);
            _connection.TryGetValue(robotId, out connection);
        }

        var current = Mission.WorkItems.FirstOrDefault(w => w.Status == "DISPATCHED");
        var s = Describe(connection, robot, Mission.CurrentRun?.State,
            current?.AreaName, current?.Attempts ?? 0,
            current?.Tasks.Any(t => t.Status == "RUNNING") ?? false,
            Mission.TaskProgress);
        StatusKind = s.Kind;
        StatusHeadline = s.Headline;
        StatusDetail = s.Detail;
        RobotSummary = DescribeRobot(robotId, robot, connection);
    }

    /// <summary>
    /// 현재 단계 판정(순수 함수). 우선순위: 통신 두절 > 비상정지 > run 상태(대기/완료/중단/층 이동) >
    /// 진행 중 세부(수동 모드 > 로봇 오류 > 배차 대기 > 검사 중 > 주행 중 > 이동 준비).
    /// </summary>
    public static StatusSnapshot Describe(string? connection, RobotStateDto? robot, string? runState,
        string? currentArea, int currentAttempts, bool inspecting, RunProgressDto? progress)
    {
        if (connection is "OFFLINE" or "CONNECTIONBROKEN")
            return new("fail", "로봇 통신 두절", "HD_AMR 연결 상태를 확인하세요. 재연결되면 자동으로 이어집니다.");
        if (robot is not null && IsEStop(robot))
            return new("fail", "비상정지 상태",
                "로봇 비상정지를 해제한 뒤 '이어하기'로 남은 작업을 재개하세요.");
        // 배터리 — 비상정지 다음(=작업자가 즉시 조치해야 할 순위). 수동 모드·run 상태 판단보다 앞 [HD_AMR 배터리관리 §8].
        if (robot is not null && HasErrorType(robot, "batteryCritical"))
            return new("fail", "배터리 위험 — AMR 그 자리 안전정지",
                "로봇 위치에서 배터리를 교체(핫스왑)하세요. 교체 완료 후 자동 복귀, '이어하기'로 남은 작업 재개.");
        if (robot is not null && HasErrorType(robot, "batteryLow"))
            return new("warn", "배터리 저전력",
                "로봇을 현재 층 배터리 교체 장소로 보내려면 '배터리 교체 이동' 버튼을 누르세요. 교체 완료 후 '이어하기'로 재개.");

        bool manual = robot?.OperatingMode == "MANUAL";
        switch (runState)
        {
            case null:
                return manual
                    ? new("warn", "대기 — 로봇 수동 모드", "자동 작업을 하려면 로봇을 자동(AUTOMATIC) 모드로 전환하세요.")
                    : new("wait", "대기 — 실행 중인 작업 없음", "시나리오와 로봇을 선택하고 '미션 시작'을 누르세요.");
            case "COMPLETED":
                var failed = progress?.FailedTasks ?? 0;
                if (failed > 0)
                    return new("warn", $"작업 완료 — 실패 {failed}건 포함",
                        $"성공 {progress!.SucceededTasks} · 실패 {failed} / 전체 {progress.TotalTasks}. 실패 사유는 아래 로그·이력에서 확인하세요.");
                return new("done", "작업 완료",
                    progress is null ? "모든 검사 작업이 끝났습니다." : $"검사 {progress.TotalTasks}건 모두 성공");
            case "ABORTED":
                return new("warn", "작업 중단됨", "완료된 작업은 보존됩니다. '이어하기'로 남은 작업을 재개하세요.");
            case "WAITING_FLOOR_TRANSFER":
                return new("warn", "층 이동 대기",
                    "로봇을 다음 층으로 옮긴 뒤 '수동 층 변경' → '다음 층 릴리스'를 누르세요.");
        }

        // 진행 중(RUNNING)
        var attempt = currentAttempts > 0 ? $" · 재시도 {currentAttempts + 1}회차" : "";
        if (manual)
            return new("warn", "로봇 수동 모드 — 작업 진행 불가", "로봇을 자동(AUTOMATIC) 모드로 전환하세요.");
        if (robot?.ErrorDescriptions is { Length: > 0 } errs)
            return new("warn", "로봇 오류 보고 중", string.Join(" / ", errs));
        if (currentArea is null)
            return new("run", "다음 작업 배차 대기", "로봇 위치·층 확인 후 다음 작업을 지시합니다.");
        if (inspecting)
            return new("run", $"검사 중 — {currentArea}", $"협동로봇·카메라 검사 진행{attempt}");
        if (robot?.Driving == true)
            return new("run", $"주행 중 → {currentArea}", $"정차점으로 이동{attempt}");
        return new("run", $"이동 준비 → {currentArea}", $"로봇 응답 대기{attempt}");
    }

    private static string DescribeRobot(string? robotId, RobotStateDto? s, string? connection)
    {
        if (robotId is null) return "로봇 미선택";
        if (connection is "OFFLINE" or "CONNECTIONBROKEN") return $"{robotId} · 통신 두절";
        if (s is null) return $"{robotId} · 상태 수신 대기";
        var parts = new List<string> { robotId };
        if (s.OperatingMode is not null) parts.Add(ModeText(s.OperatingMode));
        parts.Add(s.Driving ? "주행 중" : "정지");
        if (s.BatteryPct is { } b) parts.Add($"배터리 {b:0}%");
        if (s.Errors > 0) parts.Add($"오류 {s.Errors}건");
        return string.Join(" · ", parts);
    }

    private static bool IsEStop(RobotStateDto s) =>
        (s.EStop is not null && s.EStop != "NONE")
        || (s.ErrorDescriptions?.Any(e => e.StartsWith("emergencyStop", StringComparison.OrdinalIgnoreCase)) ?? false);

    /// <summary>errorDescriptions에 특정 errorType이 활성 중인지(접두 매칭, "errorType: desc" / "errorType" 모두 수용).</summary>
    public static bool HasErrorType(RobotStateDto s, string errorType) =>
        s.ErrorDescriptions?.Any(e => e.StartsWith(errorType + ":", StringComparison.OrdinalIgnoreCase)
                                   || e.Equals(errorType, StringComparison.OrdinalIgnoreCase)) ?? false;

    private static string ModeText(string? mode) => mode switch
    {
        "AUTOMATIC" => "자동",
        "SEMIAUTOMATIC" => "반자동",
        "MANUAL" => "수동",
        "SERVICE" => "서비스",
        "TEACHIN" => "티칭",
        _ => mode ?? "?",
    };

    // ── 보조 ────────────────────────────────────────────────────

    /// <summary>현재 run이 정해져 있으면 그 run의 것만 — 다른 run 노이즈 무시.</summary>
    private bool IsCurrentRun(Guid runId) => Mission.CurrentRun is null || Mission.CurrentRun.RunId == runId;

    private string AreaName(Guid workItemId) =>
        Mission.WorkItems.FirstOrDefault(w => w.WorkItemId == workItemId)?.AreaName is { } n ? $"영역 {n}" : "영역";

    private string TaskName(Guid? workItemId, Guid? taskId)
    {
        var row = Mission.WorkItems.FirstOrDefault(w => w.WorkItemId == workItemId);
        var task = row?.Tasks.FirstOrDefault(t => t.TaskId == taskId);
        return (row, task) switch
        {
            ({ } r, { } t) => $"{r.AreaName} / {t.Name}",
            ({ } r, null) => $"{r.AreaName} 용접선",
            _ => "용접선",
        };
    }

    private void Add(string kind, string text)
    {
        Events.Insert(0, new OperationEvent(DateTimeOffset.Now, kind, text));
        while (Events.Count > MaxEvents) Events.RemoveAt(Events.Count - 1);
    }
}

/// <summary>현재 상태 카드 내용. Kind = run|done|wait|warn|fail.</summary>
public readonly record struct StatusSnapshot(string Kind, string Headline, string Detail);

/// <summary>이벤트 로그 항목. Kind = info|run|done|warn|fail.</summary>
public sealed record OperationEvent(DateTimeOffset Time, string Kind, string Text)
{
    public string TimeText => Time.ToLocalTime().ToString("HH:mm:ss");
}
