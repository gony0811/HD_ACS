# 검사 시나리오 모델

> 원칙: HD_ACS의 시나리오는 "어느 지점에서 어떤 검사 작업을 어떤 순서로" 수준까지만 정의한다.
> 검사 작업의 실행 방법(협동로봇 자세 시퀀스, 장비 세부 제어)은 HD_AMR에 정의되어 있으며 ACS는 알지 못한다 [ADR-001].
> 데이터 스키마는 VDA 5050 액션 카탈로그(Q1) 확정 후 구체화한다.
> 미션은 층(map) 단위로 분할된다 — 여러 층에 걸친 시나리오는 층별 미션 시퀀스로 분해되고, 층 전환은 작업자 수동 절차이다.

## 1. 개념 계층

```
Scenario (검사 시나리오)
 └── InspectionPoint[] (검사 지점, 순서 있음)
      ├── amr_goal          : AMR 목표 위치 — VDA 5050 Order의 노드로 변환
      └── InspectionTask[]  : 지점 내 검사 작업 — VDA 5050 커스텀 액션으로 변환
           ├── job_ref      : HD_AMR에 정의된 검사 작업 식별자 (실행 내용은 HD_AMR 소관)
           ├── position     : 촬영 명령에 전달하는 위치 정보 [ADR-004]
           └── params       : HD_AMR이 해석하는 불투명(opaque) 파라미터 — ACS는 저장·전달만
```

- **Scenario**: 운영자가 실행하는 단위. 이름, 버전, 대상(화물창/구역), 지점 목록, 운영 정책 포함
- **InspectionPoint**: AMR이 정차하는 하나의 위치. 한 지점에서 여러 검사 작업 수행 가능
- **InspectionTask**: HD_AMR의 검사 작업을 참조하는 최소 지시 단위 — 웨이포인트·장비 파라미터 같은 실행 세부는 포함하지 않는다

## 2. 미션 인스턴스

시나리오 실행 시 **Mission** 인스턴스가 생성되어 실행 이력과 분리 관리된다.
Order 생성 시 두절 내성을 위해 시나리오 전체(또는 큰 구간)를 Base로 선릴리즈한다 [ADR-002].

```
Mission
 ├── scenario_ref (시나리오 ID + 버전), robot_id
 ├── status, started_at, ended_at
 ├── transition_log[] — 상태머신 전이 이벤트 기록 (Stateless + EF Core [ADR-010])
 └── PointResult[] / TaskResult[]
      ├── status (SUCCESS / RETRIED / SKIPPED / FAILED)
      ├── attempts, error_code (HD_AMR 보고 기준)
      └── position, timestamp — 검사 S/W와 대조용 키 [ADR-004, Q2]
```

## 3. 상태 정의 (Stateless 상태머신 [ADR-010])

상태의 진실은 HD_AMR의 VDA 5050 `state` 보고이며, ACS 상태머신은 이를 트리거로 전이한다 (robot-is-truth).

### 미션 수준
| 상태 | 설명 |
|---|---|
| CREATED | 미션 생성, Order 미릴리즈 |
| RELEASED | Order Base 선릴리즈 완료 |
| WAITING_FLOOR_TRANSFER | 층 단위 미션 시퀀스에서 다음 층 대기 — 작업자 수동 이송 + 존 변경 + mapId 검증 후 다음 미션 릴리즈 (GRAPH_DATA_MODEL.md 8.4) |
| RUNNING | HD_AMR 실행 중 (state 보고 수신) |
| DISCONNECTED | 통신 두절 — HD_AMR은 온보드 실행 지속, ACS는 마지막 상태 유지 표시 |
| PAUSED | 운영자 일시정지 (instantAction) |
| COMPLETED | 전 지점 처리 완료 (스킵 포함) |
| ABORTED | 정책 또는 운영자에 의해 중단 |

### 지점 수준 (HD_AMR state의 lastNodeId/actionStates로부터 도출)
| 상태 | 설명 |
|---|---|
| PENDING | 대기 |
| MOVING | 해당 노드로 이동 중 |
| INSPECTING | 지점 내 검사 액션 실행 중 |
| DONE | 지점 내 전체 액션 완료 |
| SKIPPED | 정책에 따라 스킵 (Order 갱신으로 반영) |
| FAILED | 실패 확정 |

## 4. 실패 처리 정책 (시나리오 데이터로 외부화 [ADR-010])

ACS는 HD_AMR이 보고하는 실패(errors, actionStatus=FAILED)의 유형 코드를 기준으로 정책을 적용한다.
실패의 원인 세부(자세 실패인지, 장비 오류인지)는 HD_AMR의 error 분류를 따른다.

| HD_AMR 보고 실패 유형 | 기본 정책 (초안) |
|---|---|
| 이동 실패 (경로 불가/장애물) | 재시도 2회 → 지점 스킵 + 알람 |
| 이동 타임아웃 | 재시도 1회 → 스킵 + 알람 |
| 검사 작업 실패 (액션 FAILED) | 해당 액션 재시도 1회 → 액션 스킵 기록 |
| 촬영 실패 응답 [ADR-004] | 재시도 2회 → 실패 기록 + 알람 |
| 배터리 저전압 | 미션 일시정지 → 복귀 정책 (TBD) |
| 로봇 측 비상 이벤트 | 미션 중단, 운영자 개입 필수 |

정책 파라미터(재시도 횟수, 스킵 조건)는 시나리오 데이터에 저장되어 코드 수정 없이 조정 가능하다.

## 5. 시나리오 예시 (개념)

```yaml
scenario:
  name: "1번화물창_횡방향용접부_정기검사"
  version: 3
  policy: { move_retry: 2, task_retry: 1, on_point_fail: skip_and_alarm }
  points:
    - id: P01
      amr_goal: { node_id: "N_P01", x: 12.4, y: 3.1, theta: 90 }   # → Order 노드
      tasks:
        - id: T01
          job_ref: "WELD_SEAM_SCAN_A"        # HD_AMR에 정의된 검사 작업 — 내용은 ACS 관여 밖
          position: { x: 12.4, y: 3.1, z: 0.0 }   # 촬영 명령 위치 파라미터 [ADR-004]
          params: { profile: "default" }      # opaque — HD_AMR이 해석
    - id: P02
      ...
```
