using HD.Acs.UI.Models;
using HD.Acs.UI.Primitives;

namespace HD.Acs.UI.Rendering;

/// <summary>
/// 선창 형상 순수 함수 — 면 프레임(u,v)→도면 3D, 마구리(F/A) 팔각 단면 반폭·클리핑 다각형.
/// WPF TankView.xaml.cs의 TryPoint/TryCorners/HalfWidth/BulkheadPolygon을 프레임워크 중립으로 옮긴 것(TANK_RENDERING.md 3중복 해소의 정본).
/// </summary>
public static class TankShape
{
    /// <summary>면 로컬 (u,v) → 도면 3D 단일 점. 프레임 배열이 없으면 false.</summary>
    public static bool TryPoint(WallDto w, double u, double v, out Pt3 p)
    {
        p = default;
        if (w.Origin is not { Length: 3 } o || w.UAxis is not { Length: 3 } ua || w.VAxis is not { Length: 3 } va)
            return false;
        p = Pt3.FromArray(o) + Pt3.FromArray(ua) * u + Pt3.FromArray(va) * v;
        return true;
    }

    /// <summary>면 로컬 (u,v) 사각형의 4코너(c0=(uMin,vMin) → c1=(uMax,vMin) → c2=(uMax,vMax) → c3=(uMin,vMax)).</summary>
    public static Pt3[]? Corners(WallDto w, double uMin, double vMin, double uMax, double vMax)
    {
        if (w.Origin is not { Length: 3 } o || w.UAxis is not { Length: 3 } ua || w.VAxis is not { Length: 3 } va)
            return null;
        var origin = Pt3.FromArray(o); var u = Pt3.FromArray(ua); var v = Pt3.FromArray(va);
        Pt3 P(double uu, double vv) => origin + u * uu + v * vv;
        return new[] { P(uMin, vMin), P(uMax, vMin), P(uMax, vMax), P(uMin, vMax) };
    }

    /// <summary>면 법선(내부향) × 거리. 법선 없으면 영벡터.</summary>
    public static Pt3 NormalOffset(WallDto w, double meters) =>
        w.Normal is { Length: 3 } n ? Pt3.FromArray(n) * meters : Pt3.Zero;

    /// <summary>팔각 단면 반폭 y(z) — 하부챔퍼/수직벽/상부챔퍼 구간별 선형.</summary>
    public static double HalfWidth(TankGeometryDto g, double z)
    {
        double b2 = g.Derived.B / 2, wf2 = g.WFloor / 2, wc2 = g.Derived.WCeil / 2;
        double hLow = g.HLow, zWall = g.HLow + g.HWall, h = g.Derived.H;
        if (z <= hLow) return hLow > 1e-9 ? wf2 + (z / hLow) * (b2 - wf2) : b2;           // 하부 챔퍼
        if (z <= zWall) return b2;                                                         // 수직벽
        double hUp = h - zWall;
        return hUp > 1e-9 ? b2 - ((z - zWall) / hUp) * (b2 - wc2) : wc2;                   // 상부 챔퍼
    }

    /// <summary>
    /// 마구리 면(F/A)을 z∈[zLo,zHi]로 클리핑한 팔각 단면 다각형(도면 3D). F/A가 아니거나 지오메트리가 없으면 null → 호출부가 직사각형으로 폴백.
    /// 면은 x=const 평면이며 반폭 HalfWidth(z)로 좌/우 윤곽을 만들어 챔퍼 모서리가 모따기된 형상이 된다.
    /// </summary>
    public static List<Pt3>? BulkheadPolygon(WallDto w, TankGeometryDto? g, double zLo, double zHi)
    {
        if (w.WallCode is not ("F" or "A")) return null;
        if (g is null || w.Origin is not { Length: 3 } o) return null;
        double x = o[0], oy = g.OriginOy, h = g.Derived.H;
        zLo = Math.Max(0, zLo); zHi = Math.Min(h, zHi);
        if (zHi <= zLo) return null;

        double zWall = g.HLow + g.HWall;   // 수직벽 상단 z (챔퍼 무릎)
        var zs = new List<double> { zLo };
        foreach (var knee in new[] { g.HLow, zWall })
            if (knee > zLo + 1e-9 && knee < zHi - 1e-9) zs.Add(knee);
        zs.Add(zHi);
        zs.Sort();

        var pts = new List<Pt3>(zs.Count * 2);
        foreach (var z in zs) pts.Add(new Pt3(x, oy + HalfWidth(g, z), z));                       // 우현(+y) 아래→위
        for (int i = zs.Count - 1; i >= 0; i--) pts.Add(new Pt3(x, oy - HalfWidth(g, zs[i]), zs[i])); // 좌현(−y) 위→아래
        return pts;
    }

    /// <summary>면 타입별 연한 반투명 색조(바닥/천장/수직벽/챔퍼/마구리) — WPF FaceBrush와 동일.</summary>
    public static Rgba FaceColor(string code) => code switch
    {
        "B" => Rgba.FromArgb(0x66, 0x2E, 0x86, 0xC1),
        "T" => Rgba.FromArgb(0x55, 0xAE, 0xD6, 0xF1),
        "SM" or "PM" => Rgba.FromArgb(0x55, 0x48, 0xC9, 0xB0),
        "SL" or "PL" or "SU" or "PU" => Rgba.FromArgb(0x55, 0x5D, 0x6D, 0x7E),
        "F" or "A" => Rgba.FromArgb(0x55, 0xD5, 0xA6, 0x7E),
        _ => Rgba.FromArgb(0x55, 0x85, 0x92, 0x9E),
    };
}
