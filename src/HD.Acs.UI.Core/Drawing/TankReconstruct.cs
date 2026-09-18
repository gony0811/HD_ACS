using HD.Acs.UI.Primitives;

namespace HD.Acs.UI.Drawing;

/// <summary>
/// 도면(DXF)에서 선창 팔각 단면 파라미터를 역산한다 — 파라미터 수기 입력(선창 3D 정의)을 대체.
/// 마구리(A/F) 면의 멤브레인 패턴 외곽(=팔각 단면)에서 바닥폭·전폭·챔퍼 높이/각을 뽑고,
/// 측벽/바닥/천장 면의 폭에서 선창 길이(L)를 얻는다. 좌표는 면-로컬 mm.
/// 결과 파라미터는 기존 지오메트리 파이프라인(RegisterTankGeometry)에 그대로 투입 → 3D·전개도·백엔드 무변경.
/// </summary>
public static class TankReconstruct
{
    /// <summary>팔각 단면 역산 결과(모두 mm·도).</summary>
    public sealed record OctagonFit(
        double BeamB, double HTotal, double WFloor, double WCeil,
        double HLow, double HWall, double HUp, double ThetaLowDeg, double ThetaUpDeg);

    /// <summary>
    /// 마구리 면-로컬 점 집합에서 팔각 단면을 역산. bbox 경계(최상/최하/최좌)에 tol 이내로 붙은 점을
    /// 각 변으로 묶어 바닥폭·천장폭·수직벽 구간을 구한다. 점 부족/퇴화 시 null.
    /// </summary>
    public static OctagonFit? FitOctagon(IReadOnlyList<Pt2> pts, double tolMm = 60)
    {
        if (pts.Count < 4) return null;
        double minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X);
        double minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
        double beamB = maxX - minX, hTotal = maxY - minY;
        if (beamB < 1 || hTotal < 1) return null;

        var bottom = pts.Where(p => Math.Abs(p.Y - minY) <= tolMm).Select(p => p.X).ToList();  // 바닥 변
        var top = pts.Where(p => Math.Abs(p.Y - maxY) <= tolMm).Select(p => p.X).ToList();      // 천장 변
        var leftY = pts.Where(p => Math.Abs(p.X - minX) <= tolMm).Select(p => p.Y).ToList();    // 좌 수직벽
        if (bottom.Count < 2 || top.Count < 2 || leftY.Count < 2) return null;

        double wFloor = bottom.Max() - bottom.Min();
        double wCeil = top.Max() - top.Min();
        double yLoWall = leftY.Min(), yHiWall = leftY.Max();
        double hLow = yLoWall - minY, hUp = maxY - yHiWall, hWall = yHiWall - yLoWall;
        if (hLow <= 0 || hUp <= 0 || hWall <= 0 || wFloor <= 0 || wCeil <= 0) return null;

        // 챔퍼 각 = atan2(챔퍼 높이, 수평 투영 (전폭−변폭)/2)
        double thLow = Math.Atan2(hLow, Math.Max(1e-6, (beamB - wFloor) / 2)) * 180 / Math.PI;
        double thUp = Math.Atan2(hUp, Math.Max(1e-6, (beamB - wCeil) / 2)) * 180 / Math.PI;
        return new OctagonFit(beamB, hTotal, wFloor, wCeil, hLow, hWall, hUp, thLow, thUp);
    }

    /// <summary>점 집합의 bbox 크기(mm). 비면 null.</summary>
    public static (double W, double H)? Extent(IReadOnlyList<Pt2> pts) =>
        pts.Count == 0 ? null : (pts.Max(p => p.X) - pts.Min(p => p.X), pts.Max(p => p.Y) - pts.Min(p => p.Y));
}
