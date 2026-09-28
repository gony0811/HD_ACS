using HD.Acs.Data;
using HD.Acs.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HD.Acs.App.Services;

/// <summary>
/// 기동 시드: alarm.spec 누락 코드를 멱등 insert. alarm.alarm 이 alarm.spec 을 FK로 참조하므로
/// 코드가 새 알람을 기록하기 전에 spec 행이 있어야 한다. 앱은 자동 마이그레이션을 하지 않으므로
/// 현장 서버(폐쇄망)에서는 이 기동 시드가 유일한 적용 경로다(ActionCatalogSeed 와 동일 취지).
/// 기존 행은 건드리지 않는다(운영자가 severity/문구를 조정했을 수 있음).
///
/// ⚠️ 유지보수: <c>db/schema.sql</c> 의 alarm.spec 시드와 동일 코드 집합을 유지할 것.
/// </summary>
public static class AlarmSpecSeed
{
    private static readonly (string Code, string Severity, string Title, string Description)[] Specs =
    {
        ("SAIGE_UNREACHABLE", "WARNING", "SAIGE 전송 불가",
            "로봇 상태(robot-health-check) 전송이 임계 횟수 이상 연속 실패 — SAIGE 기동/네트워크 확인 [SAIGE §9.5]"),
        ("SAIGE_BAD_REQUEST", "WARNING", "SAIGE 형식 오류",
            "SAIGE가 로봇 상태 페이로드를 형식 오류(40001)로 거부 — 재시도 없이 폐기됨, 규격 불일치 확인 [SAIGE §9.5]"),
    };

    public static async Task EnsureAsync(AcsDbContext db, ILogger log, CancellationToken ct = default)
    {
        var existing = await db.AlarmSpecs.AsNoTracking().Select(s => s.AlarmCode).ToListAsync(ct);
        var missing = Specs.Where(s => !existing.Contains(s.Code)).ToList();
        if (missing.Count == 0) return;

        foreach (var s in missing)
            db.AlarmSpecs.Add(new AlarmSpecEntity
            { AlarmCode = s.Code, Severity = s.Severity, Title = s.Title, Description = s.Description });
        await db.SaveChangesAsync(ct);
        log.LogInformation("alarm.spec 기동 시드 적용: {Codes}", string.Join(", ", missing.Select(m => m.Code)));
    }
}
