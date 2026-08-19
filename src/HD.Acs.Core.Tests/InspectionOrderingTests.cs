using HD.Acs.Core.Planning;
using Xunit;

namespace HD.Acs.Core.Tests;

/// <summary>greedy 최근접 배차 정책 — 순수 정렬 로직 단위 테스트.</summary>
public class InspectionOrderingTests
{
    private static readonly IInspectionOrderingPolicy Policy = new GreedyNearestPolicy();

    [Fact]
    public void 빈_목록이면_null()
    {
        Assert.Null(Policy.SelectNext(0, 0, System.Array.Empty<DispatchCandidate>()));
    }

    [Fact]
    public void 로봇에서_가장_가까운_후보_선택()
    {
        var far = new DispatchCandidate(System.Guid.NewGuid(), 10, 10);
        var near = new DispatchCandidate(System.Guid.NewGuid(), 1, 1);
        var mid = new DispatchCandidate(System.Guid.NewGuid(), 5, 0);

        var pick = Policy.SelectNext(0, 0, new[] { far, near, mid });

        Assert.Equal(near.WorkItemId, pick!.Value.WorkItemId);
    }

    [Fact]
    public void 로봇_위치가_바뀌면_최근접도_바뀐다()
    {
        var a = new DispatchCandidate(System.Guid.NewGuid(), 0, 0);
        var b = new DispatchCandidate(System.Guid.NewGuid(), 10, 0);
        var list = new[] { a, b };

        Assert.Equal(a.WorkItemId, Policy.SelectNext(1, 0, list)!.Value.WorkItemId);
        Assert.Equal(b.WorkItemId, Policy.SelectNext(9, 0, list)!.Value.WorkItemId);
    }

    [Fact]
    public void 동률은_WorkItemId_순으로_결정적()
    {
        var g1 = new System.Guid("00000000-0000-0000-0000-000000000001");
        var g2 = new System.Guid("00000000-0000-0000-0000-000000000002");
        var c2 = new DispatchCandidate(g2, 3, 4);   // 거리 5
        var c1 = new DispatchCandidate(g1, 3, 4);   // 거리 5 (동률)

        var pick = Policy.SelectNext(0, 0, new[] { c2, c1 });

        Assert.Equal(g1, pick!.Value.WorkItemId);   // 입력 순서와 무관하게 작은 id
    }
}
