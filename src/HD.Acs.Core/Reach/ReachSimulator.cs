using HD.Acs.Core.Geometry;

namespace HD.Acs.Core.Reach;

/// <summary>
/// 리치·간섭 시뮬레이션의 로봇 모델 — AMR 차체·리프트·FR5-WML·검사 툴. 길이 mm(간섭 여유만 m).
/// 링크 굵기는 캡슐 근사이고, BASE 는 차체 중심 위 <see cref="MountZMm"/> + 리프트 스트로크에 있다(STEP 장착, 축 평행).
/// 차체·신호탑·리프트 기둥은 차체 기준 상자(STEP E26003-E-A000 실측)로, 탱크 면과 팔(자기 간섭) 모두와 잰다.
/// </summary>
public sealed record ReachRobotModel
{
    public Fr5Kinematics Kinematics { get; init; } = Fr5Kinematics.Fr5Wml;
    /// <summary>플랜지 → TCP(검사 툴 초점) [mm]. 광축 = 플랜지 +Z.</summary>
    public double ToolZMm { get; init; } = 375;
    /// <summary>
    /// 검사 툴 외곽 — 플랜지 면에서 광축(+Z) 방향 구간별 원통(STEP E26003-E-A200 실측, 2026-11-01).
    /// 실물은 조명·레이저 막대가 한쪽(툴 −x)에 치우쳐 있지만 운영 J6 각을 모르므로 광축 둘레 회전 대칭으로 감싼다(보수적).
    /// </summary>
    public IReadOnlyList<ReachToolSegment> ToolSegments { get; init; } = new[]
    {
        new ReachToolSegment("검사 툴(브래킷·D435i)", -71, 56, 245),
        new ReachToolSegment("검사 툴(몸체·조명)", -15, 369, 234),
    };
    /// <summary>차체 바닥 → 코봇 BASE 면 높이(리프트 0) [mm].</summary>
    public double MountZMm { get; init; } = 1635;
    public double StrokeMaxMm { get; init; } = 1000;

    public double AmrFrontMm { get; init; } = 395;
    public double AmrRearMm { get; init; } = 365;
    public double AmrHalfWidthMm { get; init; } = 350;
    public double AmrHeightMm { get; init; } = 887;

    /// <summary>J2 축 방향 가로 오프셋 — 어깨(상완 위치)·팔꿈치 이후(전완 위치). DH d4 = 어깨 − 팔꿈치 + 손목.</summary>
    public double ShoulderOffsetMm { get; init; } = 185;
    public double ForearmOffsetMm { get; init; } = 45;

    /// <summary>리프트 기둥(CTD-LF144) 사각 단면 반폭 [mm] — 200×200, 차체 중심, 차체 윗면 → BASE.</summary>
    public double ColumnHalfMm { get; init; } = 100;
    /// <summary>차체 윗면 신호탑(SWTEL) — 후방 77~225 · 좌우 ±108 · 높이 933 [mm].</summary>
    public double TowerRearFromMm { get; init; } = 77;
    public double TowerRearToMm { get; init; } = 225;
    public double TowerHalfWidthMm { get; init; } = 108;
    public double TowerTopMm { get; init; } = 933;
    public double BaseRadiusMm { get; init; } = 80;
    public double ShoulderRadiusMm { get; init; } = 70;
    public double UpperArmRadiusMm { get; init; } = 60;
    public double ElbowRadiusMm { get; init; } = 55;
    public double ForearmRadiusMm { get; init; } = 50;
    public double WristRadiusMm { get; init; } = 45;

    /// <summary>관절 허용(J1~J5 대칭) [도] — J6 은 운영이 따로 맞추므로 판정하지 않는다.</summary>
    public double JointLimitDeg { get; init; } = 175;
    public double WristMarginDeg { get; init; } = 15;
    public double ElbowMarginDeg { get; init; } = 8;
    /// <summary>이 여유 미만이면 '주의' [m].</summary>
    public double WarnClearanceM { get; init; } = 0.05;

    /// <summary>접근 세 자세의 TCP 면 후퇴 [mm] — ② 거친 접근 · 정렬 준비(수평 맞춤 등) · 초점 검사(초점을 용접선 위에).</summary>
    public double[] StageStandoffsMm { get; init; } = { 400, 300, 0 };

