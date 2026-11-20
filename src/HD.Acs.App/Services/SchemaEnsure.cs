using HD.Acs.Data;
using Microsoft.EntityFrameworkCore;

namespace HD.Acs.App.Services;

/// <summary>
/// 기동 시 **추가형(additive) 컬럼**만 멱등 보장한다. 앱은 자동 마이그레이션을 하지 않는데, EF 가 매핑하는
/// 컬럼이 DB에 없으면 그 테이블을 읽는 모든 API가 500이 된다 — 현장 이동식 서버(폐쇄망)에서
/// db/migrations 수동 적용을 잊어도 바이너리 배포만으로 뜨도록 하는 최소 안전망이다.
///
/// 범위 제한: <c>ADD COLUMN IF NOT EXISTS</c>(기본값 있는 추가)와 <c>CREATE TABLE IF NOT EXISTS</c>(새 테이블)만. 삭제·타입 변경·데이터 환산은
/// 여기 넣지 않는다(일반 마이그레이션 러너는 별도 결정 사항). 각 항목은 db/migrations 의 동일 구문과 짝을 이룬다.
/// </summary>
public static class SchemaEnsure
{
    internal static readonly string[] Statements =
    {
        // db/migrations/2026-09-21_run_task_totals.sql
        "ALTER TABLE run.scenario_run ADD COLUMN IF NOT EXISTS total_tasks int",
        "ALTER TABLE run.scenario_run ADD COLUMN IF NOT EXISTS excluded_tasks int NOT NULL DEFAULT 0",
        // db/migrations/2026-10-07_area_task_points.sql — CROSS3/4 교차 가지 끝점(VDA §8.5.1 N13)
        "ALTER TABLE ref.area_task ADD COLUMN IF NOT EXISTS points jsonb",
        // db/migrations/2026-11-12_area_task_enabled.sql — 용접선 임시 제외
        "ALTER TABLE ref.area_task ADD COLUMN IF NOT EXISTS enabled boolean NOT NULL DEFAULT true",
        // db/migrations/2026-10-31_area_station_max_backward.sql — 영역별 정차 보정 후진 상한(VDA §4.4.2 N23)
        "ALTER TABLE ref.inspection_area ADD COLUMN IF NOT EXISTS station_max_backward_mm double precision",
        // db/migrations/2026-11-04_area_station_lift_mm.sql — 영역별 정차 리프트 높이(0~1000mm, NULL=배차 기본 0)
        "ALTER TABLE ref.inspection_area ADD COLUMN IF NOT EXISTS station_lift_mm double precision",
        // db/migrations/2026-11-11_run_task_ids.sql — 리페어 run의 검사 대상 용접선(NULL=일반 run)
        "ALTER TABLE run.scenario_run ADD COLUMN IF NOT EXISTS task_ids jsonb",
        // db/migrations/2026-11-15_tank_inspection_kind.sql — 선창의 검사 종류(WELD 용접 / GAS 가스)
        "ALTER TABLE ref.tank_geometry ADD COLUMN IF NOT EXISTS inspection_kind text NOT NULL DEFAULT 'WELD'",
        // db/migrations/2026-11-15_run_round.sql — 가스검사 회차(1|2, NULL=미지정)
        "ALTER TABLE run.scenario_run ADD COLUMN IF NOT EXISTS round int",
        // db/migrations/2026-11-15_gas_result.sql — 가스 측정 결과 + 세이지 전송 상태(새 테이블)
        """
        CREATE TABLE IF NOT EXISTS hist.gas_result (
          result_id     uuid PRIMARY KEY,
          action_id     uuid NOT NULL,
          run_id        uuid NOT NULL,
          task_id       uuid,
          tank_id       text,
          robot_id      text NOT NULL,
          round         int,
          result        text NOT NULL,
          point_count   int  NOT NULL DEFAULT 0,
          body          jsonb NOT NULL,
          send_state    text NOT NULL DEFAULT 'PENDING',
          send_attempts int  NOT NULL DEFAULT 0,
          last_error    text,
          created_at    timestamptz NOT NULL DEFAULT now(),
          sent_at       timestamptz
        )
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS ux_gas_result_action ON hist.gas_result (action_id)",
        "CREATE INDEX IF NOT EXISTS ix_gas_result_send ON hist.gas_result (send_state, created_at)",
        "CREATE INDEX IF NOT EXISTS ix_gas_result_run ON hist.gas_result (run_id)",
        // db/migrations/2026-11-17_seam_test_attempt.sql — 코봇 이동 시험 발행 기록(시험에도 누적 attempt 발급)
        """
        CREATE TABLE IF NOT EXISTS run.seam_test (
          test_id    uuid PRIMARY KEY,
          task_id    uuid NOT NULL,
          robot_id   text NOT NULL,
          order_id   text NOT NULL,
          attempt    int  NOT NULL,
          created_at timestamptz NOT NULL DEFAULT now()
        )
        """,
        "CREATE INDEX IF NOT EXISTS ix_seam_test_task ON run.seam_test (task_id)",
        // db/migrations/2026-11-15_inspection_profile.sql — HD_AMR 검사 프로필 사본(새 테이블 — 없을 때만 만든다)
        """
        CREATE TABLE IF NOT EXISTS ref.inspection_profile (
          source         text NOT NULL,
          amr_profile_id int  NOT NULL,
          name           text NOT NULL,
          seam_type      text NOT NULL DEFAULT 'LINE',
          pose_absolute  boolean NOT NULL DEFAULT false,
          waypoints      jsonb NOT NULL DEFAULT '[]',
          point_count    int  NOT NULL DEFAULT 0,
          version        int  NOT NULL DEFAULT 1,
          content_hash   text NOT NULL DEFAULT '',
          amr_updated_at timestamptz,
          received_at    timestamptz NOT NULL DEFAULT now(),
          changed_at     timestamptz NOT NULL DEFAULT now(),
          deleted        boolean NOT NULL DEFAULT false,
          PRIMARY KEY (source, amr_profile_id)
        )
        """,
        "CREATE INDEX IF NOT EXISTS ix_inspection_profile_name ON ref.inspection_profile (name)",
        """
        CREATE TABLE IF NOT EXISTS ref.inspection_profile_assignment (
          source         text NOT NULL,
          seam_type      text NOT NULL,
          wall_code      text NOT NULL,
          direction      text NOT NULL,
          amr_profile_id int  NOT NULL,
          PRIMARY KEY (source, seam_type, wall_code, direction)
        )
        """,
        // db/migrations/2026-11-19_amr_teaching.sql — HD_AMR 교시 위치(리치 시뮬레이터 출발 자세) 사본
        // 주의: ExecuteSqlRaw 는 SQL 을 string.Format 으로 다루므로 중괄호 리터럴('{}')을 쓰면 기동 시 예외 — jsonb_build_object() 사용
        """
        CREATE TABLE IF NOT EXISTS ref.amr_teaching (
          source             text PRIMARY KEY,
          teaching_positions jsonb NOT NULL DEFAULT '[]',
          start_joints       jsonb NOT NULL DEFAULT jsonb_build_object(),
          content_hash       text NOT NULL DEFAULT '',
          received_at        timestamptz NOT NULL DEFAULT now(),
          changed_at         timestamptz NOT NULL DEFAULT now()
        )
        """,
    };

    public static async Task EnsureAsync(AcsDbContext db, ILogger log, CancellationToken ct = default)
    {
        foreach (var sql in Statements)
            await db.Database.ExecuteSqlRawAsync(sql, ct);
        log.LogInformation("스키마 추가 컬럼 보장 완료 ({N}건, 멱등).", Statements.Length);
    }
}
