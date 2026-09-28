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
    public IReadOnlyList<string> Update(string robotId, IEnumerable<string> currentTypes)
    {
        var current = new HashSet<string>(currentTypes, StringComparer.OrdinalIgnoreCase);
        var previous = _activeByRobot.GetOrAdd(robotId, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        List<string> appeared;
        lock (previous)
        {
            appeared = current.Where(t => !previous.Contains(t)).ToList();
            previous.Clear();
            foreach (var t in current) previous.Add(t);
        }
        return appeared;
    }

    /// <summary>로봇이 마지막 state에서 errors를 보고 중인가 — SAIGE 로봇 상태(ERROR) 판정용.</summary>
    public bool HasActiveErrors(string robotId)
    {
        if (!_activeByRobot.TryGetValue(robotId, out var active)) return false;
        lock (active) return active.Count > 0;
    }
}
