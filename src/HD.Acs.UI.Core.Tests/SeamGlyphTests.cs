using HD.Acs.UI.Models;
using HD.Acs.UI.Primitives;
using HD.Acs.UI.Rendering;
using HD.Acs.UI.ViewModels;
using Xunit;

namespace HD.Acs.UI.Core.Tests;

/// <summary>
/// 용접선 유형 구분 표시 [SeamGlyph] — LINE/CROSS3(T자·▲·줄기 화살촉·회전 라벨)/CROSS4(十자·■)/가지 없음(점선).
/// 핵심 불변식: 그림은 저장된 가지 좌표로 그린다 → R0과 R180은 화면에서 줄기가 반대쪽을 가리킨다.
/// </summary>
public class SeamGlyphTests
{
    // 전개도와 같은 규약: u=오른쪽, v=위쪽(캔버스 y는 아래로 증가)
    private static (double X, double Y) Proj(double u, double v) => (10 + u * 10, 100 - v * 10);

    private static AreaTaskDto Task(string seamType, double cu, double cv, double[][]? points, int seq = 3) =>
        new(Guid.NewGuid(), seq, null, seamType, cu, cv, cu, cv, "dxf", "prof", points);

    // 중심 (5,5): 줄기 왼쪽, 통과선 위·아래
    private static AreaTaskDto Cross3StemLeft(string rot = "CROSS3_R0") =>
        Task(rot, 5, 5, new[] { new[] { 4.0, 5 }, new[] { 5.0, 6 }, new[] { 5.0, 4 } });

    [Theory]
    [InlineData("LINE", SeamKind.Line)]
    [InlineData("CROSS3_R90", SeamKind.Cross3)]
    [InlineData("cross3", SeamKind.Cross3)]
    [InlineData("CROSS4", SeamKind.Cross4)]
    [InlineData("CROSS", SeamKind.Cross4)]     // legacy 별칭
    [InlineData("CORNER2", SeamKind.Line)]
    [InlineData(null, SeamKind.Line)]
    public void KindOf_ClassifiesSeamTypes(string? seamType, SeamKind expected) =>
        Assert.Equal(expected, SeamGlyph.KindOf(seamType));

    [Fact]
    public void Badge_ShowsTypeRotationAndScreenDirection()
    {
        var line = Task("LINE", 1, 1, null);
        Assert.Equal("3", SeamGlyph.Badge(line, BadgeStyle.Full));

        var t = Cross3StemLeft();
        Assert.Equal("3T R0 (화면 ←)", SeamGlyph.Badge(t, BadgeStyle.Full));
        Assert.Equal("3T R0←", SeamGlyph.Badge(t, BadgeStyle.Compact));
        Assert.Equal("3T R0", SeamGlyph.Badge(t, BadgeStyle.NoScreen));   // 3D — 시점 회전이라 화면 방향 없음

        var c4 = Task("CROSS4", 5, 5, new[] { new[] { 6.0, 5 }, new[] { 5.0, 6 }, new[] { 4.0, 5 }, new[] { 5.0, 4 } }, seq: 5);
        Assert.Equal("5+", SeamGlyph.Badge(c4, BadgeStyle.Full));

        Assert.Equal("3T 가지 미지정", SeamGlyph.Badge(Task("CROSS3_R0", 1, 1, null), BadgeStyle.Full));
        Assert.Equal("5+ ?", SeamGlyph.Badge(Task("CROSS4", 1, 1, null, seq: 5), BadgeStyle.Compact));
        Assert.Equal("3T R0 (화면 ↓)", SeamGlyph.Badge(Task("CROSS3", 5, 5, new[] { new[] { 5.0, 4 }, new[] { 4.0, 5 }, new[] { 6.0, 5 } }), BadgeStyle.Full));
    }

    [Fact]
    public void TypeLabel_MarksCrossTypes_AndMissingArms()
    {
        Assert.Equal("LINE", SeamGlyph.TypeLabel("LINE", null));
        Assert.Equal("▲ CROSS3_R0", Cross3StemLeft().TypeLabel);
        Assert.Equal("▲ CROSS3_R90 · 가지 없음", SeamGlyph.TypeLabel("CROSS3_R90", null));
        Assert.Equal("■ CROSS4 · 가지 없음", SeamGlyph.TypeLabel("CROSS4", new[] { new[] { 1.0, 1 } }));   // 개수 불일치
    }

    [Fact]
    public void Cross3_DrawsTShape_FromStoredArms_WithStemArrowAndTriangleMarker()
    {
        var seg = SeamGlyph.ToSeg(Cross3StemLeft(), Proj, 0, null, BadgeStyle.Full);
        Assert.Equal(SeamKind.Cross3, seg.Kind);
        Assert.Equal(3, seg.Arms!.Count);
        Assert.Equal(new Pt2(60, 50), new Pt2(seg.MidX, seg.MidY));   // 배지 기준 = 교차 중심

        var g = SeamGlyph.Build(seg, markerR: 6, arrowLen: 10);
        Assert.False(g.Dashed);
        Assert.Equal(3, g.Lines.Count);
        var stem = Assert.Single(g.Lines, l => l.IsStem);
        Assert.Equal(new Pt2(60, 50), stem.A);                         // 중심 → 줄기 끝
        Assert.Equal(new Pt2(50, 50), stem.B);
        Assert.Equal(new Pt2(50, 50), g.Arrow![0]);                    // 화살촉 끝 = 줄기 끝
        Assert.Equal(3, g.Marker!.Count);                              // ▲
        Assert.True(g.Marker.Min(p => p.X) < 60 - 5.9);                // 삼각형 꼭짓점이 줄기(왼쪽)를 가리킴
    }

