# 사양 — VDA5050 Action 카탈로그 & 안전 인터록 (ACS ↔ AMR)

order 노드에서 수행되는 **action**과, 이동·액션에 걸리는 **안전 인터록**을 정의한다.
이동 실현(노드↔Job/Task 인덱스)은 `VDA5050_MOVEMENT_MAPPING_DESIGN` / `VDA5050_NODE_INDEX_TRANSMISSION`,
기반 계약은 `VDA5050_SPEC_PLAN`(Phase 3/4)을 따른다. 본 문서는 Phase 3 action 카탈로그의 확장분이다.

> 상태: **Draft** — 코봇·검사 세부는 HD_AMR 시퀀스 스텝을 기준으로 반영.

---

## 0. 액션 계층 (핵심 구분)

| 계층 | 소유 | 예 | VDA 노출 |
|---|---|---|---|
| **주행(travel)** | 어댑터 | 노드 간 이동 | 노드/엣지(액션 아님) |
| **order 액션(coarse)** | ACS가 노드에 부여 | startWeldInspection · cobotHome · homeReturn | **VDA action** |
| **검사 서브스텝(fine)** | HD_AMR 시퀀스 엔진 | cobotInspection·cameraAlign·flatSurfaceAlign·peak/bead·wobj… | 미노출(액션 내부) |

- ACS는 **coarse 액션만** 발행한다. 코봇 정렬·용접선 추적 등 fine 스텝은 `startWeldInspection` 내부에서
  HD_AMR 시퀀스 엔진이 실행한다(현행 스텝 재사용).

---

## 1. Action 카탈로그 (order 노드 액션)

| actionType | scope | blockingType | 설명 | HD_AMR 실현(시퀀스 스텝) |
|---|---|---|---|---|
| **startWeldInspection** | NODE | HARD | 스테이션 1구간 자동 검사 | cobotInspection(200)→cameraAlign(300)→flatSurfaceAlign(400)→laserWD(450)→peak/bead·wobj(500~1170)→inspectionRun(1200)→wobjReset(1300)→monitorClose(1400) |
| **cobotHome** | NODE 또는 INSTANT | HARD | 코봇 안전(홈) 자세 복귀 | amrMove의 EnsureCobotAtHome / 홈 MoveJ |
| **homeReturn** | NODE | HARD | AMR 도킹/홈 복귀(운영 종료·충전) | 도킹 Job/Task Index 실행 |

- 확장 여지(후속): `charge`(충전 결합), `calibrateReference`(기준점 캡처) 등. 초기 카탈로그는 위 3종 + Phase 4 instantActions.
- `startWeldInspection` 파라미터 = SPEC_PHASE2 §4.1 스키마(jobRef, position, params). (Phase 3.2)

### 1.1 blockingType 규칙
- 위 3종 모두 **HARD**: 실행 중 주행 금지, 순차 실행. (검사·홈복귀는 정차 상태에서만 안전)
- SOFT/NONE 액션은 현재 없음(동시 주행+액션 미허용).

### 1.2 actionState 생명주기 (Phase 3.3 재확인)
`WAITING → RUNNING → FINISHED | FAILED` (일시정지 중 `PAUSED`). resultDescription: `OK;…` / `FAIL;reason=…`.

---

## 2. 안전 인터록 (필수)

### 2.1 이동 전 코봇 안전 자세 [최우선 안전 규칙]
- **AMR 주행 직전, 코봇이 홈(안전) 자세인지 반드시 확인하고, 아니면 홈 복귀 후에만 주행을 명령한다.**
- 근거: 코봇 팔이 뻗은 채 주행하면 주변 충돌·전도 위험.
- 구현: 어댑터의 이동 실현(MoveToNode) **선행 스텝 = ensureCobotSafe**. HD_AMR `AmrMoveStep.EnsureCobotAtHomeAsync`가
  이 로직을 이미 보유(관절각이 홈과 다르면 MoveJ 복귀).
- 실패 처리: 코봇 홈 복귀 실패 → **이동 미실행 + error(safetyInterlock 또는 robotError)**, 미션 Aborted.
- **암묵 강제**: 이 인터록은 ACS가 액션으로 지시하지 않아도 **어댑터가 모든 주행 전 자동 보장**한다(누락 방지).
  필요 시 명시적 `cobotHome` 액션도 제공(수동·티칭용).

### 2.2 액션 실행 중 정차 유지
- HARD 액션 실행 중에는 AMR 주행 금지(상태제어 정지 유지). 액션 FINISHED 후에만 다음 주행.

### 2.3 비상/보호 정지 (Phase 4 연계)
- `emergencyStop`/protective stop 시 코봇·AMR 모두 정지. 복구 후 재개는 운영 절차(재측위·상태 확인) 경유.

---

## 3. 노드 실행 순서 (어댑터, 요약)

```
per order 노드:
  ① ensureCobotSafe        (§2.1 — 홈 아니면 홈 복귀; 실패 시 abort)
  ② move → node            (Job/Task Index; 도착판정 = pose tol + 로봇상태/Task번호)
  ③ run node actions       (startWeldInspection 등 HARD; 시퀀스 엔진 실행)
  ④ actionState 보고        (RUNNING→FINISHED/FAILED, resultDescription)
마지막 노드 후: homeReturn(선택) → order 완료
```

- ①은 ②의 전제(안전). ③은 정차 상태에서만.
- ②의 도착 실패·③의 액션 실패는 각각 error로 보고하고 정책(재시도/스킵/알람)에 따른다.

---

## 4. 결과·상관 (resultDescription)

- 성공: `OK;anchor=FULL|SHARED;jobRef=<jobRef>` (Phase 3.4).
- 실패: `FAIL;reason=<CODE>[;detail=…]` (예: `PARAM`, `COBOT_HOME`, `VISION`, `MOVE_TIMEOUT`).
- 검사 결과 상세(촬영 성공/실패·좌표)는 inspection_result로 기록(진행률/엔터프라이즈 연동과 정합).
- 비전 v3 연동 시 startWeldInspection 결과에 Run/Task ID 상관(비전 인터페이스 v3).

---

## 5. ref.action_catalog 반영 (구현 시)

```sql
INSERT INTO ref.action_catalog (action_type, scope, blocking_type, param_schema, description) VALUES
 ('startWeldInspection','NODE','HARD','<SPEC_PHASE2 §4.1>','스테이션 1구간 자동 검사'),
 ('cobotHome','NODE','HARD','{}','코봇 안전(홈) 자세 복귀'),
 ('homeReturn','NODE','HARD','{}','AMR 도킹/홈 복귀');
```

- ensureCobotSafe는 **카탈로그 액션이 아니라 어댑터 인터록**(암묵 강제)으로 두는 것을 권장(§2.1).

---

## 6. 결정/확인 필요
- [ ] cobotHome을 **명시 액션 + 암묵 인터록 병행**으로 둘지, 인터록만 둘지 (권장: 병행 — 인터록 필수 + 액션 선택)
- [ ] homeReturn/도킹의 Job/Task Index·충전 결합 여부 (벤더 도킹 절차 확인)
- [ ] 코봇 "안전(홈) 자세" 정의·허용오차 (현행 `AmrMoveStep.HomeToleranceDeg=0.5°` 재사용 여부)
- [ ] 액션 실패 시 정책(재시도/스킵/알람) — 시나리오 policy jsonb와 정합
