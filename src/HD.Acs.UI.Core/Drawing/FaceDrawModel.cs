using HD.Acs.UI.Primitives;

namespace HD.Acs.UI.Drawing;

// 면(작업 평면) 위 인터랙티브 드로잉 도구의 프레임워크 중립 모델.
// 모든 좌표·간격은 실좌표(mm) 기준, 면-로컬 (0,0)=좌하단·y는 위로 증가(수학 좌표계).
// 화면(px) 변환·y 뒤집기는 각 UI 헤드의 캔버스가 담당한다.

/// <summary>그리기 타입 — 색/스타일로 구분.</summary>
public enum DrawType { WeldLine, Corrugation }

/// <summary>그리기 모드 — 선(단일) / 표(등간격 격자).</summary>
public enum DrawMode { Line, Table }

/// <summary>면 사각형 꼭짓점 식별.</summary>
public enum Corner { BottomLeft, BottomRight, TopLeft, TopRight }

/// <summary>실좌표(mm) 선분.</summary>
public readonly record struct DrawSeg(Pt2 A, Pt2 B);

/// <summary>그리기 대상 면(사각형). 원점(0,0)=좌하단, 폭 W·높이 H (mm).</summary>
public readonly record struct FaceRect(double W, double H)
{
    public Pt2 BottomLeft => new(0, 0);
    public Pt2 BottomRight => new(W, 0);
    public Pt2 TopLeft => new(0, H);
    public Pt2 TopRight => new(W, H);

    /// <summary>점을 면 내부로 클램프(경계 밖 선 방지).</summary>
    public Pt2 Clamp(Pt2 p) => new(Math.Clamp(p.X, 0, W), Math.Clamp(p.Y, 0, H));

    /// <summary>점이 면 내부(경계 포함)인지.</summary>
    public bool Contains(Pt2 p) => p.X >= 0 && p.X <= W && p.Y >= 0 && p.Y <= H;
}

/// <summary>면 경계 한 변 너머에 있는 인접 면 표시 — 변 중점(mm)·바깥 방향 단위벡터·면 코드.
/// 캔버스가 변 바깥에 화살표+코드를 그려 "지금 어느 면을 수정 중인지" 알려준다.</summary>
public readonly record struct FaceEdgeLabel(Pt2 Mid, Pt2 OutwardDir, string Code);

/// <summary>커서에서 가장 가까운 면 꼭짓점 + 상대 오프셋(HUD 표시용).</summary>
public readonly record struct NearestCorner(Corner Corner, Pt2 Point, double Dx, double Dy, double Distance)
{
    /// <summary>기준 꼭짓점 한글 식별(예: 좌하단).</summary>
    public string Label => Corner switch
    {
        Corner.BottomLeft => "좌하단",
        Corner.BottomRight => "우하단",
        Corner.TopLeft => "좌상단",
        _ => "우상단",
    };
}

/// <summary>용접선 교착점 유형 — 용접선×용접선 / 용접선×Corrugation(가로) / 용접선×Corrugation(세로).</summary>
public enum IntersectionKind { WeldWeld, WeldCorrugationH, WeldCorrugationV }

/// <summary>용접선 교착점(WeldIntersection) — 위치(mm)와 교차 유형. 후속 로봇 경로/검사점 생성 입력.</summary>
public readonly record struct WeldIntersection(Pt2 Point, IntersectionKind Kind)
{
    /// <summary>유형 한글 라벨(HUD/직렬화 표시).</summary>
    public string Label => Kind switch
    {
        IntersectionKind.WeldWeld => "용접선×용접선",
        IntersectionKind.WeldCorrugationH => "용접선×Corr(가로)",
        _ => "용접선×Corr(세로)",
    };
}

/// <summary>확정된 도형 — 선(1선분) 또는 표(격자 선분 묶음). 실좌표(mm).
/// 직렬화 계약: {타입, 모드, 기준점(Anchor), 방향(Direction), X간격, Y간격, 개수(CountX/CountY)}.
/// 표(Table)의 CountX/CountY = **칸 수(열×행)** — 선 개수는 (CountX+1)+(CountY+1). 선(Line)은 0.</summary>
public sealed record DrawnShape(
    Guid Id,
    DrawType Type,
    DrawMode Mode,
    Pt2 Anchor,
    Pt2 Extent,             // 선: 끝점 / 표: 두 번째 클릭점(격자 대각 코너)
    double PitchX,
    double PitchY,
    int CountX,             // 표: 열(칸) 수
    int CountY,             // 표: 행(칸) 수
    IReadOnlyList<DrawSeg> Segments)
{
    /// <summary>방향 단위벡터(Anchor→Extent). 표는 사분면 부호 방향.</summary>
    public Pt2 Direction
    {
        get
        {
            double dx = Extent.X - Anchor.X, dy = Extent.Y - Anchor.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            return len < 1e-9 ? new Pt2(0, 0) : new Pt2(dx / len, dy / len);
        }
    }
}
