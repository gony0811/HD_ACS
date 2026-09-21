namespace HD.Acs.UI.Primitives;

/// <summary>
/// 프레임워크 중립 2D 점(캔버스 px 등). VM이 Avalonia.Point 등 프레임워크 타입 대신 노출하는 타입.
/// 각 UI 헤드가 자기 프레임워크의 Points 컬렉션으로 변환한다(Avalonia 헤드: PointsConverter).
/// </summary>
public readonly record struct Pt2(double X, double Y);
