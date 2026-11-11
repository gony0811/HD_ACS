using HD.Acs.Core.Geometry;

namespace HD.Acs.Core.Reach;

/// <summary>도면 순회 판정 자세 하나 — 용접선 시작점에서의 거리·광축 기울기·TCP(도면 m)·관절각·이 자세의 관절 여유.</summary>
public sealed record ReachTraversalPoint(double AlongMm, double TiltDeg, double[] Tcp, double[] JointsDeg, double MarginDeg, string Limiting);

/// <summary>
/// 도면 순회(⑱ inspectionRun) 판정 — HD_AMR 이 용접선 시작점 정렬 뒤 티칭 프로필 경유점을 도는 동안 관절이 이어지는가.
/// </summary>
/// <param name="Ok">모든 판정 자세가 HD_AMR 경로 검증(관절 한계·관절 튐·팔꿈치·손목 특이점·J6 안전 범위)을 통과.</param>
/// <param name="Blocker">처음 막히는 자세의 사유(통과면 null).</param>
/// <param name="MinMarginDeg">전 판정 자세의 가장 빡빡한 여유 [도] — 관절 한계(−2° 여유)·|J5|−5°·J6 안전 범위 끝까지 중 최소. 해가 없으면 −∞.</param>
/// <param name="Limiting">그 최소 여유를 만든 항목.</param>
/// <param name="TwistDeg">고른 광축 둘레 비틀림(0 또는 180°) — HD_AMR SelectSafeTwist 와 같은 규칙.</param>
/// <param name="Worst">최소 여유 자세.</param>
public sealed record ReachTraversal(bool Ok, string? Blocker, double MinMarginDeg, string Limiting, double TwistDeg,
    ReachTraversalPoint? Worst, IReadOnlyList<ReachTraversalPoint> Points);

