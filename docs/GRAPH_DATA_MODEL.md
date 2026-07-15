# 그래프 자료구조 및 DB 설계 (VDA 5050 Order 생성 기반)

> VDA 5050에서 토폴로지 그래프(노드/엣지)의 소유자는 마스터 컨트롤(HD_ACS)이다.
> HD_ACS는 그래프에서 경로를 계산하여 Order(노드/엣지 시퀀스 + 액션)로 HD_AMR에 전달한다.
> DB: PostgreSQL + EF Core [ADR-009]

## 1. 데이터 계층 구조 (4계층)

```
① 정적 그래프 (맵 마스터 데이터)     Map ── Node ── Edge          ← 티칭/임포트로 구축, 버전 관리
② 액션 카탈로그                    ActionCatalog                 ← HD_AMR과 협의된 액션 정의 [Q1]
③ 시나리오 (검사 계획)              Scenario ─ InspectionPoint ─ InspectionTask
                                   (Point는 그래프 Node를 참조)
④ 런타임 (미션/주문/상태)           Mission ─ OrderNode/OrderEdge/OrderAction ─ TransitionLog
                                   (미션 생성 시 ①+③에서 생성되는 불변 스냅샷)
```

핵심 원칙:
- **③은 ①을 참조**하고, **④는 ①+③의 스냅샷**이다 — 맵이나 시나리오가 나중에 수정되어도 과거 미션 이력은 당시 값을 보존한다.
- 벽면 검사 대상 좌표(TANK_WALL_LAYOUT의 wall_code + 로컬 좌표)는 그래프 노드가 아니라 **액션 파라미터**에 담는다. 그래프 노드는 AMR이 B면(바닥)에서 정차하는 위치만 표현한다.

## 2. ERD 개요

```
Map 1──N Node 1──N Edge(start/end)      ActionCatalog
              ↑                              ↑
Scenario 1──N InspectionPoint 1──N InspectionTask
    │
Mission 1──N OrderNode 1──N OrderAction
    │    1──N OrderEdge
    │    1──N TransitionLog
```

## 3. 테이블 설계

### ① 정적 그래프
```sql
CREATE TABLE map (
  map_id        text PRIMARY KEY,          -- VDA5050 nodePosition.mapId 로 그대로 사용
  tank_id       text NOT NULL,             -- 화물창 식별 (TANK_WALL_LAYOUT)
  name          text NOT NULL,
  version       int  NOT NULL DEFAULT 1,
  is_active     boolean NOT NULL DEFAULT true
);

CREATE TABLE node (
  node_id       text PRIMARY KEY,          -- VDA5050 node.nodeId (예: "CT1-N012")
  map_id        text NOT NULL REFERENCES map,
  name          text,
  x             double precision NOT NULL, -- 맵 좌표 [m]
  y             double precision NOT NULL,
  theta         double precision,          -- 정차 방향 [rad], null=무관
  allowed_dev_xy    double precision,      -- 허용 위치 편차
  allowed_dev_theta double precision,
  node_type     text NOT NULL,             -- WAYPOINT | INSPECTION_STOP | CHARGING | PARKING
  metadata      jsonb                      -- 확장 (예: 인접 벽면 코드)
);

CREATE TABLE edge (
  edge_id       text PRIMARY KEY,          -- VDA5050 edge.edgeId
  map_id        text NOT NULL REFERENCES map,
  start_node_id text NOT NULL REFERENCES node,
  end_node_id   text NOT NULL REFERENCES node,
  bidirectional boolean NOT NULL DEFAULT true,  -- 양방향이면 경로계산 시 역방향 허용
  max_speed     double precision,
  length        double precision,          -- 경로 비용 (미지정 시 유클리드 거리)
  metadata      jsonb
);
```

### ② 액션 카탈로그 (HD_AMR과 협의된 계약 [Q1])
```sql
CREATE TABLE action_catalog (
  action_type   text PRIMARY KEY,          -- 예: "startInspectionJob", "capture", "emergencyStop"
  scope         text NOT NULL,             -- NODE | EDGE | INSTANT
  blocking_type text NOT NULL,             -- NONE | SOFT | HARD (VDA5050 blockingType)
  param_schema  jsonb,                     -- 파라미터 JSON Schema (검증용)
  description   text
);
```

