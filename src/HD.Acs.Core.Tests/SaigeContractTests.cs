using HD.Acs.Core.Geometry;
using HD.Acs.Core.Integration;
using Xunit;

namespace HD.Acs.Core.Tests;

/// <summary>SAIGE 연동 대외 규약 순수 로직 [SAIGE 연동 사양서 v2.6 §2.3/§6.3/§7.4/§9].</summary>
public class SaigeContractTests
{
    [Theory]
    [InlineData("B", 1)] [InlineData("T", 2)] [InlineData("PM", 3)] [InlineData("SM", 4)] [InlineData("F", 5)]
    [InlineData("A", 6)] [InlineData("PL", 7)] [InlineData("SL", 8)] [InlineData("PU", 9)] [InlineData("SU", 10)]
    [InlineData("pm", 3)]          // 대소문자 무시
    [InlineData("XX", 0)]          // 미등록 = 0(예약 — 미지정)
    [InlineData(null, 0)]
    public void WallId_MatchesAppendixA(string? code, int expected) =>
        Assert.Equal(expected, WallIds.FromCode(code));

    /// <summary>TankGeometry가 생성하는 10면 코드가 전부 1~10에 1:1로 대응한다(누락·중복 없음).</summary>
    [Fact]
    public void WallId_CoversAllGeneratedWalls()
    {
        var geom = new HD.Acs.Core.Planning.TankGeometry(45, 8.2, Math.PI / 4, 1.9, 5.4, Math.PI / 4, 1.9, new[] { 0.0, 2.4, 4.8, 7.2 });
        var ids = geom.GenerateWalls().Select(w => WallIds.FromCode(w.WallCode)).OrderBy(i => i).ToArray();
        Assert.Equal(Enumerable.Range(1, 10).ToArray(), ids);
    }