    [Fact]
    public void R0AndR180_PointOppositeWays_OnScreen()
    {
        // 같은 중심, 줄기만 반대 — 회전값이 아니라 좌표로 그리므로 화살촉이 중심의 반대편에 온다
        var left = SeamGlyph.Build(SeamGlyph.ToSeg(Cross3StemLeft("CROSS3_R0"), Proj, 0, null, BadgeStyle.Full));
        var rightTask = Task("CROSS3_R180", 5, 5, new[] { new[] { 6.0, 5 }, new[] { 5.0, 4 }, new[] { 5.0, 6 } });
        var right = SeamGlyph.Build(SeamGlyph.ToSeg(rightTask, Proj, 0, null, BadgeStyle.Full));

        Assert.True(left.Arrow![0].X < 60);
        Assert.True(right.Arrow![0].X > 60);
        Assert.Equal("3T R180 (화면 →)", SeamGlyph.Badge(rightTask, BadgeStyle.Full));
    }

    [Fact]
    public void Cross4_DrawsFourEqualArms_SquareMarker_NoArrow()
    {
        var t = Task("CROSS4", 5, 5, new[] { new[] { 6.0, 5 }, new[] { 5.0, 6 }, new[] { 4.0, 5 }, new[] { 5.0, 4 } });
        var g = SeamGlyph.Build(SeamGlyph.ToSeg(t, Proj, 0, "RUNNING", BadgeStyle.Compact));
        Assert.Equal(4, g.Lines.Count);
        Assert.DoesNotContain(g.Lines, l => l.IsStem);
        Assert.Null(g.Arrow);
        Assert.Equal(4, g.Marker!.Count);   // ■
    }

    [Fact]
    public void CrossWithoutArms_IsDashedStartEndLine_WithMarker()
    {
        var t = new AreaTaskDto(Guid.NewGuid(), 2, null, "CROSS3_R0", 1, 1, 3, 1, "dxf", "prof", null);
        var g = SeamGlyph.Build(SeamGlyph.ToSeg(t, Proj, 0, null, BadgeStyle.Full));
        Assert.True(g.Dashed);
        var l = Assert.Single(g.Lines);
        Assert.Equal(new Pt2(20, 90), l.A);
        Assert.Equal(new Pt2(40, 90), l.B);
        Assert.Equal(3, g.Marker!.Count);
        Assert.Null(g.Arrow);
    }

    [Fact]
    public void Line_IsUnchanged_AndVOffsetAppliesToArms()
    {
        var line = SeamGlyph.Build(SeamGlyph.ToSeg(Task("LINE", 1, 1, null) with { EndU = 3 }, Proj, 0, null, BadgeStyle.Full));
        Assert.False(line.Dashed);
        Assert.Null(line.Marker);
        Assert.Single(line.Lines);

        // 계획 화면: 층-로컬 v + vOffset — 중심과 가지가 같이 올라간다
        var seg = SeamGlyph.ToSeg(Cross3StemLeft(), Proj, 2, null, BadgeStyle.Full);
        Assert.Equal(30, seg.Y1, 9);                  // v=5+2=7 → y=100−70
        Assert.Equal(30, seg.Arms![0].Y, 9);
    }

    [Fact]
    public void Scene3D_DrawsCrossArms_WithThickStem_AndRotationLabel()
    {
        var walls = new List<WallDto>
        {
            new("T1", "SM", new[] { -10.0, -5, 2 }, new[] { 1.0, 0, 0 }, new[] { 0.0, 0, 1 }, new[] { 0.0, 1, 0 }, 20, 4, null, true, null, null),
        };
        var area = new AreaDto(Guid.NewGuid(), "T1", "SM", 1, "A1", 2, 0.5, 6, 2.5, null, null, null, 0, 1,
            Corners: new[] { new[] { 2.0, 0.5 }, new[] { 6.0, 0.5 }, new[] { 6.0, 2.5 }, new[] { 2.0, 2.5 } });
        var cross = new AreaTaskDto(Guid.NewGuid(), 4, null, "CROSS3_R90", 4, 1.5, 4, 1.5, "dxf", "prof",
            new[] { new[] { 4.0, 2.3 }, new[] { 3.0, 1.5 }, new[] { 5.0, 1.5 } });
        var input = new TankSceneInput(walls, Array.Empty<WallDto>(), null,
            new[] { new TankViewModel.AreaOverlay(area, new[] { cross }) }, ShowOverlays: true, SelectedLevel: null,
            _ => null, _ => null, HasRobotPosition: false, RobotPosition: new Pt3(0, 0, 0));
        var scene = TankSceneBuilder.Build(input);

        var weld = TankViewModel.WeldLineColor(null);
        Assert.Equal(3, scene.Segments.Count(s => s.Color == weld));
        Assert.Single(scene.Segments, s => s.Color == weld && s.Thickness == 5.5);   // 줄기만 굵게
        Assert.Contains(scene.Labels, l => l.Text == "4T R90");
    }
}
