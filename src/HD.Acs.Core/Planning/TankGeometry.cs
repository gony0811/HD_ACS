using HD.Acs.Core.Geometry;

namespace HD.Acs.Core.Planning;

/// <summary>선창 파생 치수 [SPEC v3 §2]. w_low/w_up = 챔퍼 수평 run, B=전폭, W_ceil=천장폭, H=전체높이.</summary>
public sealed record TankDerived(double WLow, double B, double WUp, double WCeil, double H);

/// <summary>
/// 자동 생성된 벽면(면) 프레임 [SPEC v3 §3]. Pose(P0,U,V)=벽면-로컬 2D→도면 3D, Normal=내부향 단위법선,
/// U/V Len=면 크기(m), FacingYaw=AMR이 면을 바라보는 도면 yaw(수평법선 없으면 null=바닥/천장).
/// </summary>
public sealed record GeneratedWall(string WallCode, WallPose Pose, double[] Normal, double ULen, double VLen, double? FacingYaw)
{
    /// <summary>벽면-로컬 (u,v) → 도면 3D [x,y,z].</summary>
    public double[] To3D(double u, double v) => Pose.LocalToDrawing(u, v);

    internal static GeneratedWall Build(string code, double[] p0, double[] u, double[] v, double[] n, double uLen, double vLen)
    {
        var U = Vec3.Normalize(u) ?? throw new WallPoseInvalidException($"{code}: U축 산출 불가.");
        var V = Vec3.Normalize(v) ?? throw new WallPoseInvalidException($"{code}: V축 산출 불가.");
        var N = Vec3.Normalize(n) ?? throw new WallPoseInvalidException($"{code}: 법선 산출 불가.");
        double? facing = null;
        double nh = Math.Sqrt(N[0] * N[0] + N[1] * N[1]);   // 법선 수평 성분 크기
        if (nh >= 1e-9) facing = Math.Atan2(-N[1], -N[0]);   // AMR이 면을 바라봄(−법선 방향) [§3]
        return new GeneratedWall(code, new WallPose(p0, U, V), N, uLen, vLen, facing);
    }
}