    /// <summary>② 접근 자세의 IK 가지 고르기 — 운영(HD_AMR)과 같게 자세 여유 최대, 또는 간섭 여유 최대(대안 검토용).</summary>
    public ReachBranchRule BranchRule { get; init; } = ReachBranchRule.MaxMargin;

    public static ReachRobotModel Default { get; } = new();
}

/// <summary>검사 툴 외곽 원통 한 구간 — 플랜지 면 기준 광축 방향 [mm].</summary>
public sealed record ReachToolSegment(string Name, double FromMm, double ToMm, double RadiusMm);

/// <summary>② 접근 자세의 IK 가지 규칙. 뒤 자세는 어느 쪽이든 바로 앞 해에 가장 가까운 허용 해.</summary>
public enum ReachBranchRule
{
    /// <summary>자세 여유 최대 — HD_AMR 운영 규칙.</summary>
    MaxMargin,
    /// <summary>자세 한계 안에서 간섭 여유(탱크 면·차체·기둥) 최대 — 운영 가지가 간섭일 때 다른 가지로 되는지 본다.</summary>
    MaxClearance,
}

/// <summary>탱크 면(또는 데크) 평면 — Normal 은 내부향 단위벡터. 탱크 단면이 볼록이라 모든 평면 앞(+)이 곧 내부다.</summary>
public sealed record ReachPlane(string Code, double[] Origin, double[] Normal)
{
    public const string DeckCode = "DECK";
    public bool IsFloor => Code is "B" or DeckCode;
    public double SignedDistance(double[] p) => Vec3.Dot(Vec3.Sub(p, Origin), Normal);
}

/// <summary>정차 — 도면 프레임 차체 중심 (x,y)[m], 차체 전방 yaw[rad], 층 바닥 z[m].</summary>
public sealed record ReachStation(double X, double Y, double Yaw, double FloorZ);

public enum ReachStage { Approach, Align, Focus }

/// <summary>간섭 판정 부품(캡슐) — 끝점은 도면 m.</summary>
public sealed record ReachPart(string Name, double[] A, double[] B, double RadiusM, double ClearanceM, string NearestPlane);

/// <summary>관절 표시점(도면 m).</summary>
public sealed record ReachJointMark(string Name, double[] Position);

/// <summary>IK 해 하나 — 자세 여유와(요청 시) 부품 간섭.</summary>
public sealed record ReachSolution(
    double[] JointsDeg, bool PostureOk, double MarginDeg, string Limiting,
    IReadOnlyList<ReachPart> Parts, IReadOnlyList<ReachJointMark> Joints, double[] Focus,
    bool WristDown, double MinClearanceM);

/// <summary>자세 하나(② / 정렬 준비 / 초점)의 해 전부와 운영 선택.</summary>
public sealed record ReachStageResult(ReachStage Stage, double StandoffMm, double[] TcpTarget,
    IReadOnlyList<ReachSolution> Solutions, int ChosenIndex)
{
    public ReachSolution? Chosen => ChosenIndex >= 0 ? Solutions[ChosenIndex] : null;
}

/// <summary>AMR 차체 상자 — 꼭짓점 8개(아래 4 → 위 4, 도면 m)와 바닥 외 면까지 여유.</summary>
public sealed record ReachBody(double[][] Corners, double ClearanceM, string NearestPlane);

/// <summary>용접선 점 하나를 한 정차·리프트에서 세 자세로 평가한 결과.</summary>
public sealed record ReachPointResult(ReachBody Body, double[] ColumnTop, IReadOnlyList<ReachStageResult> Stages)
{
    /// <summary>세 자세 모두 운영 선택 해가 있고 자세 한계 안인가.</summary>
    public bool Reachable => Stages.All(s => s.Chosen is { PostureOk: true });

    /// <summary>차체·운영 선택 해 부품 중 가장 작은 여유 [m]. 도달하지 못하면 차체만.</summary>
    public double MinClearanceM => Stages.Select(s => s.Chosen?.MinClearanceM ?? double.PositiveInfinity)
        .Append(Body.ClearanceM).Min();
}

public enum ReachVerdict { Clear, Collision, Unreachable }

/// <summary>용접선 하나의 종합 판정 — 리프트를 훑어 간섭 없이 닿는 높이가 있으면 Clear.</summary>
/// <param name="StrokeMm">Clear 면 여유가 가장 큰 리프트, Collision 이면 간섭이 가장 덜한 리프트, 아니면 null.</param>
/// <param name="YawIndex">바닥·천장처럼 차체 방향이 자유일 때 고른 후보 순번.</param>
public sealed record ReachSeamSurvey(ReachVerdict Verdict, double? StrokeMm, double? MinClearanceM, int YawIndex);