public static partial class ReachSimulator
{
    /// <summary>
    /// 용접선 도면 순회를 판정한다 — HD_AMR <c>InspectionRunStep</c> 의 움직이기 전 경유점 사슬 검증과 같은 규칙.
    ///
    /// 자세 모델(실기 2026-11-03 L2-R02C05 H04 경유점 #5 관절값을 이 식으로 풀어 2° 안에서 재현):
    /// TCP = 용접선 점 + 면 내부향 법선 × <see cref="ReachRobotModel.ScanStandoffMm"/>, 광축(플랜지 +Z) = 면을 바라봄을
    /// 용접선에 수직인 면 내 축 a(= 법선 × 진행 방향) 둘레로 θ 만큼 기울인 것, 플랜지 X = a(검사 공구 #1 은 플랜지 대비 rz 90° 라
    /// 공구 X 가 용접선 방향 — HD_AMR 절대 모드 경유점 규약). 비틀림 180° 는 같은 위치·광축에서 J6 만 180° 다르다.
    ///
    /// 티칭 프로필 경유점을 ACS 가 모르므로 포괄 근사로 본다 — 용접선을 <see cref="ReachRobotModel.ScanSampleMm"/> 간격으로 나눈 점마다
    /// 기울기 0 자세를 앞 점에서 이어 풀고, 그 자세에서 ±<see cref="ReachRobotModel.ScanTiltDeg"/> 까지
    /// <see cref="ReachRobotModel.ScanTiltStepDeg"/> 간격으로 기울여 본다.
    /// 해는 바로 앞 자세에 가장 가까운 해(HD_AMR GetInverseKinNearest), 손목·팔꿈치 가지와 J6 감김 기준은 순회 직전 자세
    /// <paramref name="seed"/>(용접선 시작점 초점 자세 해)다.
    /// </summary>
    /// <param name="seed">순회 직전 관절각 J1~J6 [도] — 보통 <see cref="ScanSeed"/>(시작점 초점 자세).</param>
    public static ReachTraversal Traverse(
        ReachRobotModel m, ReachStation station, double strokeMm, double[] seamStart, double[] seamEnd, double[] wallNormal,
        double[] seed, double offsetForwardMm = 0, double offsetLeftMm = 0)
    {
        var f = new BaseFrame(m, station, strokeMm, offsetForwardMm, offsetLeftMm);
        var n = Vec3.Normalize(wallNormal) ?? new[] { 0.0, 0, 1 };
        var span = Vec3.Sub(seamEnd, seamStart);
        var len = Vec3.Length(span);
        var dir = len > 1e-6 ? Vec3.Scale(span, 1 / len)
            : Vec3.Normalize(Vec3.Cross(new[] { 0.0, 0, 1 }, n)) ?? new[] { 1.0, 0, 0 };
        var axis = Vec3.Normalize(Vec3.Cross(n, dir)) ?? Vec3.Normalize(Vec3.Cross(n, new[] { 1.0, 0, 0 })) ?? new[] { 0.0, 1, 0 };
        var look = Vec3.Scale(n, -1);
        var standoff = Vec3.Scale(n, m.ScanStandoffMm / 1000.0);

        // dzMm = 프레임 Top(점1 = ② 접근점) 대비 면 법선 방향 TCP 변위 — 경유점 Z − 33.8(코러게이션 바닥 = −33.8, 면 쪽).
        (double[] Tcp, double[,] Target) Pose(double alongMm, double tiltDeg, double dzMm)
        {
            var t = tiltDeg * Math.PI / 180;
            var z = Vec3.Add(Vec3.Scale(look, Math.Cos(t)), Vec3.Scale(Vec3.Cross(axis, look), Math.Sin(t)));
            var tcp = Vec3.Add(Vec3.Add(seamStart, Vec3.Scale(dir, alongMm / 1000.0)), Vec3.Add(standoff, Vec3.Scale(n, dzMm / 1000.0)));
            var zb = f.DirectionToBase(z);
            var xb = f.DirectionToBase(axis);
            var yb = Vec3.Cross(zb, xb);
            var tcpB = f.DrawingToBase(tcp);
            return (tcp, Mat4.FromAxes(xb, yb, zb, Vec3.Sub(tcpB, Vec3.Scale(zb, m.ToolZMm))));
        }

        // 판정 자세 순서 — (거리, 기울기, 이어 풀 기준 자세 번호). 기울기 0 자세는 앞 점 0 자세에서, 기울인 자세는 바로 앞 기울기에서 잇는다.
        var steps = Math.Max(1, (int)Math.Ceiling(len * 1000 / m.ScanSampleMm - 1e-9));
        var tiltSteps = m.ScanTiltDeg > 0 && m.ScanTiltStepDeg > 0 ? (int)Math.Ceiling(m.ScanTiltDeg / m.ScanTiltStepDeg - 1e-9) : 0;
        var plan = new List<(double Along, double Tilt, double Dz, int From)>();
        var lastMain = -1;
        if (m.ScanTiltProfile is { Count: > 0 } profile)
        {
            // 실제 경유점(거리, 기울기[, Z])이 있으면 그 순서대로 잇는다 — 사이는 판정 간격(거리·기울기)으로 나눠 경로를 본다.
            (double A, double T, double D) Wp(double[] v) => (v[0], v[1], v.Length >= 3 ? v[2] - ReachRobotModel.ScanTopZMm : 0);
            var prevWp = Wp(profile[0]);
            plan.Add((prevWp.A, prevWp.T, prevWp.D, -1));
            for (var w = 1; w < profile.Count; w++)
            {
                var wp = Wp(profile[w]);
                var sub = Math.Max(1, (int)Math.Ceiling(Math.Max(
                    Math.Abs(wp.A - prevWp.A) / m.ScanSampleMm,
                    m.ScanTiltStepDeg > 0 ? Math.Abs(wp.T - prevWp.T) / m.ScanTiltStepDeg : 0) - 1e-9));
                for (var s = 1; s <= sub; s++)
                    plan.Add((prevWp.A + (wp.A - prevWp.A) * s / sub, prevWp.T + (wp.T - prevWp.T) * s / sub,
                        prevWp.D + (wp.D - prevWp.D) * s / sub, plan.Count - 1));
                prevWp = wp;
            }
        }
        else
        for (var k = 0; k <= (len > 1e-6 ? steps : 0); k++)
        {
            var along = len * 1000 * k / steps;
            plan.Add((along, 0, 0, lastMain));
            var main = plan.Count - 1;
            lastMain = main;
            foreach (var sign in new[] { 1, -1 })
            {
                var from = main;
                for (var s = 1; s <= tiltSteps; s++)
                {
                    plan.Add((along, sign * Math.Min(m.ScanTiltDeg, s * m.ScanTiltStepDeg), 0, from));
                    from = plan.Count - 1;
                }
            }
        }

        // J1~J5 해 — 기준 자세마다 가장 가까운 해(J6 은 비틀림을 고른 뒤 감는다).
        var sols = new double[plan.Count][];
        var tcps = new double[plan.Count][];
        string? unreachable = null;
        var reached = 0;
        for (var i = 0; i < plan.Count; i++)
        {
            var (along, tilt, dz, from) = plan[i];
            var reference = from < 0 ? seed : sols[from];
            var (tcp, target) = Pose(along, tilt, dz);
            tcps[i] = tcp;
            var j = NearestWound(m, m.Kinematics.InverseFlange(target), reference);
            if (j is null) { unreachable = $"도면 순회 — {Where(along, tilt)} 도달 불가(관절 범위 안 해 없음)"; break; }
            sols[i] = j;
            reached = i + 1;
        }

        // 비틀림 — 첫 자세에서 J6(순회 직전 J6 에 가까운 감김)가 안전 범위 안인 후보 중 J6 변화가 작은 쪽(HD_AMR SelectSafeTwist).
        var shift = J6WindingShift(m, seed[5]);
        double WindJ6(double raw, double near) => raw + 360.0 * Math.Round((near - raw) / 360.0);
        bool SafeJ6(double j6) => j6 + shift >= m.SafeJ6MinDeg && j6 + shift <= m.SafeJ6MaxDeg;
        var twist = 0.0;
        if (reached > 0)
        {
            var pick = new[] { 0.0, 180.0 }
                .Select(tw => (Tw: tw, J6: WindJ6(sols[0][5] + tw, seed[5])))
                .Where(c => SafeJ6(c.J6))
                .OrderBy(c => Math.Abs(c.J6 - seed[5]))
                .Select(c => (double?)c.Tw).FirstOrDefault();
            twist = pick ?? 0.0;
        }

        var points = new List<ReachTraversalPoint>(reached);
        string? blocker = null;
        var minMargin = double.PositiveInfinity;
        var limiting = "여유 있음";
        ReachTraversalPoint? worst = null;
        for (var i = 0; i < reached; i++)
        {
            var (along, tilt, _, from) = plan[i];
            var j = (double[])sols[i].Clone();
            var prev = from < 0 ? seed : points[from].JointsDeg;
            j[5] = WindJ6(j[5] + twist, prev[5]);

            var (margin, why) = TraverseMargin(m, j, seed, SafeJ6(j[5]) ? null : j[5] + shift, shift);
            var p = new ReachTraversalPoint(along, tilt, tcps[i], j, margin, why);
            points.Add(p);
            if (margin < minMargin) { minMargin = margin; limiting = why; worst = p; }

            if (blocker is not null) continue;
            var at = Where(along, tilt);
            if (from >= 0 && StageJump6(prev, j) is { Travel: var jump, Joint: var jn } && jump > m.MaxPathJointStepDeg)
                blocker = $"도면 순회 — {at} J{jn + 1} {jump:0}° 튐(> {m.MaxPathJointStepDeg:0}°) — 해 가지가 바뀜";
            else if (ElbowFlip(seed, j))
                blocker = $"도면 순회 — {at} 팔꿈치 가지 전환(J3 {seed[2]:0.0}° → {j[2]:0.0}°)";
            else if (margin < 0)
                blocker = $"도면 순회 — {at} {why}";
        }
        if (unreachable is not null)
        {
            blocker ??= unreachable;
            minMargin = double.NegativeInfinity;
            limiting = unreachable;
        }
        if (points.Count == 0 && unreachable is null) minMargin = double.NegativeInfinity;
        return new ReachTraversal(blocker is null, blocker, minMargin, limiting, twist, worst, points);

        static string Where(double along, double tilt) => $"{along:0}mm 지점 기울기 {tilt:+0;-0;0}°";
    }

