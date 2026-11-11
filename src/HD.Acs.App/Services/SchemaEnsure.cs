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
        // db/migrations/2026-10-07_area_task_points.sql — CROSS3/4 교차 가지 끝점(VDA §8.5.1 N13)
        "ALTER TABLE ref.area_task ADD COLUMN IF NOT EXISTS points jsonb",
        // db/migrations/2026-10-31_area_station_max_backward.sql — 영역별 정차 보정 후진 상한(VDA §4.4.2 N23)
        "ALTER TABLE ref.inspection_area ADD COLUMN IF NOT EXISTS station_max_backward_mm double precision",
        // db/migrations/2026-11-04_area_station_lift_mm.sql — 영역별 정차 리프트 높이(0~1000mm, NULL=배차 기본 0)
        "ALTER TABLE ref.inspection_area ADD COLUMN IF NOT EXISTS station_lift_mm double precision",
        // db/migrations/2026-11-11_run_task_ids.sql — 리페어 run의 검사 대상 용접선(NULL=일반 run)
        "ALTER TABLE run.scenario_run ADD COLUMN IF NOT EXISTS task_ids jsonb",
    };

    public static async Task EnsureAsync(AcsDbContext db, ILogger log, CancellationToken ct = default)
    {
        foreach (var sql in Statements)
            await db.Database.ExecuteSqlRawAsync(sql, ct);
        log.LogInformation("스키마 추가 컬럼 보장 완료 ({N}건, 멱등).", Statements.Length);
    }
}
