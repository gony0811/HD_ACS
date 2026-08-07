using System.Text.Json;
using HD.Acs.Core.Planning;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HD.Acs.App.Services;

/// <summary>
/// 선창 파라메트릭 정의 + 면 자동 생성 [SPEC v3 §2/§3]. 파라미터 검증 후 10면(ref.wall)을 재생성한다.
/// </summary>
public sealed class TankGeometryService
{
    private readonly AcsDbContext _db;
    private readonly ILogger<TankGeometryService> _log;

    public TankGeometryService(AcsDbContext db, ILogger<TankGeometryService> log) { _db = db; _log = log; }

    /// <summary>파라미터 검증 실패 — 등록 거부(400) [§2].</summary>
    public sealed class GeometryInvalidException(IReadOnlyList<string> reasons)
        : Exception("선창 파라미터 검증 실패: " + string.Join("; ", reasons))
    {
        public IReadOnlyList<string> Reasons { get; } = reasons;
    }

    /// <summary>파라미터 등록/수정 → 검증 → 10면 자동 생성·저장(기존 면 삭제 후 재삽입). 각도는 rad 입력.</summary>
    public async Task<int> RegisterAsync(string tankId, TankGeometry geom,
        double? checkH, double? checkBeam, double? checkWCeil, string? userId, CancellationToken ct = default)
    {
        var reasons = geom.Validate(checkH, checkBeam, checkWCeil);
        if (reasons.Count > 0) throw new GeometryInvalidException(reasons);

        var walls = geom.GenerateWalls();

        var g = await _db.TankGeometries.FirstOrDefaultAsync(x => x.TankId == tankId, ct);
        if (g is null) { g = new TankGeometryEntity { TankId = tankId }; _db.TankGeometries.Add(g); }
        g.LengthL = geom.L; g.WFloor = geom.WFloor; g.ThetaLow = geom.ThetaLow; g.HLow = geom.HLow;
        g.HWall = geom.HWall; g.ThetaUp = geom.ThetaUp; g.HUp = geom.HUp;
        g.LevelZ = JsonSerializer.Serialize(geom.LevelZ);
        g.ReachZMin = geom.ReachZMin; g.ReachZMax = geom.ReachZMax;
        g.OriginOx = geom.Ox; g.OriginOy = geom.Oy; g.CreatedBy = userId;

        var existing = await _db.Walls.Where(w => w.TankId == tankId).ToListAsync(ct);
        if (existing.Count > 0) _db.Walls.RemoveRange(existing);
        await _db.SaveChangesAsync(ct);   // geometry(FK 대상) 선확정 + 기존 면 삭제

        foreach (var w in walls)
            _db.Walls.Add(new WallEntity
            {
                TankId = tankId, WallCode = w.WallCode,
                Origin = JsonSerializer.Serialize(w.Pose.Origin),
                UAxis = JsonSerializer.Serialize(w.Pose.U),
                VAxis = JsonSerializer.Serialize(w.Pose.V),
                Normal = JsonSerializer.Serialize(w.Normal),
                ULen = w.ULen, VLen = w.VLen, FacingYaw = w.FacingYaw, Generated = true
            });
        await _db.SaveChangesAsync(ct);

        _log.LogInformation("tank geometry 등록: {Tank} → {N}면 생성", tankId, walls.Count);
        return walls.Count;
    }

    public async Task<object?> GetGeometryAsync(string tankId, CancellationToken ct = default)
    {
        var g = await _db.TankGeometries.AsNoTracking().FirstOrDefaultAsync(x => x.TankId == tankId, ct);
        if (g is null) return null;
        var d = ToGeom(g).Derived();
        return new
        {
            g.TankId,
            lengthL = g.LengthL, wFloor = g.WFloor,
            thetaLowDeg = g.ThetaLow * 180.0 / Math.PI, hLow = g.HLow, hWall = g.HWall,
            thetaUpDeg = g.ThetaUp * 180.0 / Math.PI, hUp = g.HUp,
            levelZ = JsonSerializer.Deserialize<double[]>(g.LevelZ),
            reachZMin = g.ReachZMin, reachZMax = g.ReachZMax,
            originOx = g.OriginOx, originOy = g.OriginOy,
            derived = new { d.WLow, d.B, d.WUp, d.WCeil, d.H }
        };
    }

    /// <summary>
    /// 면 목록 조회 [SPEC v3.1 §8]. level 지정 시 그 층 도달 밴드와 교차하는 면만 반환하고
    /// 각 면에 도달 가능 v구간(reachableVBand=[vLo,vHi])을 부착한다. 미지정 시 전체 면.
    /// </summary>
    public async Task<IReadOnlyList<object>> GetWallsAsync(string tankId, int? level = null, CancellationToken ct = default)
    {
        var walls = await _db.Walls.AsNoTracking().Where(w => w.TankId == tankId)
            .OrderBy(w => w.WallCode).ToListAsync(ct);

        LevelBand? band = null;
        if (level is int lv)
        {
            var g = await _db.TankGeometries.AsNoTracking().FirstOrDefaultAsync(x => x.TankId == tankId, ct);
            if (g is not null)
                band = ToGeom(g).LevelBandList().FirstOrDefault(b => b.Level == lv);
        }

        var result = new List<object>();
        foreach (var w in walls)
        {
            var origin = JsonSerializer.Deserialize<double[]>(w.Origin)!;
            var vAxis = JsonSerializer.Deserialize<double[]>(w.VAxis)!;
            double[]? reachableVBand = null;
            if (band is not null)
            {
                var vb = LevelBands.ReachableVBand(origin[2], vAxis[2], w.VLen, band);
                if (vb is null) continue;   // 이 층에서 도달 불가한 면 → 제외
                reachableVBand = new[] { vb.Value.VLo, vb.Value.VHi };
            }
            result.Add(new
            {
                w.TankId, w.WallCode,
                origin,
                uAxis = JsonSerializer.Deserialize<double[]>(w.UAxis),
                vAxis,
                normal = JsonSerializer.Deserialize<double[]>(w.Normal),
                w.ULen, w.VLen, w.FacingYaw, w.Generated, w.Description,
                reachableVBand
            });
        }
        return result;
    }

    private static TankGeometry ToGeom(TankGeometryEntity g) => new(
        g.LengthL, g.WFloor, g.ThetaLow, g.HLow, g.HWall, g.ThetaUp, g.HUp,
        JsonSerializer.Deserialize<double[]>(g.LevelZ) ?? Array.Empty<double>(), g.OriginOx, g.OriginOy,
        g.ReachZMin, g.ReachZMax);
}
