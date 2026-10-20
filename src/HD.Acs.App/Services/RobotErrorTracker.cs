using System.Collections.Concurrent;

namespace HD.Acs.App.Services;

/// <summary>
/// 로봇별 활성 errorType 추적 — AMR은 "같은 errorType 최신 1건 유지, 해소 시 제거"로 보고하므로
/// (VDA 사양서 §6.4), state 2초 반복 수신에서 알람이 중복 발화하지 않도록 **신규 등장(edge)만** 골라낸다.
/// 싱글턴 — RobotStateService(스코프)가 매 state마다 호출.
/// </summary>
public sealed class RobotErrorTracker
{
    private readonly ConcurrentDictionary<string, HashSet<string>> _activeByRobot = new();

    /// <summary>현재 state의 errorType 집합으로 갱신하고, 이번에 새로 등장한 유형 목록을 반환.</summary>
    public IReadOnlyList<string> Update(string robotId, IEnumerable<string> currentTypes) =>
        UpdateEdges(robotId, currentTypes).Appeared;

    /// <summary>갱신 + 에지 양쪽 — 새로 등장한 유형과 사라진 유형(장치 HEAVY 알람 해제 기록·배차 재개용).</summary>
    public (IReadOnlyList<string> Appeared, IReadOnlyList<string> Disappeared) UpdateEdges(
        string robotId, IEnumerable<string> currentTypes)
    {
        var current = new HashSet<string>(currentTypes, StringComparer.OrdinalIgnoreCase);
        var previous = _activeByRobot.GetOrAdd(robotId, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        List<string> appeared, disappeared;
        lock (previous)
        {
            appeared = current.Where(t => !previous.Contains(t)).ToList();
            disappeared = previous.Where(t => !current.Contains(t)).ToList();
            previous.Clear();
            foreach (var t in current) previous.Add(t);
        }
        return (appeared, disappeared);
    }

    /// <summary>로봇이 장치 HEAVY 알람(*LinkLost)을 보고 중인가 — 배차 불가 판정 [HD_AMR 장치통신 HEAVY알람 §5].</summary>
    public bool HasDeviceFault(string robotId) => ActiveDeviceFaults(robotId).Count > 0;

    /// <summary>보고 중인 장치 HEAVY 알람 errorType 목록(정렬) — 차단 메시지용.</summary>
    public IReadOnlyList<string> ActiveDeviceFaults(string robotId)
    {
        if (!_activeByRobot.TryGetValue(robotId, out var active)) return Array.Empty<string>();
        lock (active) return active.Where(DeviceFaults.IsLinkLost).OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>로봇이 마지막 state에서 errors를 보고 중인가 — SAIGE 로봇 상태(ERROR) 판정용.</summary>
    public bool HasActiveErrors(string robotId)
    {
        if (!_activeByRobot.TryGetValue(robotId, out var active)) return false;
        lock (active) return active.Count > 0;
    }

    /// <summary>로봇이 마지막 state에서 해당 errorType을 보고 중인가 (예: communicationLostPause — run 중단 시 cancelOrder 판정).</summary>
    public bool HasActiveError(string robotId, string errorType)
    {
        if (!_activeByRobot.TryGetValue(robotId, out var active)) return false;
        lock (active) return active.Contains(errorType);
    }
}
