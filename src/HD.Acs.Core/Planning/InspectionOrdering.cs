namespace HD.Acs.Core.Planning;

/// <summary>
/// 배차 후보 = 미검사 작업항목 1개. X,Y는 맵 프레임 정차 좌표(로봇 보고 좌표와 동일 프레임).
/// 순수 정렬용 최소 표현 — App의 work_item을 이 형태로 투영해 전달한다.
/// </summary>
public readonly record struct DispatchCandidate(Guid WorkItemId, double X, double Y);

/// <summary>
/// 검사 순서 결정 정책. 유휴 로봇 위치에서 대기 작업 목록 중 다음에 배차할 1건을 고른다.
/// 교체 가능(기본=greedy 최근접, 후속=serpentine/2-opt).
/// </summary>
public interface IInspectionOrderingPolicy
{
    /// <summary>다음 배차 후보. 대기 목록이 비면 null.</summary>
    DispatchCandidate? SelectNext(double robotX, double robotY, IReadOnlyList<DispatchCandidate> pending);
}

/// <summary>
/// greedy 최근접 — 로봇에서 유클리드 최단(제곱거리 비교)인 후보를 고른다.
/// 동률은 WorkItemId 순으로 결정적 처리. 되돌아옴(backtracking) 비효율은 후속 정책에서 개선.
/// </summary>
public sealed class GreedyNearestPolicy : IInspectionOrderingPolicy
{
    public DispatchCandidate? SelectNext(double robotX, double robotY, IReadOnlyList<DispatchCandidate> pending)
    {
        DispatchCandidate? best = null;
        double bestD2 = double.PositiveInfinity;
        foreach (var c in pending)
        {
            double dx = c.X - robotX, dy = c.Y - robotY;
            double d2 = dx * dx + dy * dy;
            if (d2 < bestD2 || (d2 == bestD2 && best is { } b && c.WorkItemId.CompareTo(b.WorkItemId) < 0))
            {
                bestD2 = d2;
                best = c;
            }
        }
        return best;
    }
}