/// <summary>
/// 리치·간섭 시뮬레이터. 정차·리프트·정차 이동이 주어지면 용접선 점마다 운영과 같은 세 접근 자세
/// (② 400 → 정렬 준비 300 → 초점 0, <see cref="ReachRobotModel.StageStandoffsMm"/>)를 FR5-WML 모델로 풀고,
/// 차체·리프트·코봇 링크·검사 툴 캡슐이 탱크 면에서 얼마나 떨어져 있는지 잰다.
///
/// 운영 선택 규칙: ② 는 자세 여유 최대 해(<see cref="ReachRobotModel.BranchRule"/> 로 간섭 여유 최대도 가능),
/// 그 뒤 자세는 바로 앞 자세 해에 가장 가까운 허용 해(같은 IK 가지로 연속 이동).
/// TOOL 은 광축(+Z)이 면 법선 반대(면을 바라봄)이고 광축 둘레 회전은 J6 만 바꾸므로 판정에 영향이 없다.
/// </summary>
public static class ReachSimulator
{
    /// <param name="seamPoint">용접선 점(도면 m).</param>
    /// <param name="wallNormal">면 내부향 단위 법선(도면).</param>
    /// <param name="offsetForwardMm">정차 이동 — 차체 전방(+ = 벽 쪽) [mm].</param>
    /// <param name="offsetLeftMm">정차 이동 — 차체 좌측 [mm].</param>
    /// <param name="withGeometry">false 면 운영 선택 해만 부품 간섭을 계산한다(전체 판정용 — 빠르다).</param>
    public static ReachPointResult Evaluate(
        ReachRobotModel model, IReadOnlyList<ReachPlane> planes, ReachStation station,
        double[] seamPoint, double[] wallNormal, double strokeMm,
        double offsetForwardMm = 0, double offsetLeftMm = 0, bool withGeometry = true)
    {
        var frame = new BaseFrame(model, station, strokeMm, offsetForwardMm, offsetLeftMm);
        var body = Body(model, planes, frame);
        var columnTop = frame.AmrToDrawing(0, 0, model.MountZMm + strokeMm);

        var stages = new List<ReachStageResult>(3);
        double[]? previous = null;
        var stageKinds = new[] { ReachStage.Approach, ReachStage.Align, ReachStage.Focus };
        for (var i = 0; i < model.StageStandoffsMm.Length && i < stageKinds.Length; i++)
        {
            var standoff = model.StageStandoffsMm[i];
            var tcp = Vec3.Add(seamPoint, Vec3.Scale(wallNormal, standoff / 1000.0));
            var raw = model.Kinematics.InverseFlange(FlangeTarget(model, frame, tcp, wallNormal));
            var evaluated = raw.Select(j => Normalize(model, j)).Select(j => (J: j, P: Posture(model, j))).ToList();

            var byClearance = previous is null && model.BranchRule == ReachBranchRule.MaxClearance;
            var solved = withGeometry || byClearance
                ? evaluated.Select(s => Solve(model, planes, frame, s.J, s.P)).ToList()
                : null;
            var chosen = previous is not null ? Nearest(evaluated, previous) ?? BestMargin(evaluated)
                : byClearance ? BestClearance(solved!) : BestMargin(evaluated);
            var sols = evaluated.Select((s, k) => solved?[k] ?? (k == chosen
                ? Solve(model, planes, frame, s.J, s.P)
                : new ReachSolution(s.J, s.P.Ok, s.P.Margin, s.P.Limiting, Array.Empty<ReachPart>(),
                    Array.Empty<ReachJointMark>(), tcp, false, double.PositiveInfinity))).ToList();
            stages.Add(new ReachStageResult(stageKinds[i], standoff, tcp, sols, chosen));
            if (chosen < 0) break;   // 운영도 이 자세에서 멈춘다 — 뒤 자세는 풀지 않는다
            previous = evaluated[chosen].J;
        }
        return new ReachPointResult(body, columnTop, stages);
    }

    /// <summary>정차 하나에서 AMR 차체(리프트·팔과 무관)의 면 여유만 잰다 — 정차 후보를 빠르게 거를 때 쓴다.</summary>
    public static ReachBody BodyAt(ReachRobotModel model, IReadOnlyList<ReachPlane> planes, ReachStation station) =>
        Body(model, planes, new BaseFrame(model, station, 0, 0, 0));