### ③ 시나리오
```sql
CREATE TABLE scenario (
  scenario_id   uuid PRIMARY KEY,
  name          text NOT NULL,
  version       int  NOT NULL,
  tank_id       text NOT NULL,
  policy        jsonb NOT NULL,            -- 재시도/스킵 정책 (외부화 [ADR-010])
  status        text NOT NULL,             -- DRAFT | RELEASED | RETIRED
  UNIQUE (name, version)
);

CREATE TABLE inspection_point (
  point_id      uuid PRIMARY KEY,
  scenario_id   uuid NOT NULL REFERENCES scenario,
  seq           int  NOT NULL,             -- 검사 순서
  node_id       text NOT NULL REFERENCES node,   -- AMR 정차 노드 (그래프 참조)
  UNIQUE (scenario_id, seq)
);

CREATE TABLE inspection_task (
  task_id       uuid PRIMARY KEY,
  point_id      uuid NOT NULL REFERENCES inspection_point,
  seq           int  NOT NULL,
  action_type   text NOT NULL REFERENCES action_catalog,
  job_ref       text,                      -- HD_AMR에 정의된 검사 작업 식별자
  position      jsonb,                     -- 촬영 위치 파라미터 [ADR-004]: {wall_code, x, y, z ...}
  params        jsonb,                     -- opaque — HD_AMR이 해석
  UNIQUE (point_id, seq)
);
```

### ④ 런타임 (미션 = 스냅샷)
```sql
CREATE TABLE mission (
  mission_id      uuid PRIMARY KEY,
  scenario_id     uuid NOT NULL,
  scenario_ver    int  NOT NULL,
  robot_id        text NOT NULL,           -- fleet-ready [ADR-003]
  order_id        text NOT NULL,           -- VDA5050 orderId
  order_update_id int  NOT NULL DEFAULT 0, -- 재시도/스킵 시 증가
  state           text NOT NULL,           -- Stateless 상태머신 [ADR-010]
  started_at      timestamptz,
  ended_at        timestamptz
);

CREATE TABLE order_node (                  -- Order에 실린 노드의 스냅샷
  mission_id    uuid NOT NULL REFERENCES mission,
  sequence_id   int  NOT NULL,             -- VDA5050: 노드=짝수 (0,2,4…)
  node_id       text NOT NULL,
  x             double precision NOT NULL, -- 스냅샷 좌표 (맵 수정과 무관하게 보존)
  y             double precision NOT NULL,
  theta         double precision,
  released      boolean NOT NULL,          -- Base 선릴리즈 [ADR-002] → 기본 true
  status        text NOT NULL DEFAULT 'PENDING',  -- state.lastNodeId 반영
  PRIMARY KEY (mission_id, sequence_id)
);

CREATE TABLE order_edge (
  mission_id    uuid NOT NULL REFERENCES mission,
  sequence_id   int  NOT NULL,             -- VDA5050: 엣지=홀수 (1,3,5…)
  edge_id       text NOT NULL,
  start_node_id text NOT NULL,
  end_node_id   text NOT NULL,
  released      boolean NOT NULL,
  PRIMARY KEY (mission_id, sequence_id)
);

CREATE TABLE order_action (
  action_id     uuid PRIMARY KEY,          -- VDA5050 actionId — state.actionStates 대조 키
  mission_id    uuid NOT NULL REFERENCES mission,
  node_sequence_id int NOT NULL,           -- 어느 노드에 붙은 액션인가
  action_type   text NOT NULL,
  blocking_type text NOT NULL,
  params        jsonb,
  status        text NOT NULL DEFAULT 'WAITING', -- WAITING|INITIALIZING|RUNNING|FINISHED|FAILED
  result        jsonb,                     -- 성공/실패 응답 기록 [ADR-004]
  attempts      int NOT NULL DEFAULT 0
);

CREATE TABLE transition_log (              -- 상태머신 전이 이벤트 [ADR-010]
  id          bigserial PRIMARY KEY,
  mission_id  uuid NOT NULL REFERENCES mission,
  from_state  text NOT NULL,
  to_state    text NOT NULL,
  trigger     text NOT NULL,               -- 예: StateReceived, ActionFailed, ConnectionLost
  payload     jsonb,
  occurred_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_translog_mission ON transition_log (mission_id, occurred_at);
```

## 4. C# 도메인 자료구조 (핵심)

