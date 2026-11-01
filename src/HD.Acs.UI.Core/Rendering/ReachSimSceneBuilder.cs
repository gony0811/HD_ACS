using HD.Acs.UI.Models;
using HD.Acs.UI.Primitives;

namespace HD.Acs.UI.Rendering;

/// <summary>리치·간섭 씬 입력 — 탱크 면, 상세 결과, 보여 줄 자세·해, 단면 자르기.</summary>
/// <param name="StageIndex">0 ② 접근 · 1 정렬 준비 · 2 초점 검사.</param>
/// <param name="SolutionIndex">그릴 IK 해 순번 — null 이면 운영 선택 해.</param>
/// <param name="ClipSection">차체 좌측(카메라 쪽) 탱크 면을 정차점 옆에서 잘라 단면이 보이게 한다.</param>
public sealed record ReachSceneInput(
    IReadOnlyList<WallDto> Walls, ReachDetailDto? Detail, int StageIndex, int? SolutionIndex, bool ClipSection);

/// <summary>
/// 리치·간섭 3D 씬 — 탱크 면(반투명, 단면 자르기), 영역 용접선, AMR 차체, 리프트·코봇 링크·검사 툴 캡슐, 관절 라벨.
/// 부품은 탱크 면까지 여유로 색을 나눈다(간섭 빨강 · 주의 주황). 좌표는 도면 m(서버 계산값 그대로).
/// </summary>
public static class ReachSimSceneBuilder
{
    public static readonly Rgba WallFill = Rgba.FromArgb(0x38, 0x9A, 0xA7, 0xB3);
    public static readonly Rgba WallActiveFill = Rgba.FromArgb(0x55, 0x4F, 0xB3, 0xBD);
    public static readonly Rgba WallEdge = Rgba.FromArgb(0x90, 0x9A, 0xA7, 0xB3);
    public static readonly Rgba DeckFill = Rgba.FromArgb(0x30, 0x7A, 0x85, 0x91);
    public static readonly Rgba Robot = Rgba.FromRgb(0x2F, 0x9C, 0xA6);
    public static readonly Rgba Column = Rgba.FromRgb(0x7A, 0x85, 0x91);
    public static readonly Rgba Tool = Rgba.FromRgb(0xD8, 0xDE, 0xE3);
    public static readonly Rgba Body = Rgba.FromArgb(0xD0, 0x6B, 0x77, 0x83);
    public static readonly Rgba Warn = Rgba.FromRgb(0xE3, 0xA2, 0x3B);
    public static readonly Rgba Hit = Rgba.FromRgb(0xEF, 0x6A, 0x5E);
    public static readonly Rgba Seam = Rgba.FromRgb(0xB0, 0x8A, 0x2A);
    public static readonly Rgba SeamActive = Rgba.FromRgb(0xF0, 0xB4, 0x00);
    public static readonly Rgba LabelText = Rgba.FromRgb(0xEC, 0xF0, 0xF1);

    /// <summary>단면 자르기 — 차체 좌측으로 이 거리[m]보다 먼 탱크 면은 그리지 않는다(카메라가 그쪽에서 본다).</summary>
    public const double ClipOffsetM = 0.9;
    private const int CapsuleSides = 10;

    /// <summary>선택한 자세의 해(운영 선택 또는 지정 순번). 없으면 null.</summary>
    public static ReachSolutionDto? SolutionOf(ReachDetailDto d, int stageIndex, int? solutionIndex)
    {
        if (stageIndex < 0 || stageIndex >= d.Stages.Count) return null;
        var st = d.Stages[stageIndex];
        var i = solutionIndex is { } s && s >= 0 && s < st.Solutions.Count ? s : st.ChosenIndex;
        return i >= 0 && i < st.Solutions.Count ? st.Solutions[i] : null;
    }