    /// <summary>
    /// 용접선(시작·중간·끝 점)을 정차 후보 방향·리프트 높이마다 평가해 간섭 없이 닿는 조합을 찾는다.
    /// </summary>
    public static ReachSeamSurvey Survey(
        ReachRobotModel model, IReadOnlyList<ReachPlane> planes, IReadOnlyList<ReachStation> stations,
        IReadOnlyList<double[]> seamPoints, double[] wallNormal, IReadOnlyList<double> strokesMm)
    {
        (double H, double Cl, int Yaw)? bestClear = null, bestHit = null;
        for (var yi = 0; yi < stations.Count; yi++)
            foreach (var h in strokesMm)
            {
                var min = double.PositiveInfinity;
                var reachable = true;
                foreach (var p in seamPoints)
                {
                    var r = Evaluate(model, planes, stations[yi], p, wallNormal, h, withGeometry: false);
                    if (!r.Reachable) { reachable = false; break; }
                    min = Math.Min(min, r.MinClearanceM);
                }
                if (!reachable) continue;
                if (min >= 0) { if (bestClear is null || min > bestClear.Value.Cl) bestClear = (h, min, yi); }
                else if (bestHit is null || min > bestHit.Value.Cl) bestHit = (h, min, yi);
            }
        if (bestClear is { } c) return new ReachSeamSurvey(ReachVerdict.Clear, c.H, c.Cl, c.Yaw);
        if (bestHit is { } b) return new ReachSeamSurvey(ReachVerdict.Collision, b.H, b.Cl, b.Yaw);
        return new ReachSeamSurvey(ReachVerdict.Unreachable, null, null, 0);
    }

    /// <summary>층의 장애물 평면 — 탱크 면 + (2층 이상) 그 층 데크면.</summary>
    public static IReadOnlyList<ReachPlane> PlanesForLevel(IEnumerable<ReachPlane> walls, double floorZ, int level)
    {
        var list = walls.ToList();
        if (level > 1) list.Add(new ReachPlane(ReachPlane.DeckCode, new[] { 0.0, 0, floorZ }, new[] { 0.0, 0, 1 }));
        return list;
    }

    // ── 내부 ─────────────────────────────────────────────────────────────

    /// <summary>차체 기준(mm, x 전방·y 좌측·z 바닥 위) ↔ 도면(m). BASE 는 차체 중심 위 마운트 + 스트로크.</summary>
    private sealed class BaseFrame
    {
        private readonly double _cos, _sin, _ox, _oy, _floor;
        public double BaseZMm { get; }

        public BaseFrame(ReachRobotModel m, ReachStation st, double strokeMm, double dF, double dL)
        {
            _cos = Math.Cos(st.Yaw); _sin = Math.Sin(st.Yaw);
            _ox = st.X + (_cos * dF - _sin * dL) / 1000.0;
            _oy = st.Y + (_sin * dF + _cos * dL) / 1000.0;
            _floor = st.FloorZ;
            BaseZMm = m.MountZMm + strokeMm;
        }

        public double[] AmrToDrawing(double x, double y, double z) =>
            new[] { _ox + (_cos * x - _sin * y) / 1000.0, _oy + (_sin * x + _cos * y) / 1000.0, _floor + z / 1000.0 };

        public double[] BaseToDrawing(double[] p) => AmrToDrawing(p[0], p[1], p[2] + BaseZMm);

        public double[] DrawingToBase(double[] p)
        {
            double dx = (p[0] - _ox) * 1000, dy = (p[1] - _oy) * 1000;
            return new[] { _cos * dx + _sin * dy, -_sin * dx + _cos * dy, (p[2] - _floor) * 1000 - BaseZMm };
        }

        public double[] DirectionToBase(double[] v) => new[] { _cos * v[0] + _sin * v[1], -_sin * v[0] + _cos * v[1], v[2] };
    }

    private static double[,] FlangeTarget(ReachRobotModel m, BaseFrame f, double[] tcpDrawing, double[] wallNormal)
    {
        var z = Vec3.Scale(f.DirectionToBase(wallNormal), -1);   // 광축 = 면을 바라봄
        var x = Vec3.Normalize(Vec3.Cross(new[] { 0.0, 0, 1 }, z)) ?? new[] { 1.0, 0, 0 };
        var y = Vec3.Cross(z, x);
        var tcp = f.DrawingToBase(tcpDrawing);
        return Mat4.FromAxes(x, y, z, Vec3.Sub(tcp, Vec3.Scale(z, m.ToolZMm)));
    }