```csharp
// ── ① 인메모리 그래프: 경로 계산용 (기동 시 DB에서 로드, 맵 버전 단위 캐시)
public sealed class MapGraph
{
    private readonly Dictionary<string, NodeEntity> _nodes = new();
    private readonly Dictionary<string, List<(EdgeEntity edge, string toNode)>> _adj = new();

    public IReadOnlyList<(NodeEntity node, EdgeEntity? viaEdge)> FindPath(
        string fromNodeId, string toNodeId)
    {
        // Dijkstra (비용 = edge.Length ?? 유클리드 거리)
        // bidirectional=false 엣지는 정방향만 인접 리스트에 등재
        ...
    }
}

// ── ④ Order 빌더: 시나리오 + 그래프 + 현재 위치 → VDA 5050 Order
public sealed class OrderBuilder
{
    // sequenceId 규칙: 노드 0,2,4… / 엣지 1,3,5… (VDA 5050)
    public Vda5050Order Build(Mission mission, MapGraph graph,
                              string currentNodeId, Scenario scenario)
    {
        var seq = 0;
        var order = new Vda5050Order { OrderId = mission.OrderId,
                                       OrderUpdateId = mission.OrderUpdateId };
        var cursor = currentNodeId;
        foreach (var point in scenario.Points.OrderBy(p => p.Seq))
        {
            foreach (var (node, edge) in graph.FindPath(cursor, point.NodeId))
            {
                if (edge != null) order.Edges.Add(ToOrderEdge(edge, seq++));   // 홀수
                var on = ToOrderNode(node, seq++);                              // 짝수
                if (node.NodeId == point.NodeId)
                    on.Actions.AddRange(point.Tasks.Select(ToAction));          // 검사 액션 부착
                order.Nodes.Add(on);
            }
            cursor = point.NodeId;
        }
        order.Nodes.ForEach(n => n.Released = true);   // Base 선릴리즈 [ADR-002]
        order.Edges.ForEach(e => e.Released = true);
        return order;
    }
}
```

## 5. 상태 대조 (state → DB 반영)

| VDA 5050 state 필드 | 반영 대상 |
|---|---|
| `lastNodeId` / `lastNodeSequenceId` | order_node.status → PASSED, 미션 진행률 |
| `actionStates[].actionId/actionStatus` | order_action.status/result (actionId로 대조) |
| `nodeStates`/`edgeStates` (잔여) | 남은 경로 표시 (UI) |
| `errors[]` | 정책 엔진 트리거 (재시도→orderUpdateId 증가 Order 재발행 / 스킵 / 알람) |
| `connection` 토픽 | Mission DISCONNECTED ↔ RUNNING 전이 |

재접속 동기화 [ADR-002]: 최신 state 1건이면 전체 대조가 가능하도록, **대조 키(actionId, sequenceId)를 ACS가 발급·보존**하는 것이 이 설계의 핵심이다.

## 6. 그래프 구축 방법 (⬜ Q6과 연동)
- 후보 A: **LIF (Layout Interchange Format, VDMA)** — VDA 5050 생태계의 맵 교환 표준 JSON. 외부 도구/HD_AMR과 그래프를 주고받을 때 유리 → import/export 지원 권장
- 후보 B: 전개도 기반 자체 에디터 — WPF 전개도 뷰[TANK_WALL_LAYOUT] 위에서 노드/엣지 편집
- 화물창은 정형 격자 구조이므로 **파라메트릭 생성**(멤브레인 그리드 간격 기반 노드 자동 생성) 병용 검토

## 7. NAMUGA_ACS DB 구조와의 대응

NAMUGA_ACS는 그래프를 `NA_{계층}_{이름}` 네이밍의 DB-first 스키마로 구축했다
(EF Core `OnModelCreating`에서 `ToTable`/`ToView` 수동 매핑, code-first 마이그레이션 미사용).

### NAMUGA_ACS 계층 접두 규칙
| 접두 | 의미 | 예시 |
|---|---|---|
| NA_R_ | Reference (기준/마스터) | NODE, LINK, STATION, LOCATION, BAY, ZONE, VEHICLE |
| NA_T_ | Transaction (런타임) | TRANSPORTCMD, INTERSECTION |
| NA_Q_ | Queue | TRANSPORTCMDREQUEST |
| NA_H_ | History | VEHICLEHISTORY, TRANSPORTCMDHISTORY |
| NA_A_ / NA_L_ | Alarm / Log | ALARM, ALARMSPEC / LOGMESSAGE |
| NA_U_ / NA_C_ / NA_X_ | UI 연동 / 통신설정 / 시스템·계정 | UI_TRANSPORT / MQTT / USER |
| *_VW | 조인 뷰 (평탄화 조회) | LOCATION_VW, LINK_VW, STATION_VW |

