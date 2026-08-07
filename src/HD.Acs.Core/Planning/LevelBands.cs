namespace HD.Acs.Core.Planning;

/// <summary>층 도달 밴드 [SPEC v3.1 §5-A]. 층 ℓ(1-based)이 코봇으로 도달 가능한 전역 z구간 [ZMin, ZMax].</summary>
/// <param name="Level">층 번호(1-based, 1=바닥층)</param>
/// <param name="ZMin">밴드 하한(전역 z, m)</param>
/// <param name="ZMax">밴드 상한(전역 z, m). 최상층은 전체높이 H(폐구간, 천장 포함)</param>
public sealed record LevelBand(int Level, double ZMin, double ZMax);

/// <summary>
/// 층 자동 유도 — 면×층 도달 밴드 [SPEC v3.1 §5-A]. 순수 함수(의존성 없음).
/// 층은 운영자 입력이 아니라 영역 z범위에서 유도한다("0층+천장" 같은 불가능 조합 원천 차단).
/// 면 z(v)는 u에 무관: 모든 면의 u축이 수평이라 z(v) = origin.z + v·vAxis.z.
/// </summary>
public static class LevelBands
{
    /// <summary>
    /// 층 도달 밴드 목록 생성. 층 ℓ 밴드 B(ℓ) = [z_ℓ + reachMin, min(z_{ℓ+1}, z_ℓ + reachMax)).
    /// reach_z_* 미지정 시 B(ℓ) = [z_ℓ, z_{ℓ+1}). 최상층 상한 = 전체높이 H(폐구간, 천장 포함).
    /// </summary>
    /// <param name="levelZ">층 경계 z 오름차순 목록 (각 층 바닥의 전역 z)</param>
    /// <param name="reachMin">(선택) 플랫폼 기준 코봇 도달 하한 상대높이</param>
    /// <param name="reachMax">(선택) 플랫폼 기준 코봇 도달 상한 상대높이</param>
    /// <param name="h">선창 전체높이 H (최상층 밴드 상한)</param>
    public static IReadOnlyList<LevelBand> Compute(double[] levelZ, double? reachMin, double? reachMax, double h)
    {
        if (levelZ is null || levelZ.Length == 0) return Array.Empty<LevelBand>();
        var bands = new List<LevelBand>(levelZ.Length);
        for (int i = 0; i < levelZ.Length; i++)
        {
            double baseLo = levelZ[i];
            double nextZ = i < levelZ.Length - 1 ? levelZ[i + 1] : h;   // 최상층 상한 = H
            double zMin = baseLo + (reachMin ?? 0);
            double zMax = reachMax is double rmax ? Math.Min(nextZ, baseLo + rmax) : nextZ;
            bands.Add(new LevelBand(i + 1, zMin, zMax));
        }
        return bands;
    }

    /// <summary>영역 z범위 = sort(z(v_min), z(v_max)). z(v) = originZ + v·vAxisZ (u 무관).</summary>
    public static (double ZLo, double ZHi) AreaZRange(double originZ, double vAxisZ, double vMin, double vMax)
    {
        double z1 = originZ + vMin * vAxisZ, z2 = originZ + vMax * vAxisZ;
        return z1 <= z2 ? (z1, z2) : (z2, z1);
    }

    /// <summary>
    /// 영역 z범위 [zLo, zHi]에서 층 유도. 허용오차 ε(기본 5mm) 내에서 정확히 하나의 밴드에
    /// 완전 포함되면 그 층 반환. 아니면 null + reason(밴드 밖=도달 불가 / 경계 걸침).
    /// </summary>
    public static int? Derive(double zLo, double zHi, IReadOnlyList<LevelBand> bands, out string? reason, double eps = 0.005)
    {
        reason = null;
        if (bands is null || bands.Count == 0) { reason = "층 밴드가 정의되지 않았습니다 (level_z 확인)."; return null; }

        var contained = bands.Where(b => zLo >= b.ZMin - eps && zHi <= b.ZMax + eps).ToList();
        if (contained.Count == 1) return contained[0].Level;

        // 실패 — 사유 구분: 인접 밴드 여러 개와 겹치면 경계 걸침, 아니면 도달 불가.
        var overlapping = bands.Where(b => zHi >= b.ZMin - eps && zLo <= b.ZMax + eps).ToList();
        reason = overlapping.Count >= 2
            ? $"층 경계에 걸침 — 영역을 층별로 분할해야 합니다 (영역 z=[{zLo:F3},{zHi:F3}])."
            : $"도달 불가 높이 — 어떤 층 도달 밴드에도 포함되지 않습니다 (영역 z=[{zLo:F3},{zHi:F3}]).";
        return null;
    }

    /// <summary>
    /// 면×층 교차 → 도달 가능 v구간 [vLo, vHi]. 면의 z(v)를 밴드 z구간으로 역변환 후 [0, vLen] 클리핑.
    /// 겹치지 않으면 null. 수평면(vAxisZ≈0, 바닥/천장)은 originZ가 밴드 안이면 전체 [0, vLen].
    /// </summary>
    public static (double VLo, double VHi)? ReachableVBand(double originZ, double vAxisZ, double vLen, LevelBand band, double eps = 0.005)
    {
        if (Math.Abs(vAxisZ) < 1e-9)   // 수평면 — z 상수
            return originZ >= band.ZMin - eps && originZ <= band.ZMax + eps ? (0.0, vLen) : null;

        double vA = (band.ZMin - originZ) / vAxisZ;
        double vB = (band.ZMax - originZ) / vAxisZ;
        double vLoRaw = Math.Min(vA, vB), vHiRaw = Math.Max(vA, vB);
        double vLo = Math.Max(0, vLoRaw), vHi = Math.Min(vLen, vHiRaw);
        return vHi > vLo + 1e-9 ? (vLo, vHi) : null;
    }
}
