using HD.Acs.UI.Drawing;
using HD.Acs.UI.Primitives;
using HD.Acs.UI.ViewModels;
using Xunit;

namespace HD.Acs.UI.Core.Tests;

/// <summary>면 드로잉 순수 기하 + VM(확정·되돌리기·직렬화) 골든.</summary>
public class DrawGeometryTests
{
    private static readonly FaceRect Face = new(1000, 600);   // mm

    // ── 표 모드 격자 — 온전한 칸 단위 ──
    [Fact]
    public void BuildGrid_CompleteCells_IncludesBoundaryLines_ClipsRemainder()
    {
        // Xrange=100/pitch30 → 3칸, Yrange=60/pitch25 → 2칸. 경계선 포함 = (3+1)+(2+1)=7선.
        var (cellsX, cellsY, segs) = DrawGeometry.BuildGrid(new Pt2(0, 0), new Pt2(100, 60), 30, 25);
        Assert.Equal(3, cellsX);
        Assert.Equal(2, cellsY);
        Assert.Equal(7, segs.Count);
        // 첫 세로선 = 경계 i=0 (x=0), 온전한 칸 높이 0→50(자투리 10 버림)
        Assert.Equal(new DrawSeg(new Pt2(0, 0), new Pt2(0, 50)), segs[0]);
        Assert.All(segs, s => Assert.True(Math.Max(s.A.Y, s.B.Y) <= 50 + 1e-9));   // 자투리 제외
    }

    [Fact]
    public void BuildGrid_NoCompleteCell_ProducesNothing()
    {
        // Yrange=10 < pitchY=25 → 온전한 칸 0 → 격자 전체 없음(미리보기·확정 모두 없음)
        var (cellsX, cellsY, segs) = DrawGeometry.BuildGrid(new Pt2(0, 0), new Pt2(100, 10), 30, 25);
        Assert.Equal(3, cellsX);
        Assert.Equal(0, cellsY);
        Assert.Empty(segs);
    }

    [Fact]
    public void BuildGrid_NegativeDirection_PlacesBoundariesTowardCursor()
    {
        var (cellsX, cellsY, segs) = DrawGeometry.BuildGrid(new Pt2(100, 0), new Pt2(0, -60), 30, 25);
        Assert.Equal(3, cellsX);
        Assert.Equal(2, cellsY);
        var xs = segs.Where(s => s.A.X == s.B.X).Select(s => s.A.X).OrderByDescending(x => x).ToArray();
        Assert.Equal(new[] { 100.0, 70, 40, 10 }, xs);   // i=0..3 커서 방향(−x)
    }

    // ── Step 이동 단위 · 최근접 점 스냅 ──
    [Fact]
    public void StepSnap_RoundsToGrid_ZeroAxisUnchanged()
    {
        Assert.Equal(new Pt2(95, 50), DrawGeometry.StepSnap(new Pt2(97, 52), 5, 25));   // 97→95(가장 가까운 5의 배수)
        Assert.Equal(new Pt2(100, 50), DrawGeometry.StepSnap(new Pt2(98, 52), 5, 25));  // 98→100
        Assert.Equal(new Pt2(97, 50), DrawGeometry.StepSnap(new Pt2(97, 52), 0, 25));   // X축 연속(step 0)
    }

    [Fact]
    public void NearestWithin_ReturnsClosestInsideTolerance_ElseNull()
    {
        var pts = new[] { new Pt2(0, 0), new Pt2(100, 0), new Pt2(100, 100) };
        Assert.Equal(new Pt2(100, 0), DrawGeometry.NearestWithin(new Pt2(96, 3), pts, 10));
        Assert.Null(DrawGeometry.NearestWithin(new Pt2(50, 50), pts, 10));
    }

    [Fact]
    public void Nearest_ReturnsClosestPointAndDistance_NullIfEmpty()
    {
        var pts = new[] { new Pt2(0, 0), new Pt2(100, 0) };   // 확정 선 두 끝점
        var r = DrawGeometry.Nearest(new Pt2(130, 40), pts);
        Assert.NotNull(r);
        Assert.Equal(new Pt2(100, 0), r!.Value.Point);
        Assert.Equal(Math.Sqrt(30 * 30 + 40 * 40), r.Value.Distance, 6);   // 50
        Assert.Null(DrawGeometry.Nearest(new Pt2(1, 1), System.Array.Empty<Pt2>()));
    }

