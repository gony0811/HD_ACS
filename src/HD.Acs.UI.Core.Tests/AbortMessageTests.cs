using HD.Acs.UI.Models;
using HD.Acs.UI.ViewModels;
using Xunit;

namespace HD.Acs.UI.Core.Tests;

/// <summary>미션 중단 안내 — AMR 에 무엇을 시켰는지 운영자가 알 수 있어야 한다.</summary>
public class AbortMessageTests
{
    private static readonly Guid Run = Guid.NewGuid();

    [Fact]
    public void FinishCurrentTask_TellsOperatorRobotKeepsWorkingThenGoesHome()
    {
        var msg = MissionViewModel.DescribeAbort(new RunAbortResultDto(Run, "ABORTED", true, true, "afterCurrentAction"));
        Assert.Contains("진행 중인 용접선 검사를 마친 뒤", msg);
        Assert.Contains("홈으로 복귀", msg);
        Assert.Contains("비상정지", msg);
    }

    [Fact]
    public void ImmediateCancel_AndUnsentCancel_AreDistinguished()
    {
        Assert.Contains("즉시 취소", MissionViewModel.DescribeAbort(new RunAbortResultDto(Run, "ABORTED", true, true, "immediate")));
        Assert.Contains("보내지 못했습니다", MissionViewModel.DescribeAbort(new RunAbortResultDto(Run, "ABORTED", false, true, "afterCurrentAction")));
    }

    [Fact]
    public void NothingInFlight_OrOldServer_GivesPlainMessage()
    {
        string plain = MissionViewModel.DescribeAbort(null);
        Assert.Contains("이어하기", plain);
        Assert.Equal(plain, MissionViewModel.DescribeAbort(new RunAbortResultDto(Run, "ABORTED")));
    }
}
