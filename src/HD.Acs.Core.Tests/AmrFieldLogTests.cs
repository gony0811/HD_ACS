using HD.Acs.Core.Reach;
using Xunit;

namespace HD.Acs.Core.Tests;

/// <summary>HD_AMR 앱 로그 해석 — 줄은 2026-11-12 현장 로그에서 그대로 옮긴 것(긴 사유는 줄였다).</summary>
public class AmrFieldLogTests
{
    private const string Exec = "[Information] [HD.AMR.App.Service.Vda5050OrderExecutor]";
    private const string Orch = "[Information] [HD.AMR.App.Service.Inspection.WeldInspectionOrchestrator]";
    private const string Pre = "[Warning    ] [HD.AMR.App.Service.Motion.CobotReachPrecheck]";

    private static string Accept(string time, string job, string task, int attempt = 1, int lift = 0, string recipe = "LINE") =>
        $"[{time}] {Orch} startWeldInspection 수리: jobRef={job}, seamType=Line, wall=T → recipe={recipe} (dxf=WALL T, profileId(촬영)=PROF-1, anchor=X#1, liftStroke={lift}mm, taskId={task}, attempt={attempt})";

    private static readonly string T1 = "9f97cd21-adf3-53b9-9f5c-d8828b4a4827";
    private static readonly string T2 = "7ae0480b-f93f-5b6d-83d0-dbb7eacb6910";
    private static readonly string T3 = "dc45f7b1-8b9e-559f-a5c3-7cf845a0ac75";

    [Fact]
    public void Stop_GivesTargetAndArrivedPose_AndEachWeldItsOutcome()
    {
        var log = new[]
        {
            $"[03:48:20.000] {Exec} VDA5050 order 수리: d22be5c9, node=df8829a9 → (13.829, 11.902, θ=2.368)",
            $"[03:48:26.354] {Exec} VDA5050 도착 대기 시작: 목표=(13.829, 11.902, θ=2.368), 허용 dxy≤0.200m, dθ≤0.100rad",
            $"[03:48:26.354] {Exec} VDA5050 도착 대기 중: pose=(11.917, 13.708, θ=-0.269), 잔여 dxy=2.629m",
            "   at Microsoft.EntityFrameworkCore.Update.Internal.BatchExecutor.ExecuteAsync()",
            $"[03:48:59.107] {Exec} VDA5050 허용 오차 진입 — 정차 대기: pose=(13.853, 11.894, θ=2.421), 잔여 dxy=0.025m, dθ=0.053rad",
            $"[03:49:03.273] {Exec} VDA5050 노드 도달: df8829a9 (seq=0)",
            Accept("03:49:03.274", "JOB-CT1-L4-T-L4-R03C17-1", T1),
            $"[03:51:59.595] {Exec} VDA5050 액션 완료: startWeldInspection (a2d28d9e) — recipe=LINE profile='LINE-1-COR-261109' anchor=X#1 jobRef=JOB-CT1-L4-T-L4-R03C17-1 taskId={T1}",
            Accept("03:51:59.596", "JOB-CT1-L4-T-L4-R03C17-2", T2, attempt: 2),
            $"[03:53:32.611] {Exec} VDA5050 액션 실패: startWeldInspection (8e96fcec) — recipe=LINE step=peak2Approach fail: 피치 이동 실패 (rc=112): 목표 자세 도달 불가 — 목표가 작업영역 안인지 확인.",
            Accept("03:53:32.613", "JOB-CT1-L4-T-L4-R03C17-3", T3, recipe: "CROSS4"),
            $"[03:56:33.678] {Exec} VDA5050 액션 실패: startWeldInspection (3a2eae2d) — recipe=CROSS4 step=cameraAlignRefine fail: 재정렬 1회차 접근 실패 — 광축 +31.5mm 이동했는데 거리가 332→348mm 로 기대만큼 변하지 않았습니다",
        };

        var r = AmrFieldLog.Parse(log, "2026-11-12");

        Assert.Empty(r.Warnings);
        Assert.Equal(3, r.Attempts.Count);
        var (a, b, c) = (r.Attempts[0], r.Attempts[1], r.Attempts[2]);
        Assert.Equal(("2026-11-12 03:49:03", "JOB-CT1-L4-T-L4-R03C17-1", Guid.Parse(T1), 1, "T", "LINE", FieldOutcome.Ok),
            (a.Time, a.JobRef, a.TaskId!.Value, a.Attempt!.Value, a.WallCode, a.Recipe, a.Outcome));
        Assert.Equal(new FieldPose(13.829, 11.902, 2.368), a.Target);
        Assert.Equal(new FieldPose(13.853, 11.894, 2.421), a.Arrived);   // 노드 도달 직전 마지막 보고 자세
        Assert.Equal("d22be5c9", a.OrderId);
        Assert.Equal((FieldOutcome.Reach, "peak2Approach", 2), (b.Outcome, b.Step, b.Attempt!.Value));
        Assert.Contains("rc=112", b.Reason);
        Assert.Equal((FieldOutcome.Sensor, "cameraAlignRefine", "CROSS4"), (c.Outcome, c.Step, c.Recipe));
        Assert.Equal(a.Arrived, c.Arrived);   // 같은 정차
    }

