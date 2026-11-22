-- 2026-11-21: run.order_action.order_id 추가 — 액션이 속한 발행 Order(정차).
-- 현장: run 중단 → 이어하기 뒤 같은 정차를 다시 배차하면, 중단 직전 Order 의 미실행 액션(WAITING)이 남아
-- "전 액션 종결" 판정이 끝나지 않아 다음 영역으로 넘어가지 않았다. 정차 종결·결과 판정을 이번 Order 액션으로 한정한다.
-- 앱 기동 시 SchemaEnsure 가 같은 문을 실행하므로 수동 적용은 필요 없다.
ALTER TABLE run.order_action ADD COLUMN IF NOT EXISTS order_id text;