    /// <summary>
    /// 순회 자세 하나의 여유 [도]와 그 항목 — 관절 한계(J1~J5, 하드 한계 − <see cref="ReachRobotModel.PathJointLimitMarginDeg"/>)·
    /// 손목 특이점(|J5| − <see cref="ReachRobotModel.PlanarWristMarginDeg"/>, 순회 직전과 J5 부호가 다르면 음수)·
    /// J6 안전 범위 끝까지(순회 직전 J6 감김 기준) 중 최소. 음수면 HD_AMR 이 막는다.
    /// </summary>
    private static (double Margin, string Why) TraverseMargin(ReachRobotModel m, double[] j, double[] seed, double? j6Out, double shift)
    {
        var margin = double.PositiveInfinity;
        var why = "여유 있음";
        for (var i = 0; i < Math.Min(5, m.JointMinDeg.Length); i++)
        {
            var s = Math.Min(j[i] - m.JointMinDeg[i], m.JointMaxDeg[i] - j[i]) - m.PathJointLimitMarginDeg;
            if (s < margin) { margin = s; why = $"J{i + 1} 관절 한계 근접({j[i]:0.0}°)"; }
        }
        var sameSign = Math.Sign(j[4]) == Math.Sign(seed[4]) || Math.Sign(seed[4]) == 0;
        var wrist = sameSign ? Math.Abs(j[4]) - m.PlanarWristMarginDeg : -Math.Abs(j[4]) - m.PlanarWristMarginDeg;
        if (wrist < margin) { margin = wrist; why = $"손목 특이점(J5 {seed[4]:0.0}° → {j[4]:0.0}°)"; }
        if (m.MinElbowBendDeg > 0)
        {
            var elbow = Math.Abs(j[2]) - m.MinElbowBendDeg;
            if (elbow < margin) { margin = elbow; why = $"팔 뻗음 과다(|J3| {Math.Abs(j[2]):0.0}° < {m.MinElbowBendDeg:0}°)"; }
        }
        var j6 = j[5] + shift;
        var j6Slack = Math.Min(j6 - m.SafeJ6MinDeg, m.SafeJ6MaxDeg - j6);
        if (j6Slack < margin)
        {
            margin = j6Slack;
            why = $"J6 안전 범위({m.SafeJ6MinDeg:0}°~{m.SafeJ6MaxDeg:0}°){(j6Out is null ? " 근접" : " 밖")} — J6 {j6:0.0}°";
        }
        return (margin, why);
    }