    [Fact]
    public void NearestVertex_FindsClosestPolygonVertex_WithDeltas_NullIfEmpty()
    {
        // 팔각(마구리) 윤곽 정점 중 챔퍼 모서리 하나가 최근접이어야 함(사각형 4꼭짓점이 아님)
        var octa = new[]
        {
            new Pt2(200, 0), new Pt2(800, 0), new Pt2(1000, 200), new Pt2(1000, 400),
            new Pt2(800, 600), new Pt2(200, 600), new Pt2(0, 400), new Pt2(0, 200),
        };
        var r = DrawGeometry.NearestVertex(new Pt2(210, 20), octa);
        Assert.NotNull(r);
        Assert.Equal(new Pt2(200, 0), r!.Value.Point);   // bbox 좌하단(0,0)이 아니라 챔퍼 정점
        Assert.Equal(10, r.Value.Dx);
        Assert.Equal(20, r.Value.Dy);
        Assert.Null(DrawGeometry.NearestVertex(new Pt2(1, 1), System.Array.Empty<Pt2>()));
    }

    // ── 선분 교차(교착점) ──
    [Fact]
    public void SegmentIntersection_Crossing_ReturnsPoint_ParallelOrDisjoint_Null()
    {
        // X자 교차 → 중앙 (50,50)
        Assert.Equal(new Pt2(50, 50),
            DrawGeometry.SegmentIntersection(new DrawSeg(new Pt2(0, 0), new Pt2(100, 100)),
                                             new DrawSeg(new Pt2(0, 100), new Pt2(100, 0))));
        // 끝점 접촉(T자) 포함
        Assert.Equal(new Pt2(50, 0),
            DrawGeometry.SegmentIntersection(new DrawSeg(new Pt2(0, 0), new Pt2(100, 0)),
                                             new DrawSeg(new Pt2(50, 0), new Pt2(50, 50))));
        // 평행 → null
        Assert.Null(DrawGeometry.SegmentIntersection(new DrawSeg(new Pt2(0, 0), new Pt2(100, 0)),
                                                     new DrawSeg(new Pt2(0, 10), new Pt2(100, 10))));
        // 직선상 교차하나 선분 범위 밖 → null
        Assert.Null(DrawGeometry.SegmentIntersection(new DrawSeg(new Pt2(0, 0), new Pt2(10, 0)),
                                                     new DrawSeg(new Pt2(50, -10), new Pt2(50, 10))));
    }

    [Fact]
    public void Vm_Intersections_ClassifiesWeldWeld_AndCorrugationHV()
    {
        var vm = new FaceDrawingViewModel("SL", 1000, 600);
        // 용접선 두 개(수평·수직)가 (500,300)에서 교차
        vm.SelectedType = DrawType.WeldLine;
        vm.CommitLine(new Pt2(0, 300), new Pt2(1000, 300));
        vm.CommitLine(new Pt2(500, 0), new Pt2(500, 600));
        // Corrugation 격자(가로·세로선 다수)
        vm.SelectedType = DrawType.Corrugation;
        vm.SelectedMode = DrawMode.Table;
        vm.PitchX = 250; vm.PitchY = 200;
        vm.CommitGrid(new Pt2(0, 0), new Pt2(1000, 600));

        var xs = vm.Intersections();
        Assert.Contains(xs, x => x.Kind == IntersectionKind.WeldWeld && x.Point == new Pt2(500, 300));
        Assert.Contains(xs, x => x.Kind == IntersectionKind.WeldCorrugationH);   // 수직 용접선 × 가로(수평) 격자선
        Assert.Contains(xs, x => x.Kind == IntersectionKind.WeldCorrugationV);   // 수평 용접선 × 세로(수직) 격자선

        vm.WeldVisible = false;   // 용접선 숨기면 교착점 없음
        Assert.Empty(vm.Intersections());
    }

