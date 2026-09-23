using System.IO;
using HD.Acs.UI.Models;
using HD.Acs.UI.Primitives;
using netDxf;
using netDxf.Entities;

namespace HD.Acs.UI.Drawing;

/// <summary>
/// 면(작업 평면) CAD(DXF) 가져오기 — KC-2B 멤브레인 시트 레이어에서 **용접라인만** 추출한다.
/// 도면에 용접선/Corrugation 구분 신호(선종류·색·레이어)가 없어 길이 임계로 구조 seam(길다)만 남기고
/// Corrugation(짧다)은 제외한다. 좌표는 도면 mm를 면-로컬 mm(bbox 좌하단=원점, y 위로)로 **평행이동만** 한다
/// (팔각기둥/면 치수는 파라미터 유지 — 축척 변경 없음). netDxf(net6.0) 의존은 이 서비스에 국한(프레임워크 중립 Core).
/// </summary>
public static class DxfFaceImport
{
    /// <summary>멤브레인 시트 패턴(용접선·Corrugation)이 담긴 표준 레이어명.</summary>
    public const string MembraneLayer = "KC-2B Membrane Sheet UM";

    /// <summary>용접라인 최소 길이(mm) — 이보다 길면 용접라인으로 읽고, 짧으면 Corrugation으로 보아 제외한다.
    /// 도면상 둘이 같은 레이어·색·선종류라 기하(길이)로만 구분 가능.</summary>
    public const double DefaultWeldMinLenMm = 500;

    /// <summary>가져오기 결과 — 면-로컬 선분 + 분류 집계 + 원본 bbox 크기(mm).</summary>
    /// <remarks><see cref="OutlineFit"/>: <b>corrugation 포함</b> 외곽으로 역산한 팔각 단면(마구리 역산용).
    /// 저장·표시 선분은 용접선만이지만, 마구리 챔퍼 각(θ)을 45°로 견고하게 얻으려면 코너까지 채우는
    /// corrugation 외곽이 필요하다 — 이 값만 그 목적에 쓴다(비면/퇴화 시 null).</remarks>
    public sealed record FaceImport(
        string WallCode, string SourceFile, IReadOnlyList<FaceCadSeg> Segments,
        int WeldCount, int CorrCount, double WidthMm, double HeightMm,
        TankReconstruct.OctagonFit? OutlineFit = null);

    /// <summary>DXF 한 장을 읽어 멤브레인 레이어 선분을 면-로컬 mm로 추출·분류한다.</summary>
    public static FaceImport Load(string wallCode, string path, double weldMinLenMm = DefaultWeldMinLenMm)
    {
        var doc = DxfDocument.Load(path)
            ?? throw new InvalidDataException($"DXF를 읽을 수 없습니다(형식 오류): {Path.GetFileName(path)}");

        var raw = new List<(Pt2 A, Pt2 B)>();
        foreach (var ln in doc.Entities.Lines)
        {
            if (!IsMembrane(ln.Layer?.Name)) continue;
            raw.Add((new Pt2(ln.StartPoint.X, ln.StartPoint.Y), new Pt2(ln.EndPoint.X, ln.EndPoint.Y)));
        }
        foreach (var pl in doc.Entities.Polylines2D)
        {
            if (!IsMembrane(pl.Layer?.Name)) continue;
            var vs = pl.Vertexes;
            for (int i = 0; i + 1 < vs.Count; i++)
                raw.Add((new Pt2(vs[i].Position.X, vs[i].Position.Y),
                         new Pt2(vs[i + 1].Position.X, vs[i + 1].Position.Y)));
            if (pl.IsClosed && vs.Count > 2)
                raw.Add((new Pt2(vs[^1].Position.X, vs[^1].Position.Y),
                         new Pt2(vs[0].Position.X, vs[0].Position.Y)));
        }

        return Classify(wallCode, Path.GetFileName(path), raw, weldMinLenMm);
    }

    /// <summary>
    /// 순수 분류·평행이동(테스트 대상) — 도면 mm 선분을 면-로컬(bbox 좌하단=원점) mm로 옮기고
    /// 길이 임계로 용접선/Corrugation을 1차 분류한다.
    /// </summary>
    public static FaceImport Classify(string wallCode, string sourceFile,
        IReadOnlyList<(Pt2 A, Pt2 B)> raw, double weldMinLenMm = DefaultWeldMinLenMm)
    {
        if (raw.Count == 0)
            return new FaceImport(wallCode, sourceFile, Array.Empty<FaceCadSeg>(), 0, 0, 0, 0);

        // 중복 선분 제거 — 도면이 용접선을 같은 좌표로 두 번씩 그려(마구리 등) 두 줄로 보이던 문제 해소.
        // 1mm 양자화 + 방향 무관 정규화로 완전히 겹치는 선분을 하나로 합친다(구분선 간격 ≥360mm라 오병합 없음).
        static long Q(double v) => (long)Math.Round(v);
        var seen = new HashSet<(long, long, long, long)>();
        var uniq = new List<(Pt2 A, Pt2 B)>(raw.Count);
        foreach (var (a, b) in raw)
        {
            long ax = Q(a.X), ay = Q(a.Y), bx = Q(b.X), by = Q(b.Y);
            var key = (ax < bx || (ax == bx && ay <= by)) ? (ax, ay, bx, by) : (bx, by, ax, ay);
            if (seen.Add(key)) uniq.Add((a, b));
        }

        double minX = uniq.Min(s => Math.Min(s.A.X, s.B.X));
        double minY = uniq.Min(s => Math.Min(s.A.Y, s.B.Y));
        double maxX = uniq.Max(s => Math.Max(s.A.X, s.B.X));
        double maxY = uniq.Max(s => Math.Max(s.A.Y, s.B.Y));

        // 용접라인만 읽어온다 — 길이 임계 이상(구조 seam)만 남기고, 그 미만(Corrugation)은 제외.
        var segs = new List<FaceCadSeg>(uniq.Count);
        int weld = 0, dropped = 0;
        foreach (var (a, b) in uniq)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            if (Math.Sqrt(dx * dx + dy * dy) < weldMinLenMm) { dropped++; continue; }   // Corrugation 제외
            weld++;
            segs.Add(new FaceCadSeg(a.X - minX, a.Y - minY, b.X - minX, b.Y - minY, nameof(DrawType.WeldLine)));
        }

        // corrugation 포함 외곽으로 팔각 단면 역산(마구리 챔퍼 각 45° 견고화용) — 저장/표시엔 미사용.
        //  용접선만으로는 챔퍼가 만나는 바닥/천장 코너가 비어 좌변 y-구간이 어긋나 θ가 47°로 근사되던 문제를
        //  코너를 채우는 corrugation 끝점까지 포함해 해소한다. 좌표계는 위 segs와 동일(면-로컬, minX/minY 원점).
        var fullPts = new List<Pt2>(uniq.Count * 2);
        foreach (var (a, b) in uniq)
        {
            fullPts.Add(new Pt2(a.X - minX, a.Y - minY));
            fullPts.Add(new Pt2(b.X - minX, b.Y - minY));
        }
        var outlineFit = TankReconstruct.FitOctagon(fullPts);

        // CorrCount = 제외된 Corrugation 선 수(참고 표시용).
        return new FaceImport(wallCode, sourceFile, segs, weld, dropped, maxX - minX, maxY - minY, outlineFit);
    }

    private static bool IsMembrane(string? layer) =>
        layer is not null && string.Equals(layer.Trim(), MembraneLayer, StringComparison.OrdinalIgnoreCase);
}
