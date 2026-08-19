-- Q2 해소 [ADR-013]: 검사 S/W 결과 대조 1차 키(job_ref) 보강.
-- 비파괴 ADD COLUMN — 기존 행은 job_ref NULL(2차 키=position+occurred_at로 폴백 대조).
-- 적용: psql -f db/migrations/2026-08-19_q2_inspection_reconciliation.sql

ALTER TABLE hist.inspection_result
  ADD COLUMN IF NOT EXISTS job_ref text;   -- JOB-{tank}-{level}-{wall}-{seam}-{seq}

CREATE INDEX IF NOT EXISTS ix_inspresult_job
  ON hist.inspection_result (job_ref);

COMMENT ON COLUMN hist.inspection_result.job_ref IS
  '검사 S/W 1차 대조 키 (상관 ID). HD_AMR이 검사 트리거 시 전달, 검사 S/W가 판정 레코드에 부기 [ADR-013]';
COMMENT ON COLUMN hist.inspection_result.position IS
  '2차 대조 키: 도면 좌표계(m) {tank,level,wall_code,x,y,z} — seam 시작점 [ADR-013]';
COMMENT ON COLUMN hist.inspection_result.occurred_at IS
  '2차 대조 키: 종료 state timestamp(로봇 클록, UTC 저장), 허용 오차 ±2s [ADR-013]';
