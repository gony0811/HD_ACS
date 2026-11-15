using HD.Acs.App.Services;
using HD.Acs.Core.Reach;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace HD.Acs.App.Tests;

/// <summary>AMR 이 알려 준 코봇 모델 값(d6·손목 여유·초점 거리)과 ACS 설정의 대조.</summary>
public class RobotModelCheckTests
{
    private static ReachRobotModel Acs(params (string K, string V)[] kv) => ReachSimService.ModelFrom(new ConfigurationBuilder()
        .AddInMemoryCollection(kv.Append(("Acs:Reach:ToolZMm", "365")).Append(("Acs:Reach:FocusStandoffMm", "10"))
            .ToDictionary(x => x.Item1, x => (string?)x.Item2)).Build());

    [Fact]
    public void 같은_값이면_차이가_없다()
    {
        var acs = Acs(("Acs:Reach:D6Mm", "106"));
        var same = new AmrRobotModel(106, acs.PlanarWristMarginDeg, 375);
        Assert.Empty(RobotModelCheck.Compare(acs, same));
        Assert.Empty(RobotModelCheck.Compare(acs, new AmrRobotModel(null, null, null)));   // 모르는 값은 대조하지 않는다
    }

    [Fact]
    public void d6와_초점_거리가_다르면_항목마다_알린다()
    {
        var acs = Acs(("Acs:Reach:D6Mm", "106"));
        var diff = RobotModelCheck.Compare(acs, new AmrRobotModel(160, acs.PlanarWristMarginDeg + 5, 365));
        Assert.Equal(3, diff.Count);
        Assert.Contains(diff, d => d.Contains("d6") && d.Contains("160") && d.Contains("106"));
        Assert.Contains(diff, d => d.Contains("손목 여유"));
        Assert.Contains(diff, d => d.Contains("초점 거리") && d.Contains("375"));
    }

    [Fact]
    public void 대조_결과가_바뀔_때만_새_보고로_본다()
    {
        var store = new AmrRobotModelStore();
        var m = new AmrRobotModel(160, 10, 375);
        Assert.True(store.Report("AMR-01", m, new[] { "d6" }));
        Assert.False(store.Report("amr-01", m, new[] { "d6" }));
        Assert.True(store.Report("AMR-01", m, Array.Empty<string>()));
        Assert.Single(store.All);
    }
}
