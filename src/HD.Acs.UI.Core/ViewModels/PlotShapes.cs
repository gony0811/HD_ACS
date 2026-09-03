using HD.Acs.UI.Primitives;

namespace HD.Acs.UI.ViewModels;

// 전개도(2D 캔버스) 렌더 레코드 — 좌표는 이미 캔버스 px로 투영된 값.
// AreaPlanningViewModel(계획 캔버스)과 TankViewModel(운영 전개도 FacePlot)이 공유하므로 별도 파일에 둔다.
// 프레임워크 타입(Point/PointCollection) 대신 Pt2 목록을 노출하고, 각 UI 헤드가 컨버터로 변환한다.

/// <summary>(u,v) 캔버스 도형 — 축정렬 박스(레거시, 현재 XAML 미사용).</summary>
public sealed record AreaBox(double Left, double Top, double Width, double Height, string Label);

/// <summary>임의 4점 영역 폴리곤(캔버스 px) — Points=투영된 꼭짓점, 라벨 앵커. Status=work_item 상태(null=계획 표시).</summary>
public sealed record AreaPoly(IReadOnlyList<Pt2> Points, double LabelX, double LabelY, string Label, string? Status = null);

/// <summary>작업(용접선) 선분(캔버스 px) — 끝점 마커·중점 배지 앵커 포함. Status=액션 상태(null=계획 표시, 주황).</summary>
public sealed record TaskSeg(double X1, double Y1, double X2, double Y2, double EndX, double EndY, double MidX, double MidY, string Badge,
    string? Status = null)
{
    // 점 형태 파생값 — Line.StartPoint/EndPoint(Avalonia)처럼 Point를 요구하는 헤드용. WPF는 X1..Y2를 직접 바인딩.
    public Pt2 Start => new(X1, Y1);
    public Pt2 End => new(X2, Y2);
    public Pt2 EndMarker => new(EndX, EndY);
    public Pt2 Mid => new(MidX, MidY);
}

/// <summary>정차점 마커(영역 centroid, 캔버스 px).</summary>
public sealed record StationMarker(double Left, double Top, string Label);

/// <summary>층 필터 선택 항목 [SPEC v3.1 §9]. Level=1-based, Label="L1".</summary>
public sealed record LevelOption(int Level, string Label);
