using System.Text.Json;
using HD.Acs.Core.Planning;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HD.Acs.App.Services;

/// <summary>측면 정차 층+벽면 설정 입력. Mode 필수(FRONT/SIDE), DefaultDir 생략 = U_PLUS, StandoffM 생략 = 영역·면 기본 이격.</summary>
public sealed record WallStationModeRequest(string Mode, string? DefaultDir = null, double? StandoffM = null, string? UserId = null);

/// <summary>조회 응답 한 줄.</summary>
public sealed record WallStationModeDto(int Level, string WallCode, string Mode, string DefaultDir, double? StandoffM,
    string? UpdatedBy, DateTimeOffset UpdatedAt);

/// <summary>
/// 측면 정차 층+벽면 설정 [VDA §4.4.3 / N25] — 조회·입력·삭제. 설정은 데이터일 뿐이고 실제로 쓰이는지는
/// <c>Acs:Dispatch:SideStation:Enabled</c>(기본 false)가 정한다(구버전 HD_AMR 이 측면 theta 로 정면 검사하는 것을 막기 위함).
/// </summary>
public sealed class WallStationModeService
{
    public const double MinStandoffM = 0.3;
    public const double MaxStandoffM = 3.0;

    private readonly AcsDbContext _db;
    public bool Enabled { get; }

    public WallStationModeService(AcsDbContext db, IConfiguration config)
    {
        _db = db;
        Enabled = config.GetValue("Acs:Dispatch:SideStation:Enabled", false);
    }

    public sealed class RejectedException(string message) : Exception(message);

    public async Task<IReadOnlyList<WallStationModeDto>?> ListAsync(string tankId, CancellationToken ct)
    {
        if (!await _db.TankGeometries.AnyAsync(g => g.TankId == tankId, ct)) return null;
        return await _db.WallStationModes.AsNoTracking().Where(m => m.TankId == tankId)
            .OrderBy(m => m.Level).ThenBy(m => m.WallCode)
            .Select(m => new WallStationModeDto(m.Level, m.WallCode, m.Mode, m.DefaultDir, m.StandoffM, m.UpdatedBy, m.UpdatedAt))
            .ToListAsync(ct);
    }

    /// <summary>입력 검사 — 문제가 있으면 사유, 없으면 null. 순수.</summary>
    public static string? Problem(string wallCode, int level, int levelCount, WallStationModeRequest req)
    {
        if (level < 1 || level > levelCount) return $"층 L{level} 없음(이 선창은 L1~L{levelCount})";
        var mode = req.Mode?.Trim().ToUpperInvariant();
        if (!SideStation.IsValidMode(mode)) return $"mode 는 FRONT 또는 SIDE 입니다(받은 값 '{req.Mode}')";
        var dir = (req.DefaultDir ?? SideStation.DirUPlus).Trim().ToUpperInvariant();
        if (!SideStation.IsValidDir(dir)) return $"defaultDir 는 U_PLUS 또는 U_MINUS 입니다(받은 값 '{req.DefaultDir}')";
        if (mode == SideStation.ModeSide && !SideStation.WallAllowsSide(wallCode))
            return $"측면 정차는 수직벽 PM·SM·F·A 에만 쓸 수 있습니다({wallCode} 불가 — 바닥·천장·챔퍼는 HD_AMR 이 Order 를 거부)";
        if (req.StandoffM is double s && (!double.IsFinite(s) || s < MinStandoffM || s > MaxStandoffM))
            return $"standoffM 은 {MinStandoffM}~{MaxStandoffM} m 입니다(받은 값 {s})";
        return null;
    }

    /// <summary>설정 저장(있으면 교체). 선창·면이 없으면 KeyNotFound, 값이 틀리면 RejectedException.</summary>
    public async Task<WallStationModeDto> PutAsync(string tankId, int level, string wallCode, WallStationModeRequest req, CancellationToken ct)
    {
        wallCode = wallCode.Trim().ToUpperInvariant();
        var geom = await _db.TankGeometries.AsNoTracking().FirstOrDefaultAsync(g => g.TankId == tankId, ct)
            ?? throw new KeyNotFoundException($"선창 '{tankId}' 없음");
        if (!await _db.Walls.AnyAsync(w => w.TankId == tankId && w.WallCode == wallCode, ct))
            throw new KeyNotFoundException($"면 '{wallCode}' 없음");
        var levels = JsonSerializer.Deserialize<double[]>(geom.LevelZ)?.Length ?? 0;
        if (Problem(wallCode, level, levels, req) is { } p) throw new RejectedException(p);

        var row = await _db.WallStationModes.FirstOrDefaultAsync(m => m.TankId == tankId && m.Level == level && m.WallCode == wallCode, ct);
        if (row is null)
        {
            row = new WallStationModeEntity { TankId = tankId, Level = level, WallCode = wallCode };
            _db.WallStationModes.Add(row);
        }
        row.Mode = req.Mode.Trim().ToUpperInvariant();
        row.DefaultDir = (req.DefaultDir ?? SideStation.DirUPlus).Trim().ToUpperInvariant();
        row.StandoffM = req.StandoffM;
        row.UpdatedBy = req.UserId;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        _db.AuditLogs.Add(new AuditLogEntity
        {
            UserId = req.UserId ?? "", Action = "WALL_STATION_MODE",
            Target = $"{tankId}-L{level}-{wallCode}",
            Detail = JsonSerializer.Serialize(new { row.Mode, row.DefaultDir, row.StandoffM }),
        });
        await _db.SaveChangesAsync(ct);
        return new WallStationModeDto(row.Level, row.WallCode, row.Mode, row.DefaultDir, row.StandoffM, row.UpdatedBy, row.UpdatedAt);
    }

    /// <summary>설정 삭제(= 정면). 없으면 false.</summary>
    public async Task<bool> DeleteAsync(string tankId, int level, string wallCode, string? userId, CancellationToken ct)
    {
        wallCode = wallCode.Trim().ToUpperInvariant();
        var row = await _db.WallStationModes.FirstOrDefaultAsync(m => m.TankId == tankId && m.Level == level && m.WallCode == wallCode, ct);
        if (row is null) return false;
        _db.WallStationModes.Remove(row);
        _db.AuditLogs.Add(new AuditLogEntity { UserId = userId ?? "", Action = "WALL_STATION_MODE_DELETE", Target = $"{tankId}-L{level}-{wallCode}" });
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
