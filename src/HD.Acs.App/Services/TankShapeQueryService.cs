using System.Text.Json;
using HD.Acs.Core.Integration;
using HD.Acs.Core.Planning;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HD.Acs.App.Services;

/// <summary>
/// 선창 형상 조회(pull) API [SAIGE 연동 사양서 v2.6 §4.6] — SAIGE 화면이 ACS와 같은 선창 도면을 그리기 위한 데이터.
/// 조회 전용. **대외 규약**: 길이·좌표 mm 정수, 각도 deg, 층 1-based, 면 = wallId(정수)+wallCode(문자), JSON camelCase.
/// 면-로컬 (u,v)는 내부값 그대로 환산만 한다 — 원점·축 정본은 <see cref="TankGeometry.GenerateWalls"/> [§2.3].
///
/// 운영 UI는 같은 데이터를 내부 규약(m 실수, 법선·facingYaw·정차 오버라이드 등 부가 필드)으로
/// <c>/api/internal/…</c> 경로에서 받는다 — 대외 계약과 내부 화면 요구가 서로를 묶지 않도록 분리했다.
/// </summary>
public sealed class TankShapeQueryService
{
    private readonly AcsDbContext _db;
    public TankShapeQueryService(AcsDbContext db) => _db = db;

    /// <summary>선창 파라미터 + 유도값 [§4.6.1]. 선창이 없으면 null.</summary>
    public async Task<TankShapeGeometry?> GetGeometryAsync(string tankId, CancellationToken ct = default)
    {
        var g = await _db.TankGeometries.AsNoTracking().FirstOrDefaultAsync(x => x.TankId == tankId, ct);
        if (g is null) return null;
        var geom = ToGeom(g);
        var d = geom.Derived();
        return new TankShapeGeometry(g.TankId,
            Mm(g.LengthL), Mm(g.WFloor), Deg(g.ThetaLow), Mm(g.HLow), Mm(g.HWall), Deg(g.ThetaUp), Mm(g.HUp),
            geom.LevelZ.Select(Mm).ToArray(),
            new[] { Mm(g.OriginOx), Mm(g.OriginOy) },
            new TankShapeDerived(Mm(d.B), Mm(d.WCeil), Mm(d.H)));
    }

    /// <summary>
    /// 면 목록 [§4.6.2] — wallId 순. level 지정 시 그 층에서 도달 가능한 면만 + reachableVBand(mm).
    /// 8면은 4점 사각형, 격벽 2면(F·A)은 8점 팔각형 — 수신 측이 계산하지 않도록 outline을 직접 제공한다. 선창이 없으면 null.
    /// </summary>
    public async Task<IReadOnlyList<TankShapeWall>?> GetWallsAsync(string tankId, int? level, CancellationToken ct = default)
    {
        var g = await _db.TankGeometries.AsNoTracking().FirstOrDefaultAsync(x => x.TankId == tankId, ct);
        if (g is null) return null;
        var geom = ToGeom(g);
        var band = level is int lv ? geom.LevelBandList().FirstOrDefault(b => b.Level == lv) : null;
        if (level is not null && band is null) return Array.Empty<TankShapeWall>();   // 존재하지 않는 층 = 도달 가능 면 없음

        var walls = await _db.Walls.AsNoTracking().Where(w => w.TankId == tankId).ToListAsync(ct);
        var result = new List<TankShapeWall>();
        foreach (var w in walls)
        {
            var origin = Vec(w.Origin);
            var vAxis = Vec(w.VAxis);
            int[]? reachable = null;
            if (band is not null)
            {
                var vb = LevelBands.ReachableVBand(origin[2], vAxis[2], w.VLen, band);
                if (vb is null) continue;   // 이 층에서 도달 불가한 면 → 제외
                reachable = new[] { Mm(vb.Value.VLo), Mm(vb.Value.VHi) };
            }

            bool bulkhead = TankGeometry.IsBulkhead(w.WallCode);
            var outline = bulkhead
                ? geom.BulkheadOutline().Select(p => new[] { Mm(p.U), Mm(p.V) }).ToArray()
                : new[] { new[] { 0, 0 }, new[] { Mm(w.ULen), 0 }, new[] { Mm(w.ULen), Mm(w.VLen) }, new[] { 0, Mm(w.VLen) } };

            result.Add(new TankShapeWall(
                WallIds.FromCode(w.WallCode), w.WallCode, Mm(w.ULen), Mm(w.VLen),
                bulkhead ? "POLYGON" : "RECTANGLE", outline,
                origin.Select(Mm).ToArray(), Vec(w.UAxis), vAxis, reachable));
        }
        return result.OrderBy(w => w.WallId).ToList();
    }

