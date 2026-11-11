using HD.Acs.UI.Models;
using HD.Acs.UI.ViewModels;
using Xunit;

namespace HD.Acs.UI.Core.Tests;

/// <summary>
/// 수동 비드 입력 요청(manualBeadInputRequired) 화면 표시 — 고장이 아니라 작업자 호출이다 [VDA §6.4].
///  · 이벤트 기록: 경고색, "오류 보고"가 아니라 "수동 비드 입력 요청"
///  · 현재 상태 카드: "수동 비드 입력 대기"(warn) — 비상정지·배터리·장치 알람·통신 단절보다는 아래
/// </summary>
public class ManualBeadInputStatusTests
{
    private const string Bead = "manualBeadInputRequired: Bead1 찾기: 수동 비드 입력 대기 — 자동 검출 신뢰도 낮음 — coverage 41% < 60%";

    private static RobotStateDto State(params string[] errors) =>
        new("AMR-01", "CT1-L4", 1, 2, 80, "o", null, false, errors.Length, null, "AUTOMATIC", "NONE", errors);

    [Fact]
    public void 입력_요청은_경고색이고_오류_보고가_아닌_문구로_적는다()
    {
        Assert.True(AlarmsViewModel.IsManualBeadInput(Bead));
        Assert.True(AlarmsViewModel.IsManualBeadInput("manualBeadInputRequired"));
        Assert.False(AlarmsViewModel.IsManualBeadInput("equipmentError: x"));
        Assert.Equal("warn", AlarmsViewModel.ClassifyErrorKind(Bead));

        var text = AlarmsViewModel.ManualBeadText(Bead);
        Assert.StartsWith("수동 비드 입력 요청 — Bead1 찾기", text);
        Assert.DoesNotContain("manualBeadInputRequired", text);
        Assert.Contains("AMR 화면", AlarmsViewModel.ManualBeadText("manualBeadInputRequired"));
    }

    [Fact]
    public void 검사_중에_팝업이_뜨면_상태_카드가_수동_비드_입력_대기로_바뀐다()
    {
        var s = AlarmsViewModel.Describe("ONLINE", State(Bead), "RUNNING", "L4-R09C09", 0, true, null);

        Assert.Equal("warn", s.Kind);
        Assert.Equal("수동 비드 입력 대기 — L4-R09C09", s.Headline);
        Assert.Contains("AMR 화면의 팝업", s.Detail);
        Assert.Contains("녹색 점멸", s.Detail);
        Assert.Contains("coverage 41%", s.Detail);
    }

    [Fact]
    public void ACS_run_없이_AMR_화면에서_단독_실행_중이어도_알린다()
    {
        var s = AlarmsViewModel.Describe("ONLINE", State(Bead), null, null, 0, false, null);

        Assert.Equal("warn", s.Kind);
        Assert.Equal("수동 비드 입력 대기", s.Headline);
    }

    [Fact]
    public void 더_급한_상태가_있으면_그쪽이_먼저다()
    {
        Assert.Equal("비상정지 상태", AlarmsViewModel.Describe("ONLINE",
            State("emergencyStopActive: x", Bead), "RUNNING", "A", 0, true, null).Headline);
        Assert.Equal("장치 통신 두절 — 현장 RESET 필요", AlarmsViewModel.Describe("ONLINE",
            State("cobotLinkLost: c", Bead), "RUNNING", "A", 0, true, null).Headline);
        Assert.Equal("통신 복구 — 재개 확인 필요", AlarmsViewModel.Describe("ONLINE",
            State("communicationLostPause: PAUSE", Bead), "RUNNING", "A", 0, true, null).Headline);
        Assert.Equal("로봇 통신 두절", AlarmsViewModel.Describe("OFFLINE",
            State(Bead), "RUNNING", "A", 0, true, null).Headline);
    }

    [Fact]
    public void 팝업이_닫히면_원래_진행_표시로_돌아간다()
    {
        var s = AlarmsViewModel.Describe("ONLINE", State(), "RUNNING", "L4-R09C09", 0, true, null);

        Assert.Equal("run", s.Kind);
        Assert.Equal("검사 중 — L4-R09C09", s.Headline);
    }
}