    // ── ALT 엣지 스냅 ──
    [Fact]
    public void SnapToNearestEdge_ProjectsToClosestSide()
    {
        Assert.Equal(new Pt2(0, 300), DrawGeometry.SnapToNearestEdge(new Pt2(40, 300), Face));    // 좌측
        Assert.Equal(new Pt2(1000, 300), DrawGeometry.SnapToNearestEdge(new Pt2(950, 300), Face)); // 우측
        Assert.Equal(new Pt2(500, 0), DrawGeometry.SnapToNearestEdge(new Pt2(500, 20), Face));     // 하단
        Assert.Equal(new Pt2(500, 600), DrawGeometry.SnapToNearestEdge(new Pt2(500, 580), Face));  // 상단
    }

    // ── SHIFT 직교 구속 ──
    [Fact]
    public void OrthoConstrain_KeepsAxisWithLargerDelta()
    {
        var from = new Pt2(100, 100);
        Assert.Equal(new Pt2(300, 100), DrawGeometry.OrthoConstrain(from, new Pt2(300, 130))); // 수평
        Assert.Equal(new Pt2(100, 400), DrawGeometry.OrthoConstrain(from, new Pt2(120, 400))); // 수직
    }

    [Fact]
    public void OrthoThenSnap_ConstrainsThenSnapsFreeAxisToEdge()
    {
        var from = new Pt2(100, 100);
        // 수평 구속(y=100 고정) 후 자유축 X를 좌측(0)으로 스냅
        Assert.Equal(new Pt2(0, 100), DrawGeometry.OrthoThenSnap(from, new Pt2(200, 130), Face));
    }

    // ── 최근접 꼭짓점(HUD) ──
    [Fact]
    public void FindNearestCorner_ReturnsCornerAndDeltas()
    {
        var nc = DrawGeometry.FindNearestCorner(new Pt2(50, 40), Face);
        Assert.Equal(Corner.BottomLeft, nc.Corner);
        Assert.Equal("좌하단", nc.Label);
        Assert.Equal(50, nc.Dx);
        Assert.Equal(40, nc.Dy);
        Assert.Equal(Math.Sqrt(50 * 50 + 40 * 40), nc.Distance, 6);

        Assert.Equal(Corner.TopRight, DrawGeometry.FindNearestCorner(new Pt2(980, 590), Face).Corner);
    }

    // ── VM: 확정·되돌리기·직렬화 ──
    [Fact]
    public void Vm_CommitLine_ThenUndoRedo()
    {
        var vm = new FaceDrawingViewModel("SL", 1000, 600);
        vm.CommitLine(new Pt2(0, 0), new Pt2(100, 0));
        Assert.Single(vm.Shapes);
        Assert.True(vm.CanUndo);

        vm.UndoCommand.Execute(null);
        Assert.Empty(vm.Shapes);
        Assert.True(vm.CanRedo);

        vm.RedoCommand.Execute(null);
        Assert.Single(vm.Shapes);
    }

    [Fact]
    public void Vm_CommitGrid_UsesPitch_AndSerializesContract()
    {
        var vm = new FaceDrawingViewModel("SL", 1000, 600)
        {
            SelectedType = DrawType.Corrugation,
            SelectedMode = DrawMode.Table,
            PitchX = 50,
            PitchY = 50,
        };
        var shape = vm.CommitGrid(new Pt2(0, 0), new Pt2(200, 100));
        Assert.NotNull(shape);
        Assert.Equal(4, shape!.CountX);   // 200/50
        Assert.Equal(2, shape.CountY);    // 100/50

        string json = vm.SerializeJson();
        Assert.Contains("\"타입\": \"Corrugation\"", json);
        Assert.Contains("\"X간격\": 50", json);
        Assert.Contains("\"개수\"", json);
    }

    [Fact]
    public void Vm_MoveSelected_TranslatesAllSegments()
    {
        var vm = new FaceDrawingViewModel("SL", 1000, 600);
        var s = vm.CommitLine(new Pt2(10, 10), new Pt2(20, 10))!;
        vm.Selected = s;
        vm.MoveSelected(5, 7);
        Assert.Equal(new Pt2(15, 17), vm.Shapes[0].Anchor);
        Assert.Equal(new Pt2(25, 17), vm.Shapes[0].Extent);
    }