### 그래프 모델 대응표
| NAMUGA_ACS | 구조 | HD_ACS 대응 | 비고 |
|---|---|---|---|
| NA_R_NODE (node_id, type, xpos/ypos/zpos) | 위상 정점 | `node` | 동일 개념. HD_ACS는 정차 방향(theta)·허용 편차 추가 (VDA 5050 요구) |
| NA_R_LINK (from→to, length, speed, availability, load, agvType) | 방향성 간선 | `edge` | 동일 개념. availability/load/agvType 같은 운영 제약은 metadata jsonb로 수용 |
| NA_R_STATION (linkId + distance) | **링크 위 오프셋 위치** | 없음 — `node_type=INSPECTION_STOP` 노드로 대체 | VDA 5050은 노드 정차 모델이므로 링크 오프셋 계층 불필요 |
| NA_R_LOCATION / BAY | 스테이션 위 작업 포트 | 없음 — InspectionPoint(시나리오 계층)가 역할 대체 | 반송 도메인 전용 개념 |
| NA_R_ZONE / LINK_ZONE | 구역 계층 | **초기 도입** — `zone`/`zone_member`로 승계 (층/구역 관리, 8절) | 4층 슬라이스 구조 대응 |
| NA_T_INTERSECTION | 교차 점유 제어 | 초기 미도입 | fleet(다중 로봇) 확장 시 승계 [ADR-003] |
| NA_H_VEHICLESEARCHPATH | 경로 탐색 이력 | transition_log가 유사 역할 | |
| PathManager 인메모리 로드 + Dijkstra | | MapGraph + Dijkstra | 패턴 그대로 승계 |

### HD_ACS 스키마 운영 방식 (제안)
- **네이밍 확정 [C안]**: PostgreSQL 스키마(namespace)로 계층을 구분하고 테이블은 snake_case —
  `ref.node`, `ref.edge`, `run.mission`, `run.order_node`, `hist.transition_log`, `sys.app_user`.
  NAMUGA의 NA_{계층}_ 접두 개념을 DB 스키마로 승계한 형태이며, 따옴표 없는 소문자 식별자라
  현장 psql/DBeaver 수작업 쿼리에 유리 (확정판: db/schema.sql, docs/DB_SCHEMA.md)
- **매핑 방식**: NAMUGA는 DB-first 수동 매핑이나, HD_ACS는 신규 스키마이므로
  **EF Core code-first + 마이그레이션** 채택 권장 (폐쇄망 배포 시 마이그레이션 스크립트 export)
- **뷰 활용 승계**: 전개도/모니터링 UI 조회용 평탄화 뷰(예: 미션 진행률 뷰, 노드-검사결과 조인 뷰)는
  NAMUGA의 *_VW 패턴을 따른다

## 8. 멀티 레벨 구조 — 4층 슬라이스 + 엘리베이터

화물창은 건조 단계에서 **바닥부터 4개 층(Level)으로 슬라이스**되어 구성되며,
AMR은 **엘리베이터로 층간 이동**한다. 그래프·존·Order 설계에 다음을 반영한다.

### 8.1 층(Level) 모델링 — 층 = 맵
- VDA 5050의 `nodePosition.mapId`가 층 구분에 그대로 대응된다 → **1층 = 1 map** 으로 모델링:
  `map_id = "CT1-L1" … "CT1-L4"`, `map` 테이블에 `level int` 컬럼 추가, `tank_id`로 묶음
- 같은 맵 안의 노드/엣지는 기존 설계 그대로. 층이 다른 노드는 mapId가 다르다.

```sql
ALTER TABLE map ADD COLUMN level int NOT NULL DEFAULT 1;   -- 1(바닥)~4
```

### 8.2 엘리베이터 모델링 — 수동 이송 (자율 주행 대상 아님)

**엘리베이터는 작업자가 수동으로 운영한다 [Q9 해소].** 따라서 층간 이동은
자율 주행 경로가 아니라 **운영 절차**이며, 그래프에는 다음만 반영한다:

```sql
-- 각 층의 엘리베이터 탑승 위치 = 노드 (같은 층 내에서는 자율 주행으로 접근 가능)
-- node_type = 'ELEVATOR'

-- 층간 연결 표시용 엣지 (경로 계산 제외 대상)
ALTER TABLE edge ADD COLUMN edge_type text NOT NULL DEFAULT 'TRAVEL';
-- TRAVEL          : 일반 주행 엣지 (경로 계산 포함)
-- MANUAL_TRANSFER : 층간 수동 이송 표시 (경로 계산·Order 생성에서 항상 제외,
--                   시나리오 분해와 UI 층 연결 표시에만 사용)
```

