using HD.Acs.UI.Primitives;

namespace HD.Acs.UI.Rendering;

/// <summary>
/// 오빗(궤도) 원근 카메라 — 프레임워크 중립. 도면 프레임(z-up)에서 Target을 중심으로 Yaw(수평 회전)·Pitch(앙각)·Distance로 시점을 정한다.
/// Project는 화면 px(좌상단 원점, y 아래)와 시선 깊이를 돌려주고, Unproject는 화면 점의 시선 광선을 만들어 평면 교차(바닥 클릭)에 쓴다.
/// HelixToolkit 뷰포트(ZoomExtents·마우스 오빗/팬/줌)의 최소 대체.
/// </summary>
public sealed class Camera3
{
    private const double MinPitch = -89, MaxPitch = 89, NearPlane = 0.05;

    public Pt3 Target { get; set; } = Pt3.Zero;
    public double Distance { get; set; } = 30;
    /// <summary>수평 회전(도). 0=+x 방향에서 바라봄, 반시계 양수.</summary>
    public double YawDeg { get; set; } = -125;
    /// <summary>앙각(도). 양수=위에서 내려다봄.</summary>
    public double PitchDeg { get; set; } = 30;
    /// <summary>수직 시야각(도).</summary>
    public double FovDeg { get; set; } = 45;

    public double MinDistance { get; set; } = 0.5;
    public double MaxDistance { get; set; } = 2000;

    /// <summary>시점 위치(도면 프레임).</summary>
    public Pt3 Eye
    {
        get
        {
            double yaw = YawDeg * Math.PI / 180, pitch = PitchDeg * Math.PI / 180;
            var dir = new Pt3(Math.Cos(pitch) * Math.Cos(yaw), Math.Cos(pitch) * Math.Sin(yaw), Math.Sin(pitch));
            return Target + dir * Distance;
        }
    }

    /// <summary>뷰 기저 — forward(시선), right, up. Pitch가 ±89°로 제한되어 up과 평행해지지 않는다.</summary>
    public (Pt3 Forward, Pt3 Right, Pt3 Up) Basis()
    {
        var f = (Target - Eye).Normalized();
        var r = f.Cross(Pt3.UnitZ).Normalized();
        if (r.LengthSquared < 1e-12) r = new Pt3(0, 1, 0);
        var u = r.Cross(f).Normalized();
        return (f, r, u);
    }

    /// <summary>수직 시야각 기준 초점 거리(px) — 화면 높이에 비례.</summary>
    public double FocalPx(double viewHeight) => (viewHeight / 2) / Math.Tan(FovDeg * Math.PI / 360);

    /// <summary>도면 점 → 화면 px + 시선 깊이. 근평면 뒤(카메라 뒤)면 Visible=false.</summary>
    public (Pt2 Screen, double Depth, bool Visible) Project(Pt3 p, double viewWidth, double viewHeight)
    {
        var (f, r, u) = Basis();
        var d = p - Eye;
        double depth = d.Dot(f);
        if (depth <= NearPlane) return (default, depth, false);
        double focal = FocalPx(viewHeight);
        double sx = viewWidth / 2 + d.Dot(r) * focal / depth;
        double sy = viewHeight / 2 - d.Dot(u) * focal / depth;
        return (new Pt2(sx, sy), depth, true);
    }

    /// <summary>화면 px → 시선 광선(원점=Eye, 단위 방향).</summary>
    public (Pt3 Origin, Pt3 Direction) Unproject(Pt2 screen, double viewWidth, double viewHeight)
    {
        var (f, r, u) = Basis();
        double focal = FocalPx(viewHeight);
        var dir = f + r * ((screen.X - viewWidth / 2) / focal) + u * ((viewHeight / 2 - screen.Y) / focal);
        return (Eye, dir.Normalized());
    }

    /// <summary>화면 점의 시선 광선과 수평면 z=z0의 교점(카메라 앞쪽만). 평행하거나 뒤쪽이면 null.</summary>
    public Pt3? HitPlaneZ(Pt2 screen, double z0, double viewWidth, double viewHeight)
    {
        var (o, d) = Unproject(screen, viewWidth, viewHeight);
        if (Math.Abs(d.Z) < 1e-9) return null;
        double t = (z0 - o.Z) / d.Z;
        return t > 0 ? o + d * t : null;
    }

    public void Orbit(double dYawDeg, double dPitchDeg)
    {
        YawDeg = (YawDeg + dYawDeg) % 360;
        PitchDeg = Math.Clamp(PitchDeg + dPitchDeg, MinPitch, MaxPitch);
    }

    public void Zoom(double factor) => Distance = Math.Clamp(Distance * factor, MinDistance, MaxDistance);

    /// <summary>화면 px 이동량만큼 Target을 시선에 수직인 평면에서 이동(Target 깊이 기준 px→m 환산).</summary>
    public void Pan(double dxPx, double dyPx, double viewHeight)
    {
        var (_, r, u) = Basis();
        double k = Distance / FocalPx(viewHeight);
        Target = Target - r * (dxPx * k) + u * (dyPx * k);
    }

    /// <summary>점 집합의 경계 구가 화면에 들어오도록 Target·Distance 설정(시야각·종횡비 고려). 빈 집합이면 무시.</summary>
    public void ZoomExtents(IEnumerable<Pt3> points, double viewWidth, double viewHeight, double margin = 1.1)
    {
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        int n = 0;
        foreach (var p in points)
        {
            n++;
            if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
            if (p.Z < minZ) minZ = p.Z; if (p.Z > maxZ) maxZ = p.Z;
        }
        if (n == 0) return;

        Target = new Pt3((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2);
        double radius = Math.Max(1e-3, new Pt3(maxX - minX, maxY - minY, maxZ - minZ).Length / 2);

        // 유효 시야각 = 수직/수평 중 좁은 쪽 (viewWidth<viewHeight면 수평이 더 좁다)
        double vHalf = FovDeg * Math.PI / 360;
        double aspect = viewHeight > 0 && viewWidth > 0 ? viewWidth / viewHeight : 1;
        double hHalf = Math.Atan(Math.Tan(vHalf) * aspect);
        double half = Math.Min(vHalf, hHalf);
        Distance = Math.Clamp(radius / Math.Sin(half) * margin, MinDistance, MaxDistance);
    }
}