    public static Scene3 Build(ReachSceneInput input)
    {
        var scene = new Scene3();
        var d = input.Detail;
        if (d is null) return scene;

        // 단면 자르기 평면: side·p ≥ side·station − ClipOffset 쪽만 남긴다.
        var side = new Pt3(-Math.Sin(d.StationYaw), Math.Cos(d.StationYaw), 0);
        var station = new Pt3(d.StationX, d.StationY, d.FloorZ);
        Func<Pt3, double>? keep = input.ClipSection ? p => side.Dot(p - station) + ClipOffsetM : null;

        foreach (var w in input.Walls)
        {
            if (w.Origin is not { Length: 3 } o || w.UAxis is not { Length: 3 } u || w.VAxis is not { Length: 3 } v) continue;
            var p0 = Pt3.FromArray(o);
            var pu = Pt3.FromArray(u) * w.ULen;
            var pv = Pt3.FromArray(v) * w.VLen;
            var quad = Clip(new[] { p0, p0 + pu, p0 + pu + pv, p0 + pv }, keep);
            if (quad.Count < 3) continue;
            var active = w.WallCode == d.WallCode;
            scene.Faces.Add(new Face3(quad, active ? WallActiveFill : WallFill, WallEdge, 1.0, Shade: false));
        }
        if (Deck(input.Walls, d.FloorZ) is { } deck && Clip(deck, keep) is { Count: >= 3 } deckClipped)
            scene.Faces.Add(new Face3(deckClipped, DeckFill, WallEdge, 0.8, Shade: false));

        foreach (var s in d.Seams)
        {
            var a = Pt3.FromArray(s.Start);
            var b = Pt3.FromArray(s.End);
            if (s.TaskId == d.TaskId) scene.Segments.Add(new Segment3(a, b, SeamActive, 4));
            else scene.Segments.Add(new Segment3(a, b, Seam, 1.5));
        }
        var target = Pt3.FromArray(d.Target);
        scene.Markers.Add(new Marker3(target, SeamActive, RadiusWorld: 0.025, Stroke: LabelText));

        AddBody(scene, d);
        foreach (var c in d.Body.Corners) scene.ExtentPoints.Add(Pt3.FromArray(c));
        scene.ExtentPoints.Add(target);

        if (SolutionOf(d, input.StageIndex, input.SolutionIndex) is not { } sol) return scene;
        var warn = d.WarnClearanceM;
        foreach (var p in sol.Parts)
        {
            // 검사 툴은 끝이 평평한 원통 외곽(판정과 같게)이고, 안의 손목이 보이게 반투명으로 그린다.
            var tool = p.Name.StartsWith("검사 툴", StringComparison.Ordinal);
            var color = p.ClearanceM < 0 ? Hit : p.ClearanceM < warn ? Warn
                : p.Name == "리프트 기둥" ? Column : tool ? Tool : Robot;
            AddCapsule(scene, Pt3.FromArray(p.A), Pt3.FromArray(p.B), p.RadiusM, tool ? color with { A = 0x70 } : color, flat: tool);
            scene.ExtentPoints.Add(Pt3.FromArray(p.A));
            scene.ExtentPoints.Add(Pt3.FromArray(p.B));
        }
        var tip = sol.Parts.Count > 0 ? Pt3.FromArray(sol.Parts[^1].B) : target;
        var focus = Pt3.FromArray(sol.Focus);
        scene.Segments.Add(new Segment3(tip, focus, LabelText, 1));
        scene.Markers.Add(new Marker3(focus, LabelText, RadiusPx: 3));

        foreach (var j in sol.Joints)
        {
            var pos = Pt3.FromArray(j.Position);
            var near = sol.Parts.Where(p => p.Name.Contains(j.Name, StringComparison.Ordinal)).Select(p => p.ClearanceM)
                .DefaultIfEmpty(double.PositiveInfinity).Min();
            var c = near < 0 ? Hit : near < warn ? Warn : LabelText;
            scene.Markers.Add(new Marker3(pos, c, RadiusPx: 3.5));
            scene.Labels.Add(new Label3(pos + new Pt3(0, 0, 0.09), j.Name, c, 11));
        }
        return scene;
    }

    private static void AddBody(Scene3 scene, ReachDetailDto d)
    {
        if (d.Body.Corners is not { Length: 8 } c) return;
        var p = c.Select(Pt3.FromArray).ToArray();
        var fill = d.Body.ClearanceM < 0 ? Hit with { A = 0xD0 } : d.Body.ClearanceM < d.WarnClearanceM ? Warn with { A = 0xD0 } : Body;
        int[][] faces =
        {
            new[] { 4, 5, 6, 7 },              // 윗면
            new[] { 0, 1, 5, 4 },              // 앞면(벽 쪽)
            new[] { 1, 2, 6, 5 }, new[] { 2, 3, 7, 6 }, new[] { 3, 0, 4, 7 },
        };
        foreach (var f in faces)
            scene.Faces.Add(new Face3(f.Select(i => p[i]).ToArray(), fill, Rgba.FromArgb(0xC0, 0x2B, 0x36, 0x40), 1.0));
    }