- **MapGraph 경로 계산은 층(map) 내부로 한정**된다 — MANUAL_TRANSFER 엣지는 탐색에서 제외
- 한 미션의 모든 노드는 같은 map_id를 갖는다 (층 단위 미션 원칙, 8.4절)

### 8.3 존(Zone) 계층 — NAMUGA NA_R_ZONE 승계
```sql
CREATE TABLE zone (
  zone_id    text PRIMARY KEY,
  map_id     text NOT NULL REFERENCES map,     -- 존은 층에 속함
  name       text NOT NULL,
  zone_type  text NOT NULL,        -- FLOOR | AREA | ELEVATOR_CELL | RESTRICTED
  geometry   jsonb                 -- 폴리곤 (UI 표시/포함 판정)
);

CREATE TABLE zone_member (          -- NA_R_LINK_ZONE 대응 (노드 소속으로 단순화)
  zone_id  text NOT NULL REFERENCES zone,
  node_id  text NOT NULL REFERENCES node,
  PRIMARY KEY (zone_id, node_id)
);
```
용도: ① 층/구역 단위 검사 진행률 집계 (UI 전개도·3D 뷰 [ADR-005]),
② 접근 제한 구역(RESTRICTED) 경로 계산 배제, ③ fleet 확장 시 존 점유 제어의 기반 [ADR-003]

### 8.4 층 전환 운영 절차 (수동) 와 시스템 지원

**원칙: 미션은 층 단위로 분할된다.** 여러 층에 걸친 시나리오는 층별 미션 시퀀스로
분해되고, 층 사이는 수동 이송 절차가 끼어든다.

```
[L1 미션 COMPLETED]
   → 미션 시퀀스 상태: WAITING_FLOOR_TRANSFER (다음 층 미션 잠금)
   → 작업자: AMR을 엘리베이터로 이송 (수동 운전/수동 조작)
   → 작업자: HD_ACS UI에서 로봇의 현재 층(존)을 L2로 수동 변경
        - Operator 권한 필요, 변경 이력 감사 로그 기록
   → HD_ACS: VDA 5050 instantAction `initPosition` 전송
        (새 mapId + 탑승 노드 초기 포즈) — HD_AMR 재측위(re-localization) 지원
   → 검증 게이트: HD_AMR state의 agvPosition.mapId 가 L2로 보고될 때까지
        다음 미션 릴리즈 금지 (수동 입력과 로봇 실측의 정합 확인)
   → [L2 미션 RELEASED]
```

시스템 반영 사항:
- **로봇 컨텍스트 테이블** — 수동 지정 값과 로봇 보고 값을 분리 보관·대조:
```sql
CREATE TABLE robot_context (
  robot_id          text PRIMARY KEY,
  manual_map_id     text,            -- 작업자가 지정한 현재 층
  manual_updated_by text,
  manual_updated_at timestamptz,
  reported_map_id   text,            -- HD_AMR state 최신 보고값
  reported_at       timestamptz
);
```
- 미션 상태머신에 `WAITING_FLOOR_TRANSFER` 상태 추가 [ADR-010, INSPECTION_SCENARIO.md]
- Order 릴리즈 가드: `mission.map_id == robot_context.reported_map_id` 일 때만 릴리즈
- 층 전환 중 통신 두절은 문제되지 않는다 — 어차피 로봇은 미션이 없는 상태이며,
  재접속 후 state 보고로 정합이 확인되면 진행한다 [ADR-002와 정합]

### 8.5 시나리오·UI 영향
- InspectionPoint는 node 참조를 통해 층이 결정되므로 스키마 변경 불필요.
  단, 시나리오 편집기는 층 필터/층별 순서 편집을 지원해야 한다
- 전개도 UI[TANK_WALL_LAYOUT]는 층 선택기(L1~L4)를 갖고, 벽면(SM/PM 등)은 층별 슬라이스로 분할 표시
- 진행률 집계 축: 시나리오 → 층(FLOOR 존, 미션 단위) → 벽면 → 지점
- UI에 **로봇 층(존) 수동 변경 기능** 필수: Operator 권한, 현재 수동값/로봇 보고값 나란히 표시, 불일치 시 경고
