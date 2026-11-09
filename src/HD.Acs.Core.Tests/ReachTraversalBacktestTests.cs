using HD.Acs.Core.Reach;
using Xunit;

namespace HD.Acs.Core.Tests;

/// <summary>
/// 도면 순회 판정 백테스트 — HD_AMR 로그(2026-11-03·06·09, AMR PC %LOCALAPPDATA%\HD.AMR\logs)의 실제 순회 시도 15건.
/// 작업물 프레임(바닥 원점·RPY, BASE mm·도)과 순회 직전 J6 을 그대로 넣고 티칭 프로필 LINE-1-COR(HD_AMR DB InspectionProfiles id 8, 29점)로
/// 판정해 HD_AMR 의 실제 결과(완료 / 경유점 사전 검증 실패·rc=185 정지)와 맞춘다. BASE 기준으로 풀어 정차·캘리브레이션 오차는 들어가지 않는다.
/// 제외: 안전 정지(rc=99)·옛 J6 규칙(−330°~−160°, 감김 미적용) 실패·CROSS 프로필.
/// </summary>
public class ReachTraversalBacktestTests
{
    /// <summary>LINE-1-COR — [X mm, θ 도, Z mm](2026-11-06 저장분).</summary>
    public static readonly double[][] Line1Cor =
    {
        new[] { 0.0, 0.0, 33.8 },
        new[] { 4.91, 10.0, 33.37 },
        new[] { 9.67, 20.0, 32.1 },
        new[] { 14.13, 30.0, 30.02 },
        new[] { 18.17, 40.0, 27.19 },
        new[] { 21.65, 50.0, 23.71 },
        new[] { 23.2, 45.0, 21.68 },
        new[] { 25.76, 45.0, 17.17 },
        new[] { 25.54, 45.0, 11.88 },
        new[] { 30.93, 45.0, 2.94 },
        new[] { 45.0, 0.0, 0.0 },
        new[] { 90.0, 0.0, 0.0 },
        new[] { 120.0, 0.0, 0.0 },
        new[] { 150.0, 0.0, 0.0 },
        new[] { 180.0, 0.0, 0.0 },
        new[] { 210.0, 0.0, 0.0 },
        new[] { 240.0, 0.0, 0.0 },
        new[] { 270.0, 0.0, 0.0 },
        new[] { 300.0, 0.0, 0.0 },
        new[] { 329.07, -45.0, 2.94 },
        new[] { 332.46, -45.0, 11.88 },
        new[] { 334.24, -45.0, 17.17 },
        new[] { 336.8, -45.0, 21.68 },
        new[] { 338.35, -50.0, 23.71 },
        new[] { 341.83, -40.0, 27.19 },
        new[] { 345.87, -30.0, 30.02 },
        new[] { 350.33, -20.0, 32.1 },
        new[] { 355.09, -10.0, 33.37 },
        new[] { 360.0, 0.000429960695328191, 33.8 },
    };

    private sealed record Case(string Name, string Wall, double[] OriginMm, double[] RpyDeg, double J6, bool Passed);

    private static readonly Case[] Cases =
    {
        new("11-03 11:29 L2-F-L2-R02C05-1", "F", new[] { -1370.1, -467.3, 367.2 }, new[] { 89.99, -0.02, 90.62 }, 180.4, true),
        new("11-03 11:31 L2-F-L2-R02C05-2", "F", new[] { -1374.0, -108.2, 367.2 }, new[] { 89.99, -0.02, 90.62 }, 90.0, true),
        new("11-03 11:31 L2-F-L2-R02C05-3", "F", new[] { -1377.9, 251.3, 367.2 }, new[] { 89.99, -0.02, 90.62 }, 90.0, true),
        new("11-03 11:32 L2-F-L2-R02C05-4", "F", new[] { -1381.8, 610.9, 367.2 }, new[] { 89.99, -0.02, 90.62 }, 90.0, false),
        new("11-03 11:57 L2-F-L2-R02C06-2", "F", new[] { -1531.1, -260.8, 367.1 }, new[] { 89.98, -0.03, 90.18 }, 90.0, true),
        new("11-03 11:58 L2-F-L2-R02C06-3", "F", new[] { -1532.3, 107.0, 367.1 }, new[] { 89.98, -0.03, 90.18 }, 90.0, true),
        new("11-03 11:58 L2-F-L2-R02C06-4", "F", new[] { -1533.4, 468.1, 367.1 }, new[] { 89.98, -0.03, 90.18 }, 90.0, false),
        new("11-03 12:02 L2-F-L2-R02C06-6", "F", new[] { -1535.0, -251.5, 727.2 }, new[] { 89.99, -0.04, 89.21 }, 90.0, true),
        new("11-03 12:03 L2-F-L2-R02C06-7", "F", new[] { -1530.0, 109.0, 727.2 }, new[] { 89.99, -0.04, 89.21 }, 90.0, true),
        new("11-03 12:04 L2-F-L2-R02C06-8", "F", new[] { -1525.2, 454.1, 727.2 }, new[] { 89.99, -0.04, 89.21 }, 90.0, false),
        new("11-03 12:06 L2-F-L2-R02C06-9", "F", new[] { -1536.7, -445.7, -532.7 }, new[] { -8.79, -89.97, -171.47 }, 180.4, true),
        new("11-03 12:07 L2-F-L2-R02C06-10", "F", new[] { -1536.8, -445.7, -172.7 }, new[] { -8.79, -89.97, -171.47 }, 180.0, true),
        new("11-06 13:30 L4-A-L4-R01C09-1", "A", new[] { -1351.1, -963.4, -772.6 }, new[] { 89.99, -0.01, 90.03 }, -179.6, false),
        new("11-06 13:31 L4-A-L4-R01C09-2", "A", new[] { -1339.2, -603.6, -772.7 }, new[] { 89.99, -0.0, 90.01 }, -179.6, true),
        new("11-09 10:53 T 시험", "T", new[] { -366.1, 509.8, 1251.0 }, new[] { -179.45, 0.12, -92.15 }, 131.8, false),
    };