    private static double[] Normalize(ReachRobotModel m, double[] j) => j.Select(v =>
        v < -m.JointLimitDeg || v > m.JointLimitDeg
            ? new[] { v - 360, v + 360 }.FirstOrDefault(c => c >= -m.JointLimitDeg && c <= m.JointLimitDeg, v)
            : v).ToArray();

    private readonly record struct PostureEval(bool Ok, double Margin, string Limiting);

    /// <summary>HD_AMR PostureLimits.Evaluate 와 같은 여유(J6 제외, 어깨 특이점 생략).</summary>
    private static PostureEval Posture(ReachRobotModel m, double[] j)
    {
        var slack = double.PositiveInfinity;
        var why = "여유 있음";
        for (var i = 0; i < 5; i++)
        {
            var s = Math.Min(j[i] + m.JointLimitDeg, m.JointLimitDeg - j[i]);
            if (s < slack) { slack = s; why = $"J{i + 1} 관절한계"; }
        }
        var wrist = Math.Abs(j[4]) - m.WristMarginDeg;
        if (wrist < slack) { slack = wrist; why = "손목 특이점(|J5| 작음)"; }
        var elbow = Math.Abs(j[2]) - m.ElbowMarginDeg;
        if (elbow < slack) { slack = elbow; why = "팔꿈치 특이점(|J3| 작음)"; }
        return new PostureEval(slack > 0, slack, slack > 0 ? "여유 있음" : why);
    }

    private static int BestMargin(List<(double[] J, PostureEval P)> sols)
    {
        var best = -1;
        for (var i = 0; i < sols.Count; i++)
            if (best < 0 || sols[i].P.Margin > sols[best].P.Margin) best = i;
        return best;
    }

    /// <summary>자세 한계 안 해 중 간섭 여유 최대(없으면 자세 여유 최대).</summary>
    private static int BestClearance(List<ReachSolution> sols)
    {
        var best = -1;
        for (var i = 0; i < sols.Count; i++)
            if (sols[i].PostureOk && (best < 0 || sols[i].MinClearanceM > sols[best].MinClearanceM)) best = i;
        if (best >= 0) return best;
        for (var i = 0; i < sols.Count; i++)
            if (best < 0 || sols[i].MarginDeg > sols[best].MarginDeg) best = i;
        return best;
    }