    /// <summary>J6 를 안전 범위 쪽 감김으로 옮기는 360° 배수 — 범위 안이면 0, 어느 감김으로도 밖이면 0(HD_AMR SafeJ6WindingShiftDeg).</summary>
    private static double J6WindingShift(ReachRobotModel m, double j6)
    {
        foreach (var k in new[] { 0, -1, 1, -2, 2 })
            if (j6 + 360.0 * k >= m.SafeJ6MinDeg && j6 + 360.0 * k <= m.SafeJ6MaxDeg) return 360.0 * k;
        return 0;
    }

    /// <summary>
    /// IK 해마다 J1~J5 를 ±360 감아 관절 범위 안에서 기준에 가장 가까운 값으로 두고(J6 은 기준에 가까운 감김), J1~J5 이동 합이
    /// 가장 작은 해(HD_AMR GetInverseKinNearest). 범위 안 해가 없으면 null.
    /// </summary>
    private static double[]? NearestWound(ReachRobotModel m, IReadOnlyList<double[]> sols, double[] reference)
    {
        double[]? best = null;
        var bestCost = double.PositiveInfinity;
        foreach (var raw in sols)
        {
            var j = new double[raw.Length];
            var ok = true;
            for (var i = 0; i < raw.Length && ok; i++)
            {
                if (i >= m.JointMinDeg.Length)
                {
                    j[i] = raw[i] + 360.0 * Math.Round((reference[i] - raw[i]) / 360.0);
                    continue;
                }
                double? pick = null;
                foreach (var c in new[] { raw[i], raw[i] - 360, raw[i] + 360 })
                    if (c >= m.JointMinDeg[i] && c <= m.JointMaxDeg[i] && (pick is null || Math.Abs(c - reference[i]) < Math.Abs(pick.Value - reference[i])))
                        pick = c;
                if (pick is null) ok = false;
                else j[i] = pick.Value;
            }
            if (!ok) continue;
            var cost = Enumerable.Range(0, 5).Sum(k => Math.Abs(j[k] - reference[k]));
            if (cost < bestCost) { bestCost = cost; best = j; }
        }
        return best;
    }

    private static (double Travel, int Joint) StageJump6(double[] a, double[] b) =>
        Enumerable.Range(0, Math.Min(6, Math.Min(a.Length, b.Length))).Select(k => (Math.Abs(a[k] - b[k]), k)).MaxBy(t => t.Item1);
}
