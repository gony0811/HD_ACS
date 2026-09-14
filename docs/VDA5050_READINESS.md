# VDA 5050 운영 준비도 · 공백 체크리스트

ACS↔AMR VDA 5050 실운영 준비 상태와 남은 작업을 정리한다. (스냅샷: 2026-08-21)
사양: `VDA5050_SPEC_PLAN` 외 docs/VDA5050_*.

## 준비도 요약

| 영역 | 준비도 | 비고 |
|---|---|---|
| 사양(문서) | ~90% | Phase 0~6·액션·매핑·전송 확정. 일부 벤더 회신 대기 |
| ACS 런타임 (↔시뮬레이터) | ~70% | 코어 동작. 일부 필드·retained 미완 |
| 실물 AMR 어댑터 (↔TARS-M) | ~10% | **최대 공백** — 스켈레톤 착수 단계 |
| 벤더 확인 | 대기 | 어댑터 확정 선결 |
| 인프라·빌드 검증 | 0% | MQTT 브로커·Windows 빌드 미수행 |

**한 줄**: 설계 거의 완성, ACS는 시뮬레이터로 동작. 실물 운영 핵심인 HD_AMR VDA5050 어댑터가 미완이며, 그 확정을 벤더 회신이 막고 있다.

---

## ✅ 갖춰진 것 (ACS)

- VDA5050 메시지 모델·토픽, `Vda5050MasterClient`(order/instantActions 발행, state/connection 구독)
- `VdaBridgeService` — 활성 로봇 자동 등록·구독, connection 이벤트 처리
- `RobotStateService` — state 수신·actionId 대조·진행률 집계
- `InspectionDispatcher`/`MissionService` — 층별 최근접 배차, 단일 정차 order 발행·릴리즈
- `Simulator` — 가상 AMR (order 순회·action FINISHED/FAILED·2초 state)
- `ref.action_catalog` 시드(startWeldInspection·cobotHome·homeReturn), 티칭 테이블·인덱스 등록 UI

→ **ACS ↔ 시뮬레이터 루프 성립.**

---

## ❌ 공백 (실운영에 필요)

### A. 최우선 (블로커)
- [ ] **HD_AMR VDA5050 어댑터** — HD_AMR에 MQTT·order 수신·state/connection 발행 없음(현재 시뮬레이터뿐). ← 스켈레톤 착수(state/connection 우선)
- [ ] **이동 실현** — node→Job/Task Index→TARS-M(`SetJobIndex`+상태제어) + 도착판정. `AmrMoveStep` TODO
- [ ] **코봇 안전 인터록 배선** — 이동 전 홈 복귀(로직 존재)를 이동 경로에 연결
- [ ] **벤더 회신(§9)** — 이동 방식·맵/층 ID·에러코드·각도규약·도킹 (어댑터 확정 선결)

### B. 중요
- [ ] MQTT 브로커 배포·설정(host/port/TLS)
- [ ] state 필드 확장 — operatingMode·safetyState·nodeStates/edgeStates
- [ ] connection retained + Last Will 구현(어댑터 발행부)
- [ ] 에러 매핑(TARS-M 코드→VDA errorType) — 벤더 코드표
- [ ] initPosition(재측위) 배선 — 포즈탐색+맵일치율
- [ ] 다층 mapId 보고 — 어댑터 보유·보고

### C. 검증·운영
- [ ] Windows 빌드 검증(전 코드 미빌드)
- [ ] 통합 시나리오 — 층전환·재시도·취소·두절복구, 신규 액션 시뮬레이터 반영
- [ ] JSON Schema 적합성 검증

---

## 권장 진행 순서
1. **벤더 회신 확보(§9)** — 이동 방식 확정(어댑터 설계 잠금 해제)
2. **HD_AMR 어댑터** — MQTT 에이전트(state/connection 발행 → order 수신 → 이동 실현 → 코봇 인터록)
3. **MQTT 브로커** 세우고 ACS↔어댑터 E2E(시뮬레이터 대체)
4. **Windows 빌드·통합 테스트**

## 진행 로그
- 2026-08-21: 준비도 최초 기록. HD_AMR 어댑터 스켈레톤(state/connection 발행) 착수.