    [Theory]
    [InlineData(12.48, 12480)] [InlineData(-22.5, -22500)] [InlineData(0.0004, 0)] [InlineData(0.0005, 1)] [InlineData(-0.0005, -1)]
    public void ToMm_RoundsToInteger(double m, int mm) => Assert.Equal(mm, SaigeUnits.ToMm(m));

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(Math.PI / 2, 90.0)]
    [InlineData(-Math.PI / 2, 270.0)]      // 음수 → 0~360
    [InlineData(Math.PI, 180.0)]
    [InlineData(-0.0001, 0.0)]             // 359.99… 반올림 360.0 → 0.0으로 접힘 (범위 0.0~359.9)
    [InlineData(1.0, 57.3)]                // 소수 1자리
    public void ToYawDeg_NormalizesTo0_360(double rad, double deg) => Assert.Equal(deg, SaigeUnits.ToYawDeg(rad), 9);

    [Theory]
    [InlineData("CT1-L1", 1)] [InlineData("CT1-L4", 4)] [InlineData("BV-2-L12", 12)]
    [InlineData("CT1", null)] [InlineData("CT1-L0", null)] [InlineData("CT1-Lx", null)] [InlineData(null, null)]
    public void LevelFromMapId_Is1Based(string? mapId, int? level) => Assert.Equal(level, SaigeUnits.LevelFromMapId(mapId));

    [Theory]
    // 두절이 최우선 — 오류·작업이 있어도 DISCONNECTED
    [InlineData("CONNECTIONBROKEN", true, false, true, RobotHealthStatus.DISCONNECTED)]
    [InlineData("OFFLINE", false, false, false, RobotHealthStatus.DISCONNECTED)]
    [InlineData(null, false, false, false, RobotHealthStatus.DISCONNECTED)]   // connection 미수신
    [InlineData("ONLINE", true, false, true, RobotHealthStatus.ERROR)]        // 오류 > 작업 중
    [InlineData("ONLINE", false, true, false, RobotHealthStatus.ERROR)]       // 작업 중단 상태
    [InlineData("ONLINE", false, false, true, RobotHealthStatus.WORKING)]
    [InlineData("ONLINE", false, false, false, RobotHealthStatus.IDLE)]
    public void RobotHealth_Priority(string? conn, bool errors, bool aborted, bool dispatched, RobotHealthStatus expected) =>
        Assert.Equal(expected, RobotHealth.Derive(new RobotHealthInput(conn, errors, aborted, dispatched)));

    /// <summary>맵→도면 역변환: DrawingToMap/DrawingYawToMap으로 만든 맵 포즈가 원래 도면 포즈(mm·deg)로 복원된다.</summary>
    [Fact]
    public void ToDrawingPosition_InvertsCalibration()
    {
        var t = new DrawingTransform(Tx: 11.91, Ty: 16.65, YawRad: 1.574);   // 현장 재캘리브레이션 실측급 값(~90°)
        var (mx, my) = t.DrawingToMap(12.48, 5.12);
        double mapTheta = t.DrawingYawToMap(Math.PI / 2);

        var (x, y, yaw) = RobotHealth.ToDrawingPosition(t, mx, my, mapTheta);

        Assert.Equal(12480, x);
        Assert.Equal(5120, y);
        Assert.Equal(90.0, yaw, 9);
    }

    [Fact]
    public void ToDrawingPosition_NoTheta_YawZero()
    {
        var (_, _, yaw) = RobotHealth.ToDrawingPosition(new DrawingTransform(1, 2, 0.5), 3, 4, null);
        Assert.Equal(0.0, yaw);
    }

    [Fact]
    public void MapYawToDrawing_IsInverseOfDrawingYawToMap()
    {
        var t = new DrawingTransform(0, 0, 2.9);
        foreach (var d in new[] { -3.0, -1.2, 0.0, 0.7, 3.1 })
            Assert.Equal(d, t.MapYawToDrawing(t.DrawingYawToMap(d)), 9);
    }

    [Theory]
    // §6.3: 시도 중 성공이 하나라도 있으면 성공 (같은 정차의 다른 TASK 실패로 재실행되어도 불변)
    [InlineData(new[] { "FAILED", "FINISHED" }, "DONE", "SUCCESS")]
    [InlineData(new[] { "FINISHED", "FAILED" }, "SKIPPED", "SUCCESS")]
    [InlineData(new[] { "FINISHED" }, "PENDING", "SUCCESS")]        // 정차가 재큐잉 중이어도 이미 성공한 TASK는 종결
    // 모든 시도 실패 + 재시도 여유 → 미종결
    [InlineData(new[] { "FAILED" }, "PENDING", null)]
    [InlineData(new[] { "FAILED" }, "DISPATCHED", null)]
    [InlineData(new[] { "WAITING" }, "DISPATCHED", null)]
    [InlineData(new[] { "RUNNING" }, "DISPATCHED", null)]
    // 모든 시도 실패 + 소진 → 실패
    [InlineData(new[] { "FAILED", "FAILED" }, "SKIPPED", "FAILED")]
    // 소진됐는데 종결 시도 기록이 없음 → SKIPPED
    [InlineData(new[] { "WAITING" }, "SKIPPED", "SKIPPED")]
    [InlineData(new string[0], "SKIPPED", "SKIPPED")]
    public void TaskOutcome_UniqueTaskFinalResult(string[] actions, string workItem, string? expected) =>
        Assert.Equal(expected, TaskOutcome.Classify(actions, workItem));

    /// <summary>attempt = taskId별 누적(1부터), UInt8 프레임 필드라 255에서 포화(되감김 금지).</summary>
    [Theory]
    [InlineData(0, 1)] [InlineData(1, 2)] [InlineData(253, 254)] [InlineData(254, 255)] [InlineData(255, 255)] [InlineData(9999, 255)]
    [InlineData(-3, 1)]
    public void TaskAttempt_AccumulatesAndSaturates(int alreadyIssued, int expected) =>
        Assert.Equal(expected, TaskAttempt.Next(alreadyIssued));
}