    /// <summary>영역 목록 [§4.6.3] — 임의 4점 사각형이라 꼭짓점 배열로 제공. tankId 미지정 = 전 선창.</summary>
    public async Task<IReadOnlyList<TankShapeArea>> GetAreasAsync(string? tankId, int? level, int? wallId, CancellationToken ct = default)
    {
        var q = _db.InspectionAreas.AsNoTracking().AsQueryable();
        if (tankId is not null) q = q.Where(a => a.TankId == tankId);
        if (level is not null) q = q.Where(a => a.Level == level);
        var areas = await q.Select(a => new { a.AreaId, a.TankId, a.Name, a.WallCode, a.Level, a.Corners, a.SortOrder, TaskCount = a.Tasks.Count })
            .ToListAsync(ct);

        return areas
            .Select(a => new { a, WallId = WallIds.FromCode(a.WallCode) })
            .Where(x => wallId is null || x.WallId == wallId)
            .OrderBy(x => x.a.TankId).ThenBy(x => x.WallId).ThenBy(x => x.a.Level).ThenBy(x => x.a.SortOrder).ThenBy(x => x.a.Name)
            .Select(x => new TankShapeArea(x.a.AreaId, x.a.Name, x.a.TankId, x.WallId, x.a.WallCode, x.a.Level, x.a.TaskCount,
                (JsonSerializer.Deserialize<double[][]>(x.a.Corners) ?? Array.Empty<double[]>())
                    .Select(p => new[] { Mm(p[0]), Mm(p[1]) }).ToArray()))
            .ToList();
    }

    /// <summary>
    /// 영역의 검사 작업(용접선) [§4.6.3] — taskId는 촬영 메타데이터(§8.2)·결과 조회(§5.6)와 같은 값이다. 영역이 없으면 null.
    /// </summary>
    public async Task<IReadOnlyList<TankShapeTask>?> GetAreaTasksAsync(Guid areaId, CancellationToken ct = default)
    {
        if (!await _db.InspectionAreas.AsNoTracking().AnyAsync(a => a.AreaId == areaId, ct)) return null;
        var tasks = await _db.AreaTasks.AsNoTracking().Where(t => t.AreaId == areaId).OrderBy(t => t.Seq).ToListAsync(ct);
        return tasks.Select(ToTask).ToList();
    }

    private static TankShapeTask ToTask(AreaTaskEntity t) => new(
        t.TaskId, t.Seq, t.Name, Mm(t.StartU), Mm(t.StartV), Mm(t.EndU), Mm(t.EndV),
        // 면-로컬 (u,v)는 정규직교 프레임이라 평면 거리가 곧 3D 용접선 길이(챔퍼면 포함)
        Mm(Math.Sqrt(Math.Pow(t.EndU - t.StartU, 2) + Math.Pow(t.EndV - t.StartV, 2))),
        t.SeamType);

    private static int Mm(double meters) => SaigeUnits.ToMm(meters);
    private static double Deg(double rad) => Math.Round(rad * 180.0 / Math.PI, 3);
    private static double[] Vec(string json) => JsonSerializer.Deserialize<double[]>(json) ?? Array.Empty<double>();

    private static TankGeometry ToGeom(TankGeometryEntity g) => new(
        g.LengthL, g.WFloor, g.ThetaLow, g.HLow, g.HWall, g.ThetaUp, g.HUp,
        JsonSerializer.Deserialize<double[]>(g.LevelZ) ?? Array.Empty<double>(), g.OriginOx, g.OriginOy,
        g.ReachZMin, g.ReachZMax);
}

/// <summary>§4.6.1 — 길이 mm 정수, 각도 deg. levelZ[n−1] = level n 의 바닥 z (배열만 0-based, §2.6).</summary>
public sealed record TankShapeGeometry(string TankId, int LengthL, int WFloor, double ThetaLow, int HLow, int HWall,
    double ThetaUp, int HUp, int[] LevelZ, int[] OriginOffset, TankShapeDerived Derived);

public sealed record TankShapeDerived(int Beam, int WCeil, int Height);

/// <summary>§4.6.2 — uMax·vMax = 면 크기(mm), outline = 면-로컬 꼭짓점, origin/uAxis/vAxis = 전역 3D 프레임(3D 뷰용).</summary>
public sealed record TankShapeWall(int WallId, string WallCode, int UMax, int VMax, string Shape, int[][] Outline,
    int[] Origin, double[] UAxis, double[] VAxis,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    int[]? ReachableVBand);

public sealed record TankShapeArea(Guid AreaId, string AreaName, string TankId, int WallId, string WallCode, int Level,
    int TaskCount, int[][] Corners);

public sealed record TankShapeTask(Guid TaskId, int Seq, string? Name, int StartU, int StartV, int EndU, int EndV,
    int SeamLength, string SeamType);