    [Fact]
    public void PrecheckBeforeDriving_FailsNamedWeld_AndMarksTheRestNotRun_WithTargetPoseOnly()
    {
        var log = new[]
        {
            // 앞선 지령에서 본 taskId 를 jobRef 로 이어 쓴다
            $"[02:10:00.000] {Exec} VDA5050 order 수리: aaaa, node=n0 → (19.000, 6.000, θ=-1.000)",
            $"[02:10:30.000] {Exec} VDA5050 노드 도달: n0 (seq=0)",
            Accept("02:10:30.001", "JOB-CT1-L4-T-L4-R10C11-1", T1, lift: 100),
            $"[02:10:31.000] {Pre} 코봇 도달 사전 점검 실패: 코봇 도달·순회 불가 — AMR 이동과 리프트 상승을 하지 않습니다. JOB-CT1-L4-T-L4-R10C11-1 끝: IK실패 (초점 IK 실패 — 역기구학(GetInverseKin) 실패: config 0–7 모두 도달 불가) — 차체 x=1309 y=-1312 z=2843 mm, 리프트 100 mm [컨트롤러 IK]",
            $"[02:10:31.001] {Exec} VDA5050 order 완결: aaaa (액션 3건)",

            $"[02:15:29.450] {Exec} VDA5050 order 수리: 27f4e6ca, node=b7ac1585 → (19.617, 6.211, θ=-1.035)",
            $"[02:15:29.451] {Orch} TASK 묶음 Leader: JOB-CT1-L4-T-L4-R10C11-1 bundle=c1952f10 wall=T lift=100 dir=Vertical",
            $"[02:15:29.451] {Orch} TASK 묶음 Follower: JOB-CT1-L4-T-L4-R10C11-2 bundle=c1952f10 wall=T lift=100 dir=Vertical",
            $"[02:15:30.684] {Pre} 코봇 도달 사전 점검 실패: 코봇 도달·순회 불가 — AMR 이동과 리프트 상승을 하지 않습니다. JOB-CT1-L4-T-L4-R10C11-1 시작: 뻗음부족 (플랜지 뻗음 342mm < 최소 350mm (IK·자세는 통과)) — 차체 x=439 y=440 z=2843 mm, 리프트 100 mm [컨트롤러 IK] | JOB-CT1-L4-T-L4-R10C11-1 끝: 뻗음부족 (플랜지 뻗음 342mm < 최소 350mm (IK·자세는 통과)) — 차체 x=439 y=80 z=2843 mm, 리프트 100 mm [컨트롤러 IK]",
            $"[02:15:30.684] [Warning    ] [HD.AMR.App.Service.Vda5050OrderExecutor] VDA5050 임무 실패: 27f4e6ca — 코봇 도달·순회 불가 — AMR 이동과 리프트 상승을 하지 않습니다.",
        };

        var r = AmrFieldLog.Parse(log);

        Assert.Empty(r.Warnings);
        Assert.Equal(3, r.Attempts.Count);
        // 도착한 뒤 그 용접선을 시작하면서 막힌 것 — 실제 정차는 없고(pose 줄 없음) 지령만 있다
        Assert.Equal((FieldOutcome.Reach, "precheck", 100.0), (r.Attempts[0].Outcome, r.Attempts[0].Step, r.Attempts[0].LiftMm));
        Assert.Contains("IK실패", r.Attempts[0].Reason);
        // 주행 전 거부 — 사유가 적힌 용접선은 규칙 거부(뻗음 부족), 같은 정차의 나머지는 실행 안 함. 도착 자세 없음.
        var (named, rest) = (r.Attempts[1], r.Attempts[2]);
        Assert.Equal((FieldOutcome.Rule, "precheck", Guid.Parse(T1)), (named.Outcome, named.Step, named.TaskId!.Value));
        Assert.Equal((FieldOutcome.NotRun, "JOB-CT1-L4-T-L4-R10C11-2", (Guid?)null), (rest.Outcome, rest.JobRef, rest.TaskId));
        Assert.Null(named.Arrived);
        Assert.Equal(new FieldPose(19.617, 6.211, -1.035), named.Target);
        Assert.Equal(100, rest.LiftMm);
    }

