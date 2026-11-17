using System.Text.Json;
using HD.Acs.Core.Planning;
using HD.Acs.Data;
using HD.Acs.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HD.Acs.App.Services;

/// <summary>CROSS4 작업 1건의 좌우 판정 결과. <c>After</c>=정한 값(null = 정하지 못함, 사유는 <c>Reason</c>).</summary>
public sealed record Cross4SideRow(Guid TaskId, Guid AreaId, string AreaName, string WallCode, int Level, int Seq, string? Name,
    string Before, string? After, bool Changed, string? StartToEnd, string? Reason);

/// <summary>판정 요약 — 적용했으면 <c>Applied</c> = true(바뀐 작업만 저장).</summary>
public sealed record Cross4SideResult(string TankId, bool Applied, int Total, int Left, int Right, int Undecided, int Changed,
    IReadOnlyList<Cross4SideRow> Rows);

/// <summary>
/// 선창의 CROSS4 작업에 정면 기준 좌우(CROSS4_L / CROSS4_R)를 정한다 [VDA §8.2 · 1.22].
/// 가로지르는 수직 용접선은 같은 면의 세로 작업에서 찾고(<see cref="Cross4Side.CrossingVerticalU"/>), 그 선이 작업 가운데에서
/// 정면 기준 어느 쪽으로 치우쳤는지로 정한다(<see cref="Cross4Side.SideOfVertical"/>, 1 cm 미만이면 판정 불가).
/// 정하지 못하는 것(세로 작업·바닥/천장·가지를 그린 교차·이웃으로 끝을 가를 수 없음)은 그대로 둔다 — 운영자가 계획 화면에서 고른다.
/// </summary>
public sealed class Cross4SideService(AcsDbContext db, ILogger<Cross4SideService> log)
{
    public sealed class NotFoundException(string m) : Exception(m);

    /// <param name="apply">true 면 바뀐 작업의 seamType 을 저장한다(false = 미리보기).</param>
    /// <param name="overwrite">true 면 이미 CROSS4_L/R 인 작업도 다시 정한다(false = 접미 없는 CROSS4 만).</param>
    public async Task<Cross4SideResult> ClassifyAsync(string tankId, bool apply, bool overwrite, string? userId, CancellationToken ct = default)
    {
        if (!await db.TankGeometries.AnyAsync(g => g.TankId == tankId, ct))
            throw new NotFoundException($"선창 '{tankId}' 없음");

        var walls = (await db.Walls.AsNoTracking().Where(w => w.TankId == tankId).ToListAsync(ct))
            .ToDictionary(w => w.WallCode, StringComparer.OrdinalIgnoreCase);
        var areas = (await db.InspectionAreas.AsNoTracking().Where(a => a.TankId == tankId).ToListAsync(ct))
            .ToDictionary(a => a.AreaId);
        var areaIds = areas.Keys.ToList();
        var tasks = await db.AreaTasks.Where(t => areaIds.Contains(t.AreaId)).ToListAsync(ct);

        // 면마다 모든 작업(층 무관 — 면-전체 좌표) — 가로지르는 세로 작업 찾기용
        var endsByWall = tasks.GroupBy(t => areas[t.AreaId].WallCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var rows = new List<Cross4SideRow>();
        foreach (var t in tasks.Where(t => Cross4Side.IsCross4(t.SeamType))
                     .OrderBy(t => areas[t.AreaId].WallCode).ThenBy(t => areas[t.AreaId].Level)
                     .ThenBy(t => areas[t.AreaId].Name).ThenBy(t => t.Seq))
        {
            var a = areas[t.AreaId];
            var before = t.SeamType;
            if (!overwrite && before is Cross4Side.Left or Cross4Side.Right) continue;

            string? after = null, reason = null, dir = null;
            if (!walls.TryGetValue(a.WallCode, out var w))
                reason = "면 정보 없음";
            else
            {
                var (u, v, n) = (Vec(w.UAxis), Vec(w.VAxis), Vec(w.Normal));
                dir = Cross4Side.HorizontalSign(t.EndU - t.StartU, t.EndV - t.StartV, u, v, n) switch
                {
                    1 => "←", -1 => "→", _ => null,   // 정면에서 본 시작→끝(왼쪽 = 1)
                };
                if (Cross4Side.LeftDir(n) is null) reason = "바닥·천장 — 정면 좌우가 없음";
                else if (!string.IsNullOrWhiteSpace(t.Points) && t.Points != "null")
                    reason = "가지를 그린 교차(교차점 = 작업 중심) — 좌우를 정할 끝이 없음";
                else if (dir is null) reason = "가로 작업이 아님(세로·점)";
                else
                {
                    var others = endsByWall[a.WallCode].Where(o => o.TaskId != t.TaskId)
                        .Select(o => (o.StartU, o.StartV, o.EndU, o.EndV));
                    var vu = Cross4Side.CrossingVerticalU(t.StartU, t.StartV, t.EndU, t.EndV, others);
                    if (vu is null) reason = "가로지르는 수직 용접선을 찾지 못함(이웃 세로 작업 없음·여럿)";
                    else
                    {
                        after = Cross4Side.SideOfVertical(t.StartU, t.StartV, t.EndU, t.EndV, vu.Value, u, v, n);
                        if (after is null) reason = "수직 용접선이 작업 가운데(도면상 치우침 없음) — 좌우를 정할 수 없음";
                    }
                }
            }
            bool changed = after is not null && !string.Equals(after, before, StringComparison.Ordinal);
            if (apply && changed) t.SeamType = after!;
            rows.Add(new Cross4SideRow(t.TaskId, a.AreaId, a.Name, a.WallCode, a.Level, t.Seq, t.Name, before, after, changed, dir, reason));
        }

        var result = new Cross4SideResult(tankId, apply, rows.Count,
            rows.Count(r => r.After == Cross4Side.Left), rows.Count(r => r.After == Cross4Side.Right),
            rows.Count(r => r.After is null), rows.Count(r => r.Changed), rows);
        if (apply && result.Changed > 0)
        {
            db.AuditLogs.Add(new AuditLogEntity
            {
                UserId = userId ?? "", Action = "CROSS4_SIDE_APPLY", Target = tankId,
                Detail = JsonSerializer.Serialize(new { result.Total, result.Left, result.Right, result.Undecided, result.Changed, overwrite }),
            });
            await db.SaveChangesAsync(ct);
            log.LogInformation("CROSS4 좌우 적용 {Tank}: L {L} · R {R} · 판정 불가 {U} · 변경 {C}",
                tankId, result.Left, result.Right, result.Undecided, result.Changed);
        }
        return result;
    }

    private static double[] Vec(string json) => JsonSerializer.Deserialize<double[]>(json) ?? new double[3];
}
