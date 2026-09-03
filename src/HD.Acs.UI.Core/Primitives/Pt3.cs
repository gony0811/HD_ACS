namespace HD.Acs.UI.Primitives;

/// <summary>프레임워크 중립 3D 점/벡터(도면 프레임, m, z-up). 3D 씬·카메라 계산용 최소 연산만 제공.</summary>
public readonly record struct Pt3(double X, double Y, double Z)
{
    public static readonly Pt3 Zero = new(0, 0, 0);
    public static readonly Pt3 UnitZ = new(0, 0, 1);

    public static Pt3 operator +(Pt3 a, Pt3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Pt3 operator -(Pt3 a, Pt3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Pt3 operator -(Pt3 a) => new(-a.X, -a.Y, -a.Z);
    public static Pt3 operator *(Pt3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public static Pt3 operator *(double s, Pt3 a) => a * s;

    public double Dot(Pt3 b) => X * b.X + Y * b.Y + Z * b.Z;
    public Pt3 Cross(Pt3 b) => new(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
    public double LengthSquared => X * X + Y * Y + Z * Z;
    public double Length => Math.Sqrt(LengthSquared);

    /// <summary>단위 벡터. 영벡터면 그대로 반환.</summary>
    public Pt3 Normalized()
    {
        double l = Length;
        return l > 1e-12 ? this * (1.0 / l) : this;
    }

    public static Pt3 FromArray(double[] a) => new(a[0], a[1], a[2]);
}