    /// <summary>캡슐을 다각 기둥으로 근사(옆면만) — 끝은 마커 원으로 둥글게 보인다. flat 이면 끝을 평평한 면으로 닫는다(원통).</summary>
    private static void AddCapsule(Scene3 scene, Pt3 a, Pt3 b, double r, Rgba color, bool flat = false)
    {
        var axis = b - a;
        if (axis.LengthSquared < 1e-12)
        {
            scene.Markers.Add(new Marker3(a, color, RadiusWorld: r));
            return;
        }
        var n = axis.Normalized();
        var refAxis = Math.Abs(n.Z) < 0.9 ? Pt3.UnitZ : new Pt3(1, 0, 0);
        var e1 = n.Cross(refAxis).Normalized();
        var e2 = n.Cross(e1).Normalized();
        var ring = Enumerable.Range(0, CapsuleSides).Select(i =>
        {
            var t = 2 * Math.PI * i / CapsuleSides;
            return e1 * (Math.Cos(t) * r) + e2 * (Math.Sin(t) * r);
        }).ToArray();
        for (var i = 0; i < CapsuleSides; i++)
        {
            var k = (i + 1) % CapsuleSides;
            scene.Faces.Add(new Face3(new[] { a + ring[i], a + ring[k], b + ring[k], b + ring[i] }, color, null, 0));
        }
        if (flat)
        {
            scene.Faces.Add(new Face3(ring.Select(v => a + v).ToArray(), color, null, 0));
            scene.Faces.Add(new Face3(ring.Select(v => b + v).ToArray(), color, null, 0));
            return;
        }
        scene.Markers.Add(new Marker3(a, color, RadiusWorld: r));
        scene.Markers.Add(new Marker3(b, color, RadiusWorld: r));
    }

    /// <summary>층 데크 — 그 높이의 단면 폭(경사·수직벽 경계) × 선창 길이(격벽 사이).</summary>
    private static Pt3[]? Deck(IReadOnlyList<WallDto> walls, double z)
    {
        double lo = double.NegativeInfinity, hi = double.PositiveInfinity, x0 = double.NegativeInfinity, x1 = double.PositiveInfinity;
        foreach (var w in walls)
        {
            if (w.Normal is not { Length: 3 } n || w.Origin is not { Length: 3 } o) continue;
            if (Math.Abs(n[0]) > 0.5)
            {
                if (n[0] > 0) x0 = Math.Max(x0, o[0]); else x1 = Math.Min(x1, o[0]);
                continue;
            }
            if (Math.Abs(n[1]) < 1e-6) continue;
            var y = o[1] - n[2] * (z - o[2]) / n[1];   // n·(p−o) = 0 → 그 높이의 경계 y
            if (n[1] > 0) lo = Math.Max(lo, y); else hi = Math.Min(hi, y);
        }
        if (!double.IsFinite(lo) || !double.IsFinite(hi) || !double.IsFinite(x0) || !double.IsFinite(x1) || hi <= lo) return null;
        return new[] { new Pt3(x0, lo, z), new Pt3(x1, lo, z), new Pt3(x1, hi, z), new Pt3(x0, hi, z) };
    }

    /// <summary>Sutherland–Hodgman — keep(p) ≥ 0 인 쪽만 남긴다. keep 이 null 이면 그대로.</summary>
    private static IReadOnlyList<Pt3> Clip(IReadOnlyList<Pt3> poly, Func<Pt3, double>? keep)
    {
        if (keep is null) return poly;
        var output = new List<Pt3>(poly.Count + 2);
        for (var i = 0; i < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];
            double da = keep(a), db = keep(b);
            if (da >= 0) output.Add(a);
            if ((da >= 0) != (db >= 0)) output.Add(a + (b - a) * (da / (da - db)));
        }
        return output;
    }
}