/// <summary>
/// 선창 파라메트릭 정의 [SPEC v3 §2/§3]. 팔각 단면(좌우대칭) × 길이 L 프리즘 → 10면 자동 생성.
/// 전역 프레임: 원점=바닥 중심, x=길이, y=폭(+y 좌현), z=상방, 바닥 z=0 (단위 m). 각도 rad.
/// </summary>
public sealed record TankGeometry(
    double L, double WFloor, double ThetaLow, double HLow,
    double HWall, double ThetaUp, double HUp,
    double[] LevelZ, double Ox = 0, double Oy = 0,
    double? ReachZMin = null, double? ReachZMax = null)
{
    /// <summary>층 도달 밴드 [SPEC v3.1 §5-A] — level_z·reach_z·H로 산출.</summary>
    public IReadOnlyList<LevelBand> LevelBandList() =>
        LevelBands.Compute(LevelZ, ReachZMin, ReachZMax, Derived().H);

    public TankDerived Derived()
    {
        double wLow = HLow / Math.Tan(ThetaLow);
        double b = WFloor + 2 * wLow;
        double wUp = HUp / Math.Tan(ThetaUp);
        double wCeil = b - 2 * wUp;
        double h = HLow + HWall + HUp;
        return new TankDerived(wLow, b, wUp, wCeil, h);
    }

    /// <summary>등록 검증 [§2]. 위반 사유 목록(빈 목록=통과). tolM=검증치수 허용오차(기본 5mm).</summary>
    public IReadOnlyList<string> Validate(double? checkHTotal, double? checkBeam, double? checkWCeil, double tolM = 0.005)
    {
        var e = new List<string>();
        if (!(ThetaLow > 0 && ThetaLow < Math.PI / 2)) e.Add("theta_low 는 (0°,90°) 이어야 합니다.");
        if (!(ThetaUp > 0 && ThetaUp < Math.PI / 2)) e.Add("theta_up 는 (0°,90°) 이어야 합니다.");
        if (L <= 0 || WFloor <= 0 || HLow <= 0 || HWall <= 0 || HUp <= 0) e.Add("길이·폭·높이는 양수여야 합니다.");
        if (e.Count > 0) return e;   // 유도값 계산 전 조기 반환(tan 안전)

        var d = Derived();
        if (d.WCeil <= 0) e.Add($"천장폭 W_ceil={d.WCeil:F3} ≤ 0 — 상부 챔퍼가 과대합니다.");
        if (LevelZ is null || LevelZ.Length == 0) e.Add("level_z 가 비어 있습니다.");
        else
        {
            for (int i = 1; i < LevelZ.Length; i++)
                if (LevelZ[i] <= LevelZ[i - 1]) { e.Add("level_z 는 오름차순이어야 합니다."); break; }
            if (LevelZ[^1] >= d.H) e.Add($"level_z 최상단({LevelZ[^1]:F3}) 은 전체높이 H({d.H:F3}) 미만이어야 합니다.");
        }
        if (checkHTotal is double ch && Math.Abs(ch - d.H) > tolM) e.Add($"검증 H={ch:F3} ≠ 유도 H={d.H:F3} (허용 {tolM * 1000:F0}mm 초과).");
        if (checkBeam is double cb && Math.Abs(cb - d.B) > tolM) e.Add($"검증 beam={cb:F3} ≠ 유도 B={d.B:F3}.");
        if (checkWCeil is double cw && Math.Abs(cw - d.WCeil) > tolM) e.Add($"검증 W_ceil={cw:F3} ≠ 유도 W_ceil={d.WCeil:F3}.");
        return e;
    }

    /// <summary>10면 자동 생성 [§3]. 코드=TANK_WALL_LAYOUT 매핑(B/SL/PL/SM/PM/SU/PU/T/F/A). 내부향 법선.</summary>
    public IReadOnlyList<GeneratedWall> GenerateWalls()
    {
        var d = Derived();
        double Lh = L / 2, Wf2 = WFloor / 2, B2 = d.B / 2, Wc2 = d.WCeil / 2;
        double cl = Math.Cos(ThetaLow), sl = Math.Sin(ThetaLow);
        double cu = Math.Cos(ThetaUp), su = Math.Sin(ThetaUp);
        double zLow = HLow, zWall = HLow + HWall, zTop = d.H;

        GeneratedWall W(string code, double[] p0, double[] u, double[] v, double[] n, double uLen, double vLen) =>
            GeneratedWall.Build(code, new[] { p0[0] + Ox, p0[1] + Oy, p0[2] }, u, v, n, uLen, vLen);

        return new[]
        {
            W("B",  new[]{-Lh,-Wf2, 0.0}, new[]{1.0,0,0}, new[]{0.0, 1, 0}, new[]{ 0.0, 0, 1}, L, WFloor),      // 바닥 FL
            W("SL", new[]{-Lh,-Wf2, 0.0}, new[]{1.0,0,0}, new[]{0.0,-cl,sl}, new[]{ 0.0, sl, cl}, L, HLow/sl),  // 하부챔퍼 우현 BC-S
            W("PL", new[]{-Lh, Wf2, 0.0}, new[]{1.0,0,0}, new[]{0.0, cl,sl}, new[]{ 0.0,-sl, cl}, L, HLow/sl),  // 하부챔퍼 좌현 BC-P
            W("SM", new[]{-Lh,-B2, zLow}, new[]{1.0,0,0}, new[]{0.0, 0, 1}, new[]{ 0.0, 1, 0}, L, HWall),        // 수직벽 우현 SW-S
            W("PM", new[]{-Lh, B2, zLow}, new[]{1.0,0,0}, new[]{0.0, 0, 1}, new[]{ 0.0,-1, 0}, L, HWall),        // 수직벽 좌현 SW-P
            W("SU", new[]{-Lh,-B2,zWall}, new[]{1.0,0,0}, new[]{0.0, cu,su}, new[]{ 0.0, su,-cu}, L, HUp/su),    // 상부챔퍼 우현 TC-S
            W("PU", new[]{-Lh, B2,zWall}, new[]{1.0,0,0}, new[]{0.0,-cu,su}, new[]{ 0.0,-su,-cu}, L, HUp/su),    // 상부챔퍼 좌현 TC-P
            W("T",  new[]{-Lh,-Wc2,zTop}, new[]{1.0,0,0}, new[]{0.0, 1, 0}, new[]{ 0.0, 0,-1}, L, d.WCeil),      // 천장 CL
            W("F",  new[]{ Lh, B2, 0.0}, new[]{0.0,-1,0}, new[]{0.0, 0, 1}, new[]{-1.0, 0, 0}, d.B, d.H),        // 선수 마구리 FW (x=+L/2)
            W("A",  new[]{-Lh,-B2, 0.0}, new[]{0.0, 1,0}, new[]{0.0, 0, 1}, new[]{ 1.0, 0, 0}, d.B, d.H),        // 선미 마구리 AW (x=−L/2)
        };
    }
}
