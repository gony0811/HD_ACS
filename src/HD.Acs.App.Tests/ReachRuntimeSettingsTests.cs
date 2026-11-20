using HD.Acs.App.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace HD.Acs.App.Tests;

/// <summary>리치·간섭 화면 설정 — 덮어쓰기 파일에 저장하면 설정을 다시 읽어 바로 모델·지령 리프트에 반영된다.</summary>
public class ReachRuntimeSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hdacs-reach-" + Guid.NewGuid().ToString("N"));

    public ReachRuntimeSettingsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private (IConfigurationRoot Cfg, ReachRuntimeSettings S) Make()
    {
        var path = Path.Combine(_dir, ReachRuntimeSettings.FileName);
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Acs:Lift:Mode"] = "Off",
                ["Acs:Reach:MountZMm"] = "927",
                ["Acs:Reach:AmrHeightMm"] = "765",
                ["Acs:Reach:SupportColumn"] = "true",
            })
            .AddJsonFile(path, optional: true, reloadOnChange: false)
            .Build();
        return (cfg, new ReachRuntimeSettings(cfg, path));
    }

    [Fact]
    public void 저장하면_바로_모델과_지령_리프트가_바뀌고_초기화하면_돌아간다()
    {
        var (cfg, s) = Make();
        var v0 = s.Read();
        Assert.Equal("Off", v0.LiftMode);
        Assert.Equal(927, v0.MountZMm);
        Assert.Empty(v0.Overridden);

        var v = s.Save(new ReachSettingsUpdate("Fixed", 300, 1635, 887, false, 170));
        Assert.Equal("Fixed", v.LiftMode);
        Assert.Equal(300, v.FixedStrokeMm);
        Assert.Equal(6, v.Overridden.Count);

        // 설정을 요청마다 읽는 쪽(모델·배차 지령 높이)이 바로 새 값을 쓴다
        var model = ReachSimService.ModelFrom(cfg);
        Assert.Equal(1635, model.MountZMm);
        Assert.Equal(887, model.AmrHeightMm);
        Assert.False(model.SupportColumn);
        Assert.Equal(170, model.MaxEntryJointTravelDeg);
        Assert.Equal(300, model.EffectiveStrokeMm(600));
        Assert.Equal(300, LiftSettings.From(cfg).ForOrder(600));

        // 리프트만 바꾸면 나머지는 appsettings 값으로 돌아간다(보낸 값으로 통째로 교체)
        s.Save(new ReachSettingsUpdate("Auto", null, null, null, null, null));
        Assert.Equal(600, LiftSettings.From(cfg).ForOrder(600));
        Assert.Equal(927, ReachSimService.ModelFrom(cfg).MountZMm);
        Assert.Equal(new[] { "Acs:Lift:Mode" }, s.Read().Overridden);

        var r = s.Reset();
        Assert.Equal("Off", r.LiftMode);
        Assert.Empty(r.Overridden);
        Assert.Equal(0, LiftSettings.From(cfg).ForOrder(600));
    }

    [Fact]
    public void 범위_밖_값은_거부한다()
    {
        Assert.Empty(ReachRuntimeSettings.Validate(new ReachSettingsUpdate("fixed", 0, 927, 765, true, 160)));
        Assert.Empty(ReachRuntimeSettings.Validate(new ReachSettingsUpdate(null, null, null, null, null, null)));
        Assert.Single(ReachRuntimeSettings.Validate(new ReachSettingsUpdate("Up", null, null, null, null, null)));
        Assert.Single(ReachRuntimeSettings.Validate(new ReachSettingsUpdate("Fixed", null, null, null, null, null)));   // 고정인데 높이 없음
        Assert.Single(ReachRuntimeSettings.Validate(new ReachSettingsUpdate("Fixed", 1200, null, null, null, null)));
        Assert.Single(ReachRuntimeSettings.Validate(new ReachSettingsUpdate(null, null, 200, null, null, null)));
        Assert.Single(ReachRuntimeSettings.Validate(new ReachSettingsUpdate(null, null, null, 2000, null, null)));
        Assert.Single(ReachRuntimeSettings.Validate(new ReachSettingsUpdate(null, null, null, null, null, 10)));
        Assert.Single(ReachRuntimeSettings.Validate(new ReachSettingsUpdate(null, double.NaN, null, null, null, null)));
    }
}
