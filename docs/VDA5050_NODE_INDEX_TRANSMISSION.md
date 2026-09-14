# 사양 결정 — 노드 ↔ AMR Job/Task 인덱스 전송 및 편집

order 노드가 실제 TARS-M 이동으로 이어지려면, 노드가 어떤 **사전 티칭 Job/Task 인덱스**로
실행되는지 HD_AMR(어댑터)이 알아야 한다. 그 인덱스를 **어디서 보유·편집하고 어떻게 전달하는가**를
확정한다. 전제: TARS-M은 좌표 goto가 없고 인덱스로 이동한다(부록 A / MOVEMENT_MAPPING_DESIGN).

> 상태: **결정(Draft 확정안)** — 어댑터 구현 시 이 규약을 따른다.

---

## 1. 결정 요약

| 항목 | 결정 |
|---|---|
| 인덱스 보유 위치 | **HD_AMR 로컬 매핑 테이블**(nodeId → jobIndex/taskIndex/gotoMode)을 어댑터가 보유·사용 |
| 등록(운영) | **ACS UI에서 등록** → `ref.node.metadata.amr` 보존 (이미 구현: PUT `/api/nodes/{nodeId}/amr-mapping`) |
| ACS→HD_AMR 전달 | **동기화(pull)**: HD_AMR이 ACS 티칭 테이블(`GET /api/tanks/{tank}/amr-teaching-table`)을 받아 로컬 테이블에 병합 |
| VDA order | 노드는 **nodeId만** 싣는다(표준). 인덱스는 order에 넣지 않는다 → order 스키마가 AMR 비의존 |
| 런타임 | 어댑터가 order nodeId → **로컬 테이블 조회** → TARS-M `Job Index(Holding 32)` + `상태제어 시작(30=2)` |
| 로컬 편집 | HD_AMR UI에서 **직접 편집·오버라이드** 가능(현장 보정·검증) |

### 왜 "order에 인덱스를 싣지 않는가"
- 인덱스는 **AMR 하드웨어·티칭에 종속**된 값이라 VDA order(로봇 무관 계약)에 넣으면 계약이 오염된다.
- 노드-인덱스 대응은 **맵당 정적**이므로, order마다 반복 전송할 필요가 없다.
- HD_AMR이 로컬 테이블을 가지면 **현장 재티칭·오버라이드**가 즉시 반영된다(ACS 왕복 불필요).
- ACS는 좌표·계획의 진실 소스, HD_AMR은 인덱스·모션의 진실 소스로 **역할이 깔끔히 분리**된다.

### (선택) 향후 오버라이드
- 필요 시 order 노드에 `amrJobIndex`를 **권위 오버라이드**로 실어 로컬 테이블보다 우선시킬 수 있다(현재 미채택).

---

## 2. 데이터 흐름

```
[ACS UI] 인덱스 등록 ──► ref.node.metadata.amr.jobIndex   (PUT /api/nodes/{id}/amr-mapping)
                                     │
        HD_AMR 동기화(pull) ◄────────┘  GET /api/tanks/{tank}/amr-teaching-table
                                     ▼
                         [HD_AMR 로컬 매핑 테이블]  nodeId → {jobIndex, taskIndex, gotoMode}
                                     │  (+ 로컬 편집·오버라이드)
        order 수신(nodeId) ──────────┤
                                     ▼
             TARS-M: Job Index(Holding 32) 쓰기 + 상태제어 시작(30=2) → 실행중 Task/Job 번호로 진행 추적
```

## 3. 로컬 매핑 스키마 (HD_AMR)

```jsonc
// amr_job_mapping.json (HD_AMR 로컬)
[
  {
    "nodeId": "CT1-L2-W03-ST04",
    "mapId": "CT1-L2",
    "name":  "CT1-L2-W03-ST04",
    "mapX": 12.482, "mapY": 5.117, "thetaRad": 1.571,   // ACS 동기화값(참조·검증)
    "jobIndex": 12, "taskIndex": null, "gotoMode": "INDEX",
    "source": "ACS | LOCAL",        // 마지막 수정 출처
    "updatedAt": "2026-08-21T..."
  }
]
```

- `mapX/Y/thetaRad`는 ACS 동기화 시 채워지는 **참조·검증용**(도착판정·수동 티칭 대조). 로컬 편집 대상은 주로 `jobIndex`.
- `source`로 ACS 동기화값과 로컬 오버라이드를 구분한다(동기화 시 LOCAL 편집분 보존 정책 필요 — §5).

## 4. HD_AMR 편집 화면 (사전 설계)

- **위치**: HD_AMR.Web 신규 페이지 `/amr-job-mapping` (내비 "AMR Job 매핑").
- **구성**: 표(nodeId·mapId·name·mapX/Y/θ·jobIndex·taskIndex·source) + [ACS 동기화] [저장] [행 추가/삭제].
- **동작**:
  - **ACS 동기화**: 티칭 테이블 pull → nodeId 기준 병합(좌표 갱신, 인덱스는 §5 정책).
  - **편집**: jobIndex/taskIndex 인라인 편집 → 저장 시 로컬 JSON 반영, `source=LOCAL`.
  - **검증 보조**: (선택) 현재 AMR pose(Input 20~25)와 선택 노드 좌표 편차 표시.
- **저장소**: 로컬 JSON 파일(`amr_job_mapping.json`) — EF 마이그레이션 불필요, 경량. 어댑터가 런타임 조회.

## 5. 동기화 병합 정책 (결정 필요 세부)

ACS 동기화 시 로컬 편집분(jobIndex) 처리:
- **권장**: 좌표(mapX/Y/θ·name)는 ACS 값으로 갱신, **jobIndex는 로컬(LOCAL) 값 우선 보존**(ACS에 값이 있고 로컬이 비었을 때만 채움). 현장 오버라이드 유지.
- 대안: ACS 우선(강제 덮어쓰기) — 중앙 통제 강할 때. → 운영 정책으로 택1(기본=로컬 보존).

## 6. 어댑터 런타임 (요약, 구현 시)

```
onOrder(node):
  m = localTable.get(node.nodeId)
  if m == null || m.jobIndex == null: → error(noRouteError, "인덱스 미등록"), 미션 Aborted
  amr.SetJobIndex(m.jobIndex); (필요 시 SetTaskIndex)
  amr.SetExecutionControl(START=2)
  poll: 로봇상태(Input 10)·실행중 Task 번호(Input 61)·pose(20~25) → 도착판정(노드 tol)
```

## 7. 관련
- ACS 등록·티칭 테이블: `docs/VDA5050_MOVEMENT_MAPPING_DESIGN.md`, PUT `/api/nodes/{id}/amr-mapping`, GET `/api/tanks/{tank}/amr-teaching-table`.
- HD_AMR 편집 화면 구현: HD_AMR 저장소 `AmrJobMapping.razor` + `AmrJobMappingStore`(스켈레톤).