    private static int? Nearest(List<(double[] J, PostureEval P)> sols, double[] seed)
    {
        int? best = null;
        var bestD = double.PositiveInfinity;
        for (var i = 0; i < sols.Count; i++)
        {
            if (!sols[i].P.Ok) continue;
            var d = Enumerable.Range(0, 5).Max(k => Math.Abs(sols[i].J[k] - seed[k]));
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    private static ReachSolution Solve(ReachRobotModel m, IReadOnlyList<ReachPlane> planes, BaseFrame f, double[] j, PostureEval posture)
    {
        var fr = m.Kinematics.Frames(j);
        double[] O(int i) => Mat4.Origin(fr[i]);
        double[] Along(double[] p, double[] axis, double mm) => Vec3.Add(p, Vec3.Scale(axis, mm));
        var z1 = Mat4.AxisZ(fr[1]);
        var z6 = Mat4.AxisZ(fr[6]);
        var s1 = Along(O(1), z1, m.ShoulderOffsetMm);
        var e1 = Along(O(2), z1, m.ShoulderOffsetMm);
        var e2 = Along(O(2), z1, m.ForearmOffsetMm);
        var w1 = Along(O(3), z1, m.ForearmOffsetMm);
        double[] B(double[] p) => f.BaseToDrawing(p);

        var self = SelfBoxes(m, f.BaseZMm);
        ReachPart Arm(string name, double[] a, double[] b, double radiusMm, bool nearColumn = false) =>
            WithSelf(Part(name, B(a), B(b), radiusMm, planes), a, b, radiusMm, f.BaseZMm, nearColumn ? self[..2] : self);

        var parts = new[]
        {
            Column(m, planes, f),
            Arm("BASE·J1", O(0), O(1), m.BaseRadiusMm, nearColumn: true),
            Arm("어깨(J2)", O(1), s1, m.ShoulderRadiusMm, nearColumn: true),
            Arm("상완", s1, e1, m.UpperArmRadiusMm),
            Arm("팔꿈치(J3)", e1, e2, m.ElbowRadiusMm),
            Arm("전완", e2, w1, m.ForearmRadiusMm),
            Arm("손목1(J4)", w1, O(4), m.WristRadiusMm),
            Arm("손목2(J5)", O(4), O(5), m.WristRadiusMm),
            Arm("손목3(J6)", O(5), O(6), m.WristRadiusMm),
        }.Concat(m.ToolSegments.Select(t =>
        {
            var a = Along(O(6), z6, t.FromMm);
            var b = Along(O(6), z6, t.ToMm);
            return WithSelf(CylinderPart(t.Name, B(a), B(b), t.RadiusMm, planes), a, b, t.RadiusMm, f.BaseZMm, self);
        })).ToArray();
        var joints = new[]
        {
            new ReachJointMark("J1", B(O(0))),
            new ReachJointMark("J2", B(Along(O(1), z1, 100))),
            new ReachJointMark("J3", B(Along(O(2), z1, 115))),
            new ReachJointMark("J4", B(Along(O(3), z1, 100))),
            new ReachJointMark("J5", B(O(4))),
            new ReachJointMark("J6", B(O(5))),
            new ReachJointMark("플랜지", B(O(6))),
        };
        // J5 몸체(J4→J5)가 아래를 향하나 — 실기에서 천장 간섭을 피하려고 J4 를 아래로 돌린 자세 구분용.
        var wristDown = O(5)[2] < O(4)[2];
        return new ReachSolution(j, posture.Ok, posture.Margin, posture.Limiting, parts, joints,
            B(Along(O(6), z6, m.ToolZMm)), wristDown, parts.Min(p => p.ClearanceM));
    }

    /// <summary>차체 기준 상자(mm, x 전방·y 좌측·z 바닥 위).</summary>
    private readonly record struct SelfBox(string Name, double[] Min, double[] Max);

    /// <summary>자기 간섭 상자 — 신호탑·차체·리프트 기둥(차체 윗면 → BASE). 앞 둘은 BASE·어깨도 잰다(기둥에 붙은 부품).</summary>
    private static SelfBox[] SelfBoxes(ReachRobotModel m, double baseZMm) => new[]
    {
        new SelfBox("AMR 차체", new[] { -m.AmrRearMm, -m.AmrHalfWidthMm, 0 }, new[] { m.AmrFrontMm, m.AmrHalfWidthMm, m.AmrHeightMm }),
        new SelfBox("신호탑", new[] { -m.TowerRearToMm, -m.TowerHalfWidthMm, m.AmrHeightMm },
            new[] { -m.TowerRearFromMm, m.TowerHalfWidthMm, m.TowerTopMm }),
        new SelfBox("리프트 기둥", new[] { -m.ColumnHalfMm, -m.ColumnHalfMm, m.AmrHeightMm },
            new[] { m.ColumnHalfMm, m.ColumnHalfMm, baseZMm }),
    };

    /// <summary>리프트 기둥 — 사각 기둥 꼭짓점으로 탱크 면 여유를 잰다(그림은 반폭 반경 캡슐).</summary>
    private static ReachPart Column(ReachRobotModel m, IReadOnlyList<ReachPlane> planes, BaseFrame f)
    {
        var h = m.ColumnHalfMm;
        var corners = new[] { m.AmrHeightMm, f.BaseZMm }
            .SelectMany(z => new[] { (h, h), (h, -h), (-h, h), (-h, -h) }.Select(c => f.AmrToDrawing(c.Item1, c.Item2, z)))
            .ToList();
        var (cl, code) = Clearance(corners, 0, planes, skipFloor: true);
        return new ReachPart("리프트 기둥", f.AmrToDrawing(0, 0, m.AmrHeightMm), f.AmrToDrawing(0, 0, f.BaseZMm), h / 1000.0, cl, code);
    }

    /// <summary>팔 부품의 탱크 면 여유와 차체·신호탑·기둥 여유 중 작은 쪽. a·b 는 BASE 기준 mm.</summary>
    private static ReachPart WithSelf(ReachPart tank, double[] a, double[] b, double radiusMm, double baseZMm, SelfBox[] boxes)
    {
        var pa = new[] { a[0], a[1], a[2] + baseZMm };
        var pb = new[] { b[0], b[1], b[2] + baseZMm };
        var best = tank;
        foreach (var box in boxes)
        {
            var cl = (SegmentBoxDistanceMm(pa, pb, box.Min, box.Max) - radiusMm) / 1000.0;
            if (cl < best.ClearanceM) best = best with { ClearanceM = cl, NearestPlane = box.Name };
        }
        return best;
    }

    /// <summary>선분–축정렬 상자 최단거리 [mm]. 선분 위 점에서 상자까지 거리는 볼록이라 삼분 탐색으로 찾는다.</summary>
    public static double SegmentBoxDistanceMm(double[] a, double[] b, double[] min, double[] max)
    {
        double At(double t)
        {
            var s = 0.0;
            for (var i = 0; i < 3; i++)
            {
                var p = a[i] + (b[i] - a[i]) * t;
                var d = p < min[i] ? min[i] - p : p > max[i] ? p - max[i] : 0;
                s += d * d;
            }
            return Math.Sqrt(s);
        }
        double lo = 0, hi = 1;
        for (var k = 0; k < 40 && hi - lo > 1e-4; k++)
        {
            double m1 = lo + (hi - lo) / 3, m2 = hi - (hi - lo) / 3;
            if (At(m1) <= At(m2)) hi = m2; else lo = m1;
        }
        return Math.Min(At((lo + hi) / 2), Math.Min(At(0), At(1)));
    }

    /// <summary>
    /// 끝이 평평한 원통(검사 툴)의 면 여유 — 테두리 원의 가장 가까운 점 = 중심 거리 − r·sin(축–법선 각).
    /// 광축이 면 법선과 나란한 대상 면에서는 반경이 여유를 줄이지 않는다(캡슐로 두면 끝이 둥글게 r 만큼 더 나간다).
    /// </summary>
    private static ReachPart CylinderPart(string name, double[] a, double[] b, double radiusMm, IReadOnlyList<ReachPlane> planes)
    {
        var axis = Vec3.Normalize(Vec3.Sub(b, a)) ?? new[] { 0.0, 0, 1 };
        var best = double.PositiveInfinity;
        var code = "";
        foreach (var pl in planes)
        {
            var c = Vec3.Dot(axis, pl.Normal);
            var rim = radiusMm / 1000.0 * Math.Sqrt(Math.Max(0, 1 - c * c));
            var d = Math.Min(pl.SignedDistance(a), pl.SignedDistance(b)) - rim;
            if (d < best) { best = d; code = pl.Code; }
        }
        return new ReachPart(name, a, b, radiusMm / 1000.0, best, code);
    }

    private static ReachPart Part(string name, double[] a, double[] b, double radiusMm, IReadOnlyList<ReachPlane> planes, bool skipFloor = false)
    {
        var (cl, code) = Clearance(new[] { a, b }, radiusMm / 1000.0, planes, skipFloor);
        return new ReachPart(name, a, b, radiusMm / 1000.0, cl, code);
    }

    /// <summary>점 집합(선분 끝점·상자 꼭짓점)의 평면 여유 최소 — 평면까지 거리는 선형이라 끝점만 보면 된다.</summary>
    private static (double Clearance, string Code) Clearance(IEnumerable<double[]> points, double radiusM,
        IReadOnlyList<ReachPlane> planes, bool skipFloor)
    {
        var best = double.PositiveInfinity;
        var code = "";
        foreach (var pl in planes)
        {
            if (skipFloor && pl.IsFloor) continue;
            foreach (var p in points)
            {
                var d = pl.SignedDistance(p) - radiusM;
                if (d < best) { best = d; code = pl.Code; }
            }
        }
        return (best, code);
    }

    private static ReachBody Body(ReachRobotModel m, IReadOnlyList<ReachPlane> planes, BaseFrame f)
    {
        var corners = new List<double[]>(8);
        foreach (var z in new[] { 0.0, m.AmrHeightMm })
            foreach (var (x, y) in new[] { (m.AmrFrontMm, -m.AmrHalfWidthMm), (m.AmrFrontMm, m.AmrHalfWidthMm),
                                            (-m.AmrRearMm, m.AmrHalfWidthMm), (-m.AmrRearMm, -m.AmrHalfWidthMm) })
                corners.Add(f.AmrToDrawing(x, y, z));
        var (cl, code) = Clearance(corners, 0, planes, skipFloor: true);
        return new ReachBody(corners.ToArray(), cl, code);
    }
}
