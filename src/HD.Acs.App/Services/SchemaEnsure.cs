using HD.Acs.Data;
using Microsoft.EntityFrameworkCore;

namespace HD.Acs.App.Services;

/// <summary>
/// 기동 시 **추가형(additive) 컬럼**만 멱등 보장한다. 앱은 자동 마이그레이션을 하지 않는데, EF 가 매핑하는
/// 컬럼이 DB에 없으면 그 테이블을 읽는 모든 API가 500이 된다 — 현장 이동식 서버(폐쇄망)에서
/// db/migrations 수동 적용을 잊어도 바이너리 배포만으로 뜨도록 하는 최소 안전망이다.
///
/// 범위 제한: <c>ADD COLUMN IF NOT EXISTS</c>(기본값 있는 추가)만. 삭제·타입 변경·데이터 환산은
/// 여기 넣지 않는다(일반 마이그레이션 러너는 별도 결정 사항). 각 항목은 db/migrations 의 동일 구문과 짝을 이룬다.
/// </summary>
public static class SchemaEnsure
{
    private static readonly string[] Statements =
    {
        // db/migrations/2026-09-21_run_task_totals.sql
        "ALTER TABLE run.scenario_run ADD COLUMN IF NOT EXISTS total_tasks int",
        "ALTER TABLE run.scenario_run ADD COLUMN IF NOT EXISTS excluded_tasks int NOT NULL DEFAULT 0",
    };

    public static async Task EnsureAsync(AcsDbContext db, ILogger log, CancellationToken ct = default)
    {
        foreach (var sql in Statements)
            await db.Database.ExecuteSqlRawAsync(sql, ct);
        log.LogInformation("스키마 추가 컬럼 보장 완료 ({N}건, 멱등).", Statements.Length);
    }
}
