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
        ("INSPECTION_FAILED", "WARNING", "검사 실패",
            "정차 검사(용접선) 실패 — 자동 재시도하지 않음. detail.items에 용접선별 실패 사유, 재실행 여부는 작업자가 결정"),
        ("ROBOT_NOT_CONNECTED", "WARNING", "AMR 미연결",
            "AMR 미연결(connection ≠ ONLINE 또는 state 수신 끊김) 상태에서 미션 시작·이어하기 요청 — 명령을 보내지 않고 차단됨. HD_AMR 실행·MQTT 연결 확인"),
        ("SAIGE_BAD_REQUEST", "WARNING", "SAIGE 형식 오류",
            "SAIGE가 로봇 상태 페이로드를 형식 오류(40001)로 거부 — 재시도 없이 폐기됨, 규격 불일치 확인 [SAIGE §9.5]"),
        ("BATTERY_CRITICAL", "CRITICAL", "배터리 위험",
            "AMR 배터리 임계(≤10%) — 그 자리 안전정지 보고 [HD_AMR 배터리관리 §2]"),
        ("ORDER_REJECTED_BATTERY_LOW", "WARNING", "배터리 저전력 — Order 거부",
            "AMR이 저전력 상태에서 작업 Order를 거부(WARNING, 실패 아님) — 교체 장소로 보내거나 교체 완료 후 재시도 [HD_AMR 배터리관리 §8]"),
        ("BATTERY_SWAP_DISPATCHED", "INFO", "배터리 교체 이동 발행",
            "운영자가 배터리 교체 장소로 이동 Order를 수동 발행 — 활성 run이 있으면 함께 중단됨, 교체 완료 후 이어하기로 재배차"),
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