    [Fact]
    public void Vm_CommitGrid_NoCompleteCell_Rejected()
    {
        var vm = new FaceDrawingViewModel("SL", 1000, 600) { SelectedMode = DrawMode.Table, PitchX = 300, PitchY = 300 };
        Assert.Null(vm.CommitGrid(new Pt2(0, 0), new Pt2(200, 100)));   // 범위 < 간격 → 온전한 칸 0
        Assert.Empty(vm.Shapes);
        Assert.Contains("온전한 칸", vm.HudWarning);
    }

    [Fact]
    public void Vm_Endpoints_CollectsSegmentEnds_OfVisibleLayers()
    {
        var vm = new FaceDrawingViewModel("SL", 1000, 600);
        vm.CommitLine(new Pt2(10, 10), new Pt2(90, 10));
        Assert.Contains(new Pt2(10, 10), vm.Endpoints());
        Assert.Contains(new Pt2(90, 10), vm.Endpoints());

        vm.WeldVisible = false;   // 숨긴 레이어는 스냅 대상에서 제외
        Assert.Empty(vm.Endpoints());
    }

    [Fact]
    public void Vm_ExtendGridsToFace_StretchesGridLinesToBoundary()
    {
        var vm = new FaceDrawingViewModel("SL", 1000, 600)
        {
            SelectedType = DrawType.Corrugation, SelectedMode = DrawMode.Table, PitchX = 100, PitchY = 100,
        };
        // (200,100)~(400,300) 영역에 격자(면 경계 미도달)
        vm.CommitGrid(new Pt2(200, 100), new Pt2(400, 300));
        vm.ExtendGridsToFaceCommand.Execute(null);

        var segs = vm.Shapes[0].Segments;
        // 세로선은 y[0,600] 전체 높이, 가로선은 x[0,1000] 전체 폭으로 연장
        Assert.Contains(segs, g => g.A.X == g.B.X && Math.Min(g.A.Y, g.B.Y) == 0 && Math.Max(g.A.Y, g.B.Y) == 600);
        Assert.Contains(segs, g => g.A.Y == g.B.Y && Math.Min(g.A.X, g.B.X) == 0 && Math.Max(g.A.X, g.B.X) == 1000);
        Assert.All(segs, g => Assert.True(
            (g.A.X == g.B.X && Math.Min(g.A.Y, g.B.Y) == 0 && Math.Max(g.A.Y, g.B.Y) == 600) ||
            (g.A.Y == g.B.Y && Math.Min(g.A.X, g.B.X) == 0 && Math.Max(g.A.X, g.B.X) == 1000)));
    }

    [Fact]
    public void Vm_Mirror_FlipsShapesAboutFaceCenter()
    {
        var vm = new FaceDrawingViewModel("SL", 1000, 600);
        vm.CommitLine(new Pt2(100, 200), new Pt2(300, 200));

        vm.MirrorHorizontalCommand.Execute(null);   // x' = 1000 − x
        Assert.Equal(new Pt2(900, 200), vm.Shapes[0].Segments[0].A);
        Assert.Equal(new Pt2(700, 200), vm.Shapes[0].Segments[0].B);

        vm.MirrorVerticalCommand.Execute(null);      // y' = 600 − y
        Assert.Equal(new Pt2(900, 400), vm.Shapes[0].Segments[0].A);
        Assert.Equal(new Pt2(700, 400), vm.Shapes[0].Segments[0].B);
    }

    [Fact]
    public void Vm_ApplyPitchToSelected_RebuildsGrid()
    {
        var vm = new FaceDrawingViewModel("SL", 1000, 600) { SelectedMode = DrawMode.Table, PitchX = 50, PitchY = 50 };
        var s = vm.CommitGrid(new Pt2(0, 0), new Pt2(200, 100))!;
        vm.Selected = s;
        vm.PitchX = 100; vm.PitchY = 100;
        vm.ApplyPitchToSelectedCommand.Execute(null);
        Assert.Equal(2, vm.Shapes[0].CountX);   // 200/100
        Assert.Equal(1, vm.Shapes[0].CountY);   // 100/100
    }
}