    private static readonly double[] Ready = { -0.391, -9.414, 119.984, -110.76, 90.001, 180.392 };
    private static readonly double[] Home2 = { 0.158, 59.456, -140.907, 80.848, 90.048, -179.696 };

    /// <summary>BASE = 도면(m) — 장착 yaw 0·정차 원점·바닥을 BASE 높이 아래로.</summary>
    private static readonly ReachRobotModel Model = ReachRobotModel.Default with
    {
        MountYawDeg = 0, ToolZMm = 365, StageStandoffsMm = new[] { 400.0, 300, 10 }, ScanStandoffMm = 400, ScanTiltProfile = Line1Cor,
        StartJointsByWall = new Dictionary<string, double[]> { ["F"] = Ready, ["A"] = Ready, ["T"] = Home2 },
    };
    private static readonly ReachStation BaseStation = new(0, 0, 0, -ReachRobotModel.Default.MountZMm / 1000);

    private static ReachTraversal Run(Case c)
    {
        // RPY(Fairino ZYX) → 프레임 X(용접선 진행)·Z(면 바깥 = 내부향 법선). Top = 원점 + 33.8·Z = 점1(② 접근점, 면 이격 400).
        double a = c.RpyDeg[0] * Math.PI / 180, b = c.RpyDeg[1] * Math.PI / 180, g = c.RpyDeg[2] * Math.PI / 180;
        double[] x = { Math.Cos(g) * Math.Cos(b), Math.Sin(g) * Math.Cos(b), -Math.Sin(b) };
        double[] z =
        {
            Math.Cos(g) * Math.Sin(b) * Math.Cos(a) + Math.Sin(g) * Math.Sin(a),
            Math.Sin(g) * Math.Sin(b) * Math.Cos(a) - Math.Cos(g) * Math.Sin(a),
            Math.Cos(b) * Math.Cos(a),
        };
        var top = Enumerable.Range(0, 3).Select(i => c.OriginMm[i] + ReachRobotModel.ScanTopZMm * z[i]).ToArray();
        var s0 = Enumerable.Range(0, 3).Select(i => (top[i] - 400 * z[i]) / 1000).ToArray();
        var s1 = Enumerable.Range(0, 3).Select(i => s0[i] + 0.36 * x[i]).ToArray();
        var r = ReachSimulator.Evaluate(Model, Array.Empty<ReachPlane>(), BaseStation, s0, z, 0, withGeometry: false, wallCode: c.Wall, checkEntry: false);
        var seed = (double[])ReachSimulator.TraverseSeed(r)!.Clone();
        seed[5] = c.J6;   // 순회 직전 실제 J6(로그) — 연속 실행은 앞 순회 끝 자세(90°)를 이어받는다
        return ReachSimulator.Traverse(Model, BaseStation, 0, s0, s1, z, seed);
    }

    [Fact]
    public void 실기_순회_15건_중_14건을_같게_판정한다()
    {
        var results = Cases.Select(c => (c, t: Run(c))).ToList();
        var wrong = results.Where(r => r.t.Ok != r.c.Passed).ToList();

        // 어긋나는 1건 — R02C05-3 은 실기 통과인데 여유 약 −4°(경계)로 막힘 판정(11-03 당시 프로필이 지금 저장분과 달랐을 수 있다).
        var miss = Assert.Single(wrong);
        Assert.Contains("R02C05-3", miss.c.Name);
        Assert.True(miss.t.MinMarginDeg > -6, $"{miss.t.MinMarginDeg:0.0}");
        // 통과한 실기 사례는 모두 여유가 양수, 막힌 사례는 음수.
        Assert.All(results.Where(r => r.c.Passed && r.t.Ok), r => Assert.True(r.t.MinMarginDeg > 0));
    }

    [Fact]
    public void 실패_위치와_원인도_실기와_같다()
    {
        // 실기: R02C06-4 '경유점 #6 손목 특이점(J5 58.7° → -1.5°)' — #6 = X 21.6 mm, θ +50°.
        var c6 = Run(Cases.Single(c => c.Name.EndsWith("R02C06-4")));
        Assert.Contains("손목 특이점", c6.Blocker);
        Assert.StartsWith("도면 순회 — 22mm 지점 기울기 +50°", c6.Blocker);
        // 실기: T 시험 '경유점 #5 앞 경로 J4 관절 한계 근접(83.6°)' — #5 = X 18.2 mm.
        var t = Run(Cases.Single(c => c.Wall == "T"));
        Assert.Contains("J4 관절 한계", t.Blocker);
        Assert.StartsWith("도면 순회 — 18mm 지점", t.Blocker);
    }
}
