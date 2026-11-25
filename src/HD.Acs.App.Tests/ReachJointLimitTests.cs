using System.Text.Json.Nodes;
using HD.Acs.App.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace HD.Acs.App.Tests;

/// <summary>
/// 관절 사용 범위 — HD_AMR 이 factsheet cobot 묶음으로 보고한 자세 허용 범위를 리치 모델이 쓴다.
/// ACS 가 사양(J3 ±170°)으로 정차를 고르면 AMR(J3 ±160°)이 거부했다(2026-11-25 SU L4-R02C13-5: J3 160.9°, 정차 이격 1.40 m).
/// </summary>
public class ReachJointLimitTests
{
    private static readonly double[] AmrMin = { -175, -175, -160, -175, -175, -180 };
    private static readonly double[] AmrMax = { 175, 175, 160, 175, 175, 180 };

    private static string Body(double[]? min, double[]? max)
    {
        static JsonArray A(double[] v) => new(v.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
        var cobot = new JsonObject { ["linksMm"] = A(new[] { 180, -970.317, -815.788, 159.25, 114.077, 105 }), ["maxEntryJointTravelDeg"] = 160 };
        if (min is not null) cobot["jointMinDeg"] = A(min);
        if (max is not null) cobot["jointMaxDeg"] = A(max);
        return new JsonObject
        {
            ["serialNumber"] = "AMR-01",
            ["hdAmr"] = new JsonObject { ["configVersion"] = 16, ["cobot"] = cobot },
        }.ToJsonString();
    }

    private static readonly HashSet<string> All = new(AmrFactsheetService.AllSections);

    [Fact]
    public void factsheet_관절_범위를_J1_J5_설정_키로_옮긴다()
    {
        var f = AmrFactsheet.Parse(Body(AmrMin, AmrMax))!;
        Assert.Equal(AmrMin, f.JointMinDeg);
        var k = AmrFactsheetService.ConfigKeys(f, All);
        Assert.Equal("-160", k["Acs:Reach:JointMinDeg:2"]);
        Assert.Equal("160", k["Acs:Reach:JointMaxDeg:2"]);
        Assert.Equal("175", k["Acs:Reach:JointMaxDeg:4"]);
        Assert.False(k.ContainsKey("Acs:Reach:JointMaxDeg:5"));   // J6 은 따로 판정
    }

    [Fact]
    public void 구버전_보고나_잘못된_범위는_싣지_않는다()
    {
        Assert.DoesNotContain(AmrFactsheetService.ConfigKeys(AmrFactsheet.Parse(Body(null, null))!, All).Keys,
            key => key.Contains("JointM"));
        var swapped = AmrFactsheet.Parse(Body(AmrMax, AmrMin))!;   // 하한 > 상한
        Assert.DoesNotContain(AmrFactsheetService.ConfigKeys(swapped, All).Keys, key => key.Contains("JointM"));
        var cobotOff = AmrFactsheetService.ConfigKeys(AmrFactsheet.Parse(Body(AmrMin, AmrMax))!, new HashSet<string> { "teaching" });
        Assert.DoesNotContain(cobotOff.Keys, key => key.Contains("JointM"));
    }

    [Fact]
    public void 리치_모델은_보고된_범위를_쓰고_없으면_사양()
    {
        var keys = AmrFactsheetService.ConfigKeys(AmrFactsheet.Parse(Body(AmrMin, AmrMax))!, All);
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(keys!).Build();
        var m = ReachSimService.ModelFrom(cfg);
        Assert.Equal(new double[] { -175, -175, -160, -175, -175 }, m.JointMinDeg);
        Assert.Equal(new double[] { 175, 175, 160, 175, 175 }, m.JointMaxDeg);

        var spec = ReachSimService.ModelFrom(new ConfigurationBuilder().Build());
        Assert.Equal(170, spec.JointMaxDeg[2]);
    }

    [Theory]
    [InlineData(new double[] { -1, -1, -1, -1 }, new double[] { 1, 1, 1, 1 })]              // 4요소
    [InlineData(new double[] { -1, -1, 5, -1, -1 }, new double[] { 1, 1, 1, 1, 1 })]        // 하한 > 상한
    [InlineData(new double[] { -1, -1, -400, -1, -1 }, new double[] { 1, 1, 1, 1, 1 })]     // ±360 밖
    public void 범위_검증(double[] min, double[] max) => Assert.Null(ReachSimService.JointLimitsFrom(min, max));
}
