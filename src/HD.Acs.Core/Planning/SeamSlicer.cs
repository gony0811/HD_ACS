namespace HD.Acs.Core.Planning;

/// <summary>슬라이싱 설정 (appsettings Acs:Slicer). 도면 좌표계 내 계산에만 사용, m 단위.</summary>
/// <param name="CobotReachM">코봇 리치 (한 구간 최대 길이)</param>
/// <param name="OverlapM">인접 구간 겹침</param>
/// <param name="StandoffM">벽면→정차점 법선 오프셋 (코봇 워킹디스턴스 포함)</param>
/// <param name="StationThetaOffset">정차 방향 보정 — 기본 정차 방향은 벽면(−법선)을 향함</param>
/// <param name="MergeDistM">정차점 병합 거리 임계 (이내면 같은 스테이션=앵커그룹)</param>
public sealed record SlicerConfig(
    double CobotReachM,
    double OverlapM,
    double StandoffM,
    double StationThetaOffset,
    double MergeDistM);

/// <summary>SeamSlicer 입력 — Data 엔티티 미의존(Core 순수성). 도면 좌표(m).</summary>
public sealed record SeamInput(
    string SeamId,
    string TankId,
    int Level,
    string WallCode,
    string SeamType,                                       // LINE | POLYLINE
    IReadOnlyList<(double X, double Y, double Z)> Path,     // LINE이면 2점
    (double X, double Y, double Z) Normal,                  // 벽면 법선 (도면 좌표계)
    string SectionDxfId,
    string ProfileId);

/// <summary>스테이션 리치 안 검사 구간 1개 [불변식 1: TASK 1개 = seam 리치 구간 1개].</summary>
public sealed record SlicedTask(
    string SeamId,
    int SeqInGroup,
    string AnchorGroupId,
    (double X, double Y, double Z) SeamStartDrawing,
    (double X, double Y, double Z) SeamEndDrawing,
    (double X, double Y, double Z) WallNormalDrawing,
    string SeamType,
    string TankId,
    int Level,
    string WallCode,
    string SectionDxfId,
    string ProfileId);

/// <summary>정차 영역(앵커그룹) — 도면 좌표 정차 pose + 그 안에서 실행되는 TASK들.</summary>
public sealed record SlicedStation(
    string AnchorGroupId,
    (double X, double Y, double Theta) StationDrawing,
    IReadOnlyList<SlicedTask> Tasks);

