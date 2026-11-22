using HD.Acs.Core.Reach;
using Xunit;

namespace HD.Acs.Core.Tests;

/// <summary>
/// HD_AMR 리치 사전 점검과 맞춘 규칙 — 최소 플랜지 뻗음(면에 너무 가깝거나 리프트가 높은 정차), 팔 뻗음 상한(너무 먼 정차).
/// 2026-11-11~12 천장(T) 실적: 리프트 100 mm 정차 52건 전부 "플랜지 뻗음 342mm &lt; 최소 350mm" 로 거부 →
/// 2026-11-13 천장은 최소 뻗음 규칙 제외(근거 실측은 수직벽).
/// </summary>
public class ReachAmrRulesTests
{
    private static readonly ReachRobotModel Field = ReachRobotModel.Default with
    {
        MountYawDeg = 180, ToolZMm = 365, StageStandoffsMm = new[] { 400.0, 300, 10 }, MinFlangeReachMm = 350,
    };

    private static readonly double[] CeilingNormal = { 0, 0, -1 };
    private static readonly double[] OnCeiling = { 0.6, 0.8, 2.8425 };   // 층 바닥 위 2.8425 m (CT1 L4 → 천장)
    private static readonly ReachStation Station = new(0, 0, 0, 0);
    private static readonly ReachPlane[] Planes = { new("T", new[] { 0.0, 0, 2.8425 }, CeilingNormal) };

    [Fact]
    public void 플랜지_뻗음은_AMR_과_같은_식이다()
    {
        // BASE 높이 1635 + 리프트, 면까지 법선 거리 − ② 이격 400 − 공구 365
        Assert.Equal(442.5, ReachSimulator.FlangeReachMm(Field, Station, 0, OnCeiling, CeilingNormal), 3);
        Assert.Equal(342.5, ReachSimulator.FlangeReachMm(Field, Station, 100, OnCeiling, CeilingNormal), 3);   // AMR 보고 342mm
        // 평면이라 정차 위치·면 위의 점과 무관하다(천장)
        Assert.Equal(442.5, ReachSimulator.FlangeReachMm(Field, new ReachStation(3, -2, 1.2, 0), 0, new[] { 9.0, 9, 2.8425 }, CeilingNormal), 3);
    }

    [Fact]
    public void 수직벽에_너무_가까우면_뻗음이_모자라_도달_불가다()
    {
        // 근거 실측(2026-09-28): 벽에서 1.0 m 정차 → 1000 − 400 − 365 = 235 mm < 350 mm (실기 공구 370 이면 230)
        var wallNormal = new[] { -1.0, 0, 0 };   // 정차 앞 +x 쪽 벽, 안쪽 법선은 −x
        var onWall = new[] { 1.0, 0, 1.6 };
        Assert.Equal(235, ReachSimulator.FlangeReachMm(Field, Station, 0, onWall, wallNormal), 3);
        Assert.Contains("플랜지 뻗음 부족", ReachSimulator.FlangeShortReason(Field, Station, 0, onWall, wallNormal));
        Assert.Null(ReachSimulator.FlangeShortReason(Field, Station, 0, new[] { 1.2, 0, 1.6 }, wallNormal));   // 435 mm
        Assert.Null(ReachSimulator.FlangeShortReason(Field with { MinFlangeReachMm = 0 }, Station, 0, onWall, wallNormal));
    }

    [Fact]
    public void 천장은_최소_뻗음_규칙을_적용하지_않는다()
    {
        // 2026-11-11 리프트 100 정차 52건이 이 규칙(342 mm < 350 mm)으로만 거부됐다 — 천장은 접힌 팔에서도
        // ② 자세 J5 가 90° 근처라 손목 특이점이 나지 않아 규칙을 뺀다(2026-11-13, HD_AMR 와 동시).
        Assert.False(ReachSimulator.FlangeRuleApplies(CeilingNormal));
        Assert.True(ReachSimulator.FlangeRuleApplies(new[] { -1.0, 0, 0 }));                          // 수직벽
        Assert.True(ReachSimulator.FlangeRuleApplies(new[] { 0, -Math.Sqrt(0.5), -Math.Sqrt(0.5) }));   // 상부 경사면
        Assert.True(ReachSimulator.FlangeRuleApplies(new[] { 0.0, 0, 1 }));                            // 바닥
        Assert.Null(ReachSimulator.FlangeShortReason(Field, Station, 100, OnCeiling, CeilingNormal));

        var pts = new[] { OnCeiling, new[] { 0.6, 0.98, 2.8425 }, new[] { 0.6, 1.16, 2.8425 } };
        var m = Field with { CheckTraversal = false };
        var s = ReachSimulator.Survey(m, Planes, new[] { Station }, pts, CeilingNormal, new[] { 100.0 });
        Assert.NotEqual(ReachVerdict.Unreachable, s.Verdict);
        Assert.DoesNotContain("플랜지 뻗음 부족", s.Blocker ?? "");
    }

    [Fact]
    public void 팔을_거의_다_뻗는_먼_정차는_팔_뻗음_상한으로_막는다()
    {
        // 정차에서 수평 1.8 m 떨어진 천장 용접선 — 관절 한계만 보면 닿지만 팔꿈치가 거의 펴진다
        var far = new[] { new[] { 1.27, 1.27, 2.8425 }, new[] { 1.27, 1.09, 2.8425 }, new[] { 1.27, 0.91, 2.8425 } };
        var loose = Field with { CheckTraversal = false };
        Assert.NotEqual(ReachVerdict.Unreachable, ReachSimulator.Survey(loose, Planes, new[] { Station }, far, CeilingNormal, new[] { 0.0 }).Verdict);

        var strict = ReachSimulator.Survey(loose with { MinElbowBendDeg = 30 }, Planes, new[] { Station }, far, CeilingNormal, new[] { 0.0 });
        Assert.Equal(ReachVerdict.Unreachable, strict.Verdict);
        Assert.Contains("팔 뻗음 과다", strict.Blocker);

        // 가까운 용접선(수평 1.0 m)은 상한을 둬도 그대로 닿는다
        var near = new[] { new[] { 0.6, 0.8, 2.8425 }, new[] { 0.6, 0.98, 2.8425 }, new[] { 0.6, 1.16, 2.8425 } };
        Assert.NotEqual(ReachVerdict.Unreachable,
            ReachSimulator.Survey(loose with { MinElbowBendDeg = 30 }, Planes, new[] { Station }, near, CeilingNormal, new[] { 0.0 }).Verdict);
    }
}
