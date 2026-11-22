using HD.Acs.UI.Services;
using HD.Acs.UI.ViewModels;
using Xunit;

namespace HD.Acs.UI.Core.Tests;

/// <summary>
/// 층의 화면 표기 — 내부 층 번호(1부터)·맵 ID 는 그대로 두고 화면에서만 맨 아래층을 L0 으로 부른다.
/// 표기가 한 칸 내려가도 선택한 층(내부 번호)이 어긋나면 안 된다.
/// </summary>
public class LevelLabelTests
{
    [Fact]
    public void BottomFloorIsShownAsL0_ButInternalNumbersStay()
    {
        Assert.Equal(0, LevelLabel.Base);
        Assert.Equal(new[] { "L0", "L1", "L2", "L3" }, new[] { 1, 2, 3, 4 }.Select(LevelLabel.Of));
        Assert.Equal("L3", LevelLabel.OfMapId("CT1-L4"));
        Assert.Equal("L3", LevelLabel.OfMapId("HN8250-CT2-L4"));       // 선창 ID 에 '-' 가 있어도 끝의 층만 읽는다
        Assert.Equal("L3 (맵 CT1-L4)", LevelLabel.WithMapId("CT1-L4"));   // 로그·AMR 화면과 대조할 수 있게 맵 ID 를 같이 보인다
        Assert.Equal("이상한값", LevelLabel.OfMapId("이상한값"));
        Assert.Equal("-", LevelLabel.WithMapId(null));
        Assert.Equal("L0", LevelLabel.OfKey("L1"));
    }

    /// <summary>층 목록 — 화면에는 L0~L3, 맵 ID 는 CT1-L1~L4 그대로.</summary>
    [Fact]
    public void FloorList_ShowsNewLabels_WithUnchangedMapIds()
    {
        var floors = TankLayout.FloorsFor("CT1");
        Assert.Equal(new[] { "L0", "L1", "L2", "L3" }, floors.Select(f => f.Label));
        Assert.Equal(new[] { "CT1-L1", "CT1-L2", "CT1-L3", "CT1-L4" }, floors.Select(f => f.MapId));
    }

    /// <summary>3D 뷰의 층 선택 — 화면에서 "L3" 을 고르면 내부 층 4(맵 CT1-L4)다. 표기를 숫자로 읽지 않는다.</summary>
    [Fact]
    public void ViewModeSelection_MapsLabelToInternalLevelByPosition()
    {
        var api = System.Reflection.DispatchProxy.Create<IAcsApiClient, OperationStatusTests.NullApi>();
        var vm = new TankViewModel(api, System.Reflection.DispatchProxy.Create<IMonitoringClient, GasRoundTests.QuietProxy>());

        Assert.Equal(new[] { "전체", "L0", "L1", "L2", "L3" }, vm.ViewModes);
        Assert.Null(vm.SelectedLevel);

        vm.SelectedViewMode = "L3";
        Assert.Equal(4, vm.SelectedLevel);
        vm.SelectedViewMode = "L0";
        Assert.Equal(1, vm.SelectedLevel);
        vm.SelectedViewMode = TankViewModel.ViewModeOf(2);
        Assert.Equal(2, vm.SelectedLevel);
    }
}
