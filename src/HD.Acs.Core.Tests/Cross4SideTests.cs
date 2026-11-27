using HD.Acs.Core.Planning;
using Xunit;

namespace HD.Acs.Core.Tests;

/// <summary>
/// CROSS4 정면 좌우 [VDA §8.2 · 1.22] — 면을 정면으로 마주 봤을 때 수직 용접선이 작업 가운데에서 왼쪽에 있으면 L, 오른쪽에 있으면 R.
/// 작업 시작/끝 기준이 아님을 좌현·우현(ACS u 축이 둘 다 +x)으로 확인한다.
/// </summary>
public class Cross4SideTests
{
    private static Dictionary<string, GeneratedWall> Walls() => new TankGeometry(
        L: 30, WFloor: 10, ThetaLow: Math.PI / 4, HLow: 3,
        HWall: 8, ThetaUp: Math.PI / 4, HUp: 2, LevelZ: new[] { 0.0, 3.2, 6.4, 9.6 })
        .GenerateWalls().ToDictionary(w => w.WallCode);

    /// <summary>가로 작업 1.00–1.36 에서 수직선이 끝점 쪽(1.20, 가운데 1.18 보다 +u)이면 atEnd, 시작점 쪽(1.16)이면 아님.</summary>
    private static string? Side(string wall, bool atEnd, double su = 1.0, double sv = 2.0, double eu = 1.36, double ev = 2.0)
    {
        var w = Walls()[wall];
        double mid = (su + eu) / 2, toward = Math.Sign(eu - su) * (atEnd ? 1 : -1);
        return Cross4Side.SideOfVertical(su, sv, eu, ev, mid + 0.02 * toward, w.Pose.U, w.Pose.V, w.Normal);
    }

    [Fact]
    public void Port_And_Starboard_GiveOppositeSides_ForSameStartEnd()
    {
        // ACS 긴 면 u 축은 좌현·우현 모두 +x(선수 쪽). 좌현 벽을 마주 보면 선수는 오른쪽, 우현 벽을 마주 보면 왼쪽.
        Assert.Equal(1.0, Walls()["PM"].Pose.U[0], 9);
        Assert.Equal(1.0, Walls()["SM"].Pose.U[0], 9);
        Assert.Equal(Cross4Side.Right, Side("PM", atEnd: true));
        Assert.Equal(Cross4Side.Left, Side("SM", atEnd: true));
        Assert.Equal(Cross4Side.Left, Side("PM", atEnd: false));
        Assert.Equal(Cross4Side.Right, Side("SM", atEnd: false));
        // 시작·끝을 바꿔 그려도 교차점이 같은 자리면 같은 값(시작/끝 기준이 아니다)
        Assert.Equal(Side("PM", atEnd: true), Side("PM", atEnd: false, su: 1.36, eu: 1.0));
    }

    [Theory]
    [InlineData("PL")] [InlineData("PU")] [InlineData("SL")] [InlineData("SU")] [InlineData("F")] [InlineData("A")]
    public void LeftDir_IsRobotLeft_WhenFacingWall(string wall)
    {
        var w = Walls()[wall];
        var left = Cross4Side.LeftDir(w.Normal)!;
        // 로봇은 −법선(수평)을 바라본다. 바라보는 방향 × 왼쪽 = +z(위) — 오른손 좌표계에서 왼쪽이 맞다
        double hx = -w.Normal[0], hy = -w.Normal[1], hl = Math.Sqrt(hx * hx + hy * hy);
        Assert.True((hx * left[1] - hy * left[0]) / hl > 0.99);
        // 선수 격벽(F)을 마주 보면 왼쪽은 좌현(+y), 선미 격벽(A)은 우현(−y)
        if (wall == "F") Assert.True(left[1] > 0.99);
        if (wall == "A") Assert.True(left[1] < -0.99);
    }

    [Theory]
    [InlineData("B")] [InlineData("T")]
    public void FloorAndCeiling_AreUndecided(string wall)
    {
        Assert.Null(Cross4Side.LeftDir(Walls()[wall].Normal));
        Assert.Null(Side(wall, atEnd: true));
    }

    [Fact]
    public void VerticalTask_IsUndecided() =>
        Assert.Null(Side("PM", atEnd: true, su: 1.0, sv: 2.0, eu: 1.0, ev: 2.36));

    [Fact]
    public void CenteredVertical_IsUndecided()
    {
        var w = Walls()["PM"];
        Assert.Null(Cross4Side.SideOfVertical(1.0, 2.0, 1.36, 2.0, 1.18, w.Pose.U, w.Pose.V, w.Normal));     // CT1 도면 = 정가운데
        Assert.Null(Cross4Side.SideOfVertical(1.0, 2.0, 1.36, 2.0, 1.185, w.Pose.U, w.Pose.V, w.Normal));    // 1 cm 미만
        Assert.Equal(Cross4Side.Right, Cross4Side.SideOfVertical(1.0, 2.0, 1.36, 2.0, 1.195, w.Pose.U, w.Pose.V, w.Normal));
    }

    [Fact]
    public void CrossingVerticalU_FromVerticalNeighbours()
    {
        // CT1 생성 꼴: 가로 칸 12.60–12.96(v 1.844), 수직선 u 12.78 의 위아래 세로 칸(교차점 근처 0.18 m 비움)
        var others = new[] { (12.78, 2.024, 12.78, 2.384), (12.78, 1.304, 12.78, 1.664), (12.96, 1.844, 13.32, 1.844) };
        Assert.Equal(12.78, Cross4Side.CrossingVerticalU(12.6, 1.844, 12.96, 1.844, others)!.Value, 9);
        Assert.Null(Cross4Side.CrossingVerticalU(12.6, 1.844, 12.96, 1.844, new[] { (13.32, 1.844, 13.68, 1.844) }));     // 세로 없음
        Assert.Null(Cross4Side.CrossingVerticalU(12.6, 1.844, 12.96, 1.844, new[] { (12.7, 2.0, 12.7, 2.3), (12.85, 1.3, 12.85, 1.6) }));  // 여럿
        Assert.Null(Cross4Side.CrossingVerticalU(12.6, 1.844, 12.96, 1.844, new[] { (12.78, 3.0, 12.78, 3.36) }));          // 멀리 있음
    }

    [Theory]
    [InlineData("CROSS4", true)] [InlineData("cross4_t1", true)] [InlineData("CROSS4_T2", true)] [InlineData("CROSS", true)] [InlineData("CROSS4_T3", true)] [InlineData("CROSS4_T4", true)] [InlineData("CROSS4_L", true)] [InlineData("CROSS4_X", false)]
    [InlineData("CROSS3_R0", false)] [InlineData("LINE", false)] [InlineData(null, false)]
    public void IsCross4(string? st, bool expected) => Assert.Equal(expected, Cross4Side.IsCross4(st));
}