/// <summary>
/// seam 집합 → 스테이션(영역)·TASK 자동 산출 [PHASE2 WP-2, SPEC §3.2].
/// 도면 좌표계 내에서만 계산한다 (T_W_D 적용은 상위 SeamPlanningService 책임).
/// </summary>
public static class SeamSlicer
{
    /// <summary>
    /// 구간수 = ceil(L / (reach − overlap)). 스테이션 = 구간 중점 + 법선×standoff.
    /// 같은 정차점(거리 &lt; MergeDistM, 동일 tank/level/wall)에 오는 구간은 같은 AnchorGroup으로 병합.
    /// </summary>
    public static IReadOnlyList<SlicedStation> Slice(IReadOnlyList<SeamInput> seams, SlicerConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(seams);
        ArgumentNullException.ThrowIfNull(cfg);
        double step = cfg.CobotReachM - cfg.OverlapM;
        if (step <= 0)
            throw new ArgumentException("CobotReachM 는 OverlapM 보다 커야 합니다 (step = reach − overlap > 0).", nameof(cfg));

        // 1) 각 seam을 구간으로 분할하고 구간별 정차 후보(도면 pose) 생성
        var candidates = new List<Candidate>();
        foreach (var seam in seams)
        {
            if (seam.Path.Count < 2) continue;   // 유효하지 않은 seam은 조용히 스킵(상위에서 skipped 집계)

            var cum = CumulativeLengths(seam.Path);
            double length = cum[^1];
            int n = Math.Max(1, (int)Math.Ceiling(length / step));

            var (nx, ny) = Normalize2D(seam.Normal.X, seam.Normal.Y);
            double theta = Math.Atan2(-ny, -nx) + cfg.StationThetaOffset;   // 벽면(−법선) 방향

            for (int i = 0; i < n; i++)
            {
                double sStart = length * i / n;
                double sEnd = length * (i + 1) / n;
                var pStart = SampleAt(seam.Path, cum, sStart);
                var pEnd = SampleAt(seam.Path, cum, sEnd);
                var pMid = SampleAt(seam.Path, cum, (sStart + sEnd) / 2);

                double stX = pMid.X + nx * cfg.StandoffM;
                double stY = pMid.Y + ny * cfg.StandoffM;
                candidates.Add(new Candidate(seam, pStart, pEnd, stX, stY, theta));
            }
        }

        // 2) 근접 정차점 병합 — 앵커(첫 멤버 위치) 기준 거리, 동일 tank/level/wall만
        var groups = new List<Group>();
        foreach (var c in candidates)
        {
            var g = groups.FirstOrDefault(g =>
                g.TankId == c.Seam.TankId && g.Level == c.Seam.Level && g.WallCode == c.Seam.WallCode &&
                Dist(g.AnchorX, g.AnchorY, c.StX, c.StY) < cfg.MergeDistM);
            if (g is null)
            {
                g = new Group(c.Seam.TankId, c.Seam.Level, c.Seam.WallCode, c.StX, c.StY);
                groups.Add(g);
            }
            g.Members.Add(c);
        }

        // 3) anchorGroupId 부여(tank-level-wall 별 ST 카운터) + 대표 pose(centroid) + seqInGroup
        var stCounter = new Dictionary<string, int>();
        var result = new List<SlicedStation>(groups.Count);
        foreach (var g in groups)
        {
            string key = $"{g.TankId}-L{g.Level}-{g.WallCode}";
            int nn = stCounter.GetValueOrDefault(key) + 1;
            stCounter[key] = nn;
            string anchorGroupId = $"{key}-ST{nn:D2}";

            double cx = g.Members.Average(m => m.StX);
            double cy = g.Members.Average(m => m.StY);
            double theta = g.Members[0].Theta;   // 동일 벽면 → 동일 법선 → 대표 theta

            int seq = 1;
            var tasks = g.Members.Select(m => new SlicedTask(
                m.Seam.SeamId, seq++, anchorGroupId,
                m.Start, m.End, m.Seam.Normal,
                m.Seam.SeamType, m.Seam.TankId, m.Seam.Level, m.Seam.WallCode,
                m.Seam.SectionDxfId, m.Seam.ProfileId)).ToList();

            result.Add(new SlicedStation(anchorGroupId, (cx, cy, theta), tasks));
        }
        return result;
    }

    private static double[] CumulativeLengths(IReadOnlyList<(double X, double Y, double Z)> pts)
    {
        var cum = new double[pts.Count];
        for (int i = 1; i < pts.Count; i++)
            cum[i] = cum[i - 1] + Dist(pts[i - 1].X, pts[i - 1].Y, pts[i].X, pts[i].Y);
        return cum;
    }

    /// <summary>폴리라인 위 호길이 s 지점 샘플 (2D 보간, z 포함).</summary>
    private static (double X, double Y, double Z) SampleAt(
        IReadOnlyList<(double X, double Y, double Z)> pts, double[] cum, double s)
    {
        double total = cum[^1];
        if (s <= 0 || total == 0) return pts[0];
        if (s >= total) return pts[^1];

        int i = 1;
        while (i < cum.Length && cum[i] < s) i++;
        // 구간 [i-1, i] 내부
        double segLen = cum[i] - cum[i - 1];
        double t = segLen > 0 ? (s - cum[i - 1]) / segLen : 0;
        var a = pts[i - 1];
        var b = pts[i];
        return (a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);
    }

    private static (double X, double Y) Normalize2D(double x, double y)
    {
        double m = Math.Sqrt(x * x + y * y);
        return m > 1e-12 ? (x / m, y / m) : (1.0, 0.0);
    }

    private static double Dist(double ax, double ay, double bx, double by)
        => Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));

    private sealed record Candidate(
        SeamInput Seam,
        (double X, double Y, double Z) Start,
        (double X, double Y, double Z) End,
        double StX, double StY, double Theta);

    private sealed class Group(string tankId, int level, string wallCode, double anchorX, double anchorY)
    {
        public string TankId { get; } = tankId;
        public int Level { get; } = level;
        public string WallCode { get; } = wallCode;
        public double AnchorX { get; } = anchorX;
        public double AnchorY { get; } = anchorY;
        public List<Candidate> Members { get; } = new();
    }
}