    [Fact]
    public void EmergencyStop_AndMissingResult_AreNotCountedAsReachFailures()
    {
        var log = new[]
        {
            $"[01:35:00.000] {Exec} VDA5050 order 수리: o1, node=n1 → (1.000, 2.000, θ=0.000)",
            $"[01:35:20.000] {Exec} VDA5050 노드 도달: n1 (seq=0)",
            Accept("01:35:20.001", "JOB-CT1-L4-T-A-1", T1),
            "[01:35:35.657] [Warning    ] [HD.AMR.App.Service.Vda5050AdapterService] 비상정지 이행 — 조작반 EMO 버튼",
            $"[01:40:00.000] {Exec} VDA5050 order 수리: o2, node=n2 → (3.000, 4.000, θ=0.000)",
            Accept("01:40:20.001", "JOB-CT1-L4-T-B-1", T2),
        };

        var r = AmrFieldLog.Parse(log);

        Assert.Equal(new[] { FieldOutcome.Operation, FieldOutcome.Unknown }, r.Attempts.Select(a => a.Outcome));
        Assert.Equal("비상정지", r.Attempts[0].Reason);
    }

    [Theory]
    [InlineData("cobotInspection", "[Cobot 검사위치 이동] 안전한 진입 경로 없음 — 움직이지 않았습니다.", FieldOutcome.Reach)]
    [InlineData("inspectionRunDry", "경유점 #27 앞 경로 [-655, 680, 1169] J6 안전 범위(-310°~-160°, 현재 감김 기준) 밖", FieldOutcome.Reach)]
    [InlineData("inspectionRun", "경유점 #5 앞 경로 [1, 2, 3] 도달 불가(관절 한계)", FieldOutcome.Reach)]
    [InlineData("bead1Center", "검사캠 시프트 이동 실패 (rc=112): 목표 자세 도달 불가", FieldOutcome.Reach)]
    [InlineData("precheck", "끝 자세한계 (초점 팔꿈치 특이점 (|J3| 4.8° < 여유 8°))", FieldOutcome.Reach)]
    [InlineData("precheck", "시작 뻗음부족 (플랜지 뻗음 342mm < 최소 350mm (IK·자세는 통과))", FieldOutcome.Rule)]
    [InlineData("flatSurfaceAlign", "[측정 불안정] 레이저 평면 측정 실패: 유효 샘플 부족 — CH1 0/20", FieldOutcome.Sensor)]
    [InlineData("laserWorkingDistance", "WD 미도달 — 잔차 12mm > 허용 5mm", FieldOutcome.Sensor)]
    [InlineData("peak2Center", "Peak 가 반대로 움직였습니다 (이동 전 -4.8 → -4.8mm)", FieldOutcome.Sensor)]
    [InlineData(null, "저전력으로 검사를 중단했습니다", FieldOutcome.Operation)]
    [InlineData("monitorOpen", "창을 열지 못했습니다", FieldOutcome.Other)]
    public void Classify_SeparatesReachFromSensorAndOperation(string? step, string reason, FieldOutcome expected) =>
        Assert.Equal(expected, AmrFieldLog.Classify(step, reason));
}
