namespace HD.Acs.UI.Primitives;

/// <summary>
/// 프레임워크 중립 2D 점(캔버스 px 등). VM이 System.Windows.Point/Avalonia.Point 대신 노출하는 타입.
/// 각 UI 헤드가 자기 프레임워크의 Points 컬렉션으로 변환한다(WPF: PointsConverter).
/// </summary>
public readonly record struct Pt2(double X, double Y);
