using System.Text.Json;
using HD.Acs.Core.Planning;
using HD.Acs.Data.Entities;

namespace HD.Acs.App.Services;

/// <summary>
/// 검사 영역 등록(POST)·수정(PUT) 공통 검증 [SPEC v3 §4 / v3.1 §5-A] — 두 경로가 같은 규칙을 쓴다.
/// </summary>
public static class AreaRules
{
    /// <summary>
    /// 코너(임의 4점) 검증 — 전 코너 면 범위 내·비퇴화·최대 1.44m. 위반 사유(한국어) 또는 null(통과, bbox 반환).
    /// 대각 2점만 검사하면 회전 사각형이 면 밖으로 나가도 통과되므로 전 코너를 본다.
    /// </summary>
    public static string? ValidateCorners(IReadOnlyList<double[]> corners, string wallCode, double uLen, double vLen,
        out (double MinU, double MinV, double MaxU, double MaxV) bbox)
    {
        bbox = default;
        if (corners.Count < 3)
            return "영역 코너가 3점 미만입니다.";
        if (corners.Any(p => !double.IsFinite(p[0]) || !double.IsFinite(p[1])))
            return "영역 코너는 유한한 숫자여야 합니다.";
        if (corners.Any(p => !AreaGeometry.InBounds(p[0], p[1], 0, 0, uLen, vLen)))
            return $"영역 코너가 면 범위를 벗어났습니다 (면 {wallCode}: u∈[0,{uLen:0.###}], v∈[0,{vLen:0.###}]).";
        bbox = AreaGeometry.Bbox(corners);
        if (bbox.MaxU - bbox.MinU < 1e-6 || bbox.MaxV - bbox.MinV < 1e-6)
            return "영역이 퇴화(면적 0)했습니다 — 유효한 사각형 4점을 입력하세요.";
        if (!AreaGeometry.WithinMaxSize(bbox.MinU, bbox.MinV, bbox.MaxU, bbox.MaxV))
            return "AREA 최대 크기는 벽면 로컬 u/v 각 1.44m(1440mm)입니다.";
        return null;
    }

    /// <summary>층 자동 유도 [SPEC v3.1 §5-A] — 영역 z범위(코너 v의 min/max)로 유도. 실패 시 null + 사유.</summary>
    public static int? DeriveLevel(TankGeometryEntity g, WallEntity wall, double vMin, double vMax, out string? reason)
    {
        var geom = new TankGeometry(
            g.LengthL, g.WFloor, g.ThetaLow, g.HLow, g.HWall, g.ThetaUp, g.HUp,
            JsonSerializer.Deserialize<double[]>(g.LevelZ) ?? Array.Empty<double>(),
            g.OriginOx, g.OriginOy, g.ReachZMin, g.ReachZMax);
        var vAxis = JsonSerializer.Deserialize<double[]>(wall.VAxis)!;
        var origin = JsonSerializer.Deserialize<double[]>(wall.Origin)!;
        var (zLo, zHi) = LevelBands.AreaZRange(origin[2], vAxis[2], vMin, vMax);
        return LevelBands.Derive(zLo, zHi, geom.LevelBandList(), out reason);
    }

    /// <summary>
    /// 영역 수정 시 기존 작업 중 새 폴리곤 밖으로 나가는 작업의 seq 목록(시작·끝점 중 하나라도 밖이면 포함).
    /// 영역을 줄여 작업이 밖에 남으면 그 작업은 등록 규칙(영역 내부)을 어기게 되므로 수정을 거부한다.
    /// </summary>
    public static IReadOnlyList<int> TasksOutside(IReadOnlyList<double[]> poly, IEnumerable<AreaTaskEntity> tasks) =>
        tasks.Where(t => !AreaGeometry.PointInPolygon(t.StartU, t.StartV, poly) || !AreaGeometry.PointInPolygon(t.EndU, t.EndV, poly))
             .Select(t => t.Seq).OrderBy(s => s).ToList();
}
