# 설계 초안 — order 노드 ↔ AMR Job/Task 매핑 & 이동 실현

VDA5050 order의 노드(맵 좌표)를 TARS-M의 실제 이동으로 잇는 매핑 체계 설계 초안이다.
전제: TARS-M은 **좌표 goto가 없고**, 이동은 사전 티칭 Job/Task를 인덱스로 실행한다
(`docs/VDA5050_SPEC_PLAN.md` 부록 A 정정 참조).

> 상태: **Draft** — §6 결정 포크(벤더 확인)에 따라 최종 확정.

---

## 0. 요약 (결론 먼저)

1. **ACS가 도면 Area 정차위치를 SLAM 맵 좌표로 변환해 node-edge 맵을 만드는 것은 이미 구현되어 있다** — `SeamPlanningService`가 `T_W_D(DrawingToMap)`로 STATION 노드(맵 pose) + TRAVEL 엣지를 생성(`ref.node`/`ref.edge`).
2. 따라서 남은 핵심은 **"맵 좌표 노드를 어떻게 실제 주행으로 바꾸는가"** 이며, 이는 AMR 능력에 따라 두 갈래다:
   - **B안(권장, 좌표 구동)**: AMR에 **범용 goto Job 1개**를 티칭 + 유저변수(Holding 50~199)로 목표 (x,y,θ) 전달 → ACS가 변환한 맵 좌표를 런타임에 밀어넣어 이동. **테이블 업로드·스테이션별 티칭 불필요.**
   - **A안(폴백, 인덱스 구동)**: 스테이션마다 Job/Task를 수동 티칭하고 그 **인덱스를 node.Metadata에 등록**. ACS는 인덱스로 선택. 좌표는 검증·감시·도착판정용.
3. **"매칭 테이블 자동 전송"에 대한 정정**: Modbus는 Job을 **선택·파라미터화**할 수 있으나 **정의(경로)를 업로드하지 못할 가능성**이 크다. 즉 B안이면 "전송할 테이블"이 아예 없고(좌표를 런타임에 push), A안이면 테이블은 "인덱스 등록부"이지 경로 업로드가 아니다. **Job 업로드 API 유무가 벤더 확인 핵심.**
4. **node-edge 맵은 ACS가 만든다(이미 함).** 단 AMR은 이 엣지를 내비게이션에 쓰지 않는다 — AMR이 자체 경로계획을 하므로, 엣지는 **ACS의 순서결정·연결성(다익스트라)** 모델이다.

---

## 1. 이미 있는 자산 (ACS)

| 자산 | 내용 | 위치 |
|---|---|---|
| T_W_D 변환 | 도면 좌표 → 맵 좌표 강체변환 | `DrawingTransform.DrawingToMap` |
| 맵 캘리브레이션 | 맵버전 바인딩 T_W_D 저장 | `ref.map_calibration` |
| STATION 노드 생성 | 정차 도면 pose → 맵 pose 변환 후 `ref.node`(STATION) | `SeamPlanningService`(Phase 1) |
| TRAVEL 엣지 생성 | 최근접 주행노드와 양방향 엣지 | `SeamPlanningService.ConnectNearest` |
| 노드/엣지 모델 | NodeId,MapId,X,Y,Theta,AllowedDevXy/Theta,NodeType,**Metadata(jsonb)** / EdgeId,MaxSpeed,… | `RefEntities.cs` |
| 라우팅 그래프 | 층 내 다익스트라 | `MapGraph`, `GraphLoader` |

**핵심**: `NodeEntity.Metadata`(jsonb)가 비어 있으므로, 여기에 **AMR Job/Task 인덱스 매핑**을 담으면 된다.

---

## 2. 데이터 모델 추가 (매핑 레지스트리)

`ref.node.metadata`(jsonb)에 AMR 이동 실현 정보를 추가한다. 스키마 변경 없이 확장 가능.

```jsonc
// ref.node.metadata (STATION 노드)
{
  "amr": {
    "gotoMode": "VARIABLE | INDEX",   // B안 | A안
    "jobIndex":  12,                   // A안: 티칭된 Job 인덱스
    "taskIndex": 3,                    // A안: (선택) Task 인덱스
    "gotoJobIndex": 1,                 // B안: 범용 goto Job 인덱스(고정)
    "arriveTolXy": 0.08, "arriveTolTheta": 0.07
  }
}
```

- 노드의 맵 좌표(X,Y,Theta)는 **이미 `ref.node`에 저장**되어 있으므로 별도 좌표 필드 불필요.
- 매핑 "테이블" = `ref.node`(맵좌표) + `metadata.amr`(구동정보). ACS 내부 보유, AMR로 업로드하는 개념이 아님(§3).

---

## 3. 이동 실현 두 갈래 (핵심 설계 포크)

### 3.1 B안 — 범용 goto Job + 유저변수 (권장, 좌표 구동)

전제: AMR에 **"유저변수에서 목표 (x,y,θ)를 읽어 그곳으로 주행하는 범용 Job" 1개**를 티칭할 수 있다.

```
[order 노드 도착 요구]
  → 어댑터: 목표 맵좌표(x,y,θ)를 유저변수(Holding 50~) 기록
  → Job Index = gotoJobIndex 선택(Holding 32) + 상태제어 시작(Holding 30=2)
  → AMR 자체 경로계획으로 주행
  → 어댑터: Input 로봇포즈(20~25) 폴링, |pose − 목표| ≤ tol & 측위(맵일치율) OK → 도착
```

- 장점: **스테이션별 티칭 불필요**, ACS 좌표를 그대로 사용(도면 Area → T_W_D → 맵좌표 → 유저변수). 당신이 제안한 흐름 그대로.
- 유저변수 레이아웃(예): `[50]=cmdSeq, [51-52]=X(f32), [53-54]=Y(f32), [55-56]=θ(f32), [57]=go`. **벤더와 확정 필요.**
- 필요 조건(벤더 확인 #1): 범용 goto Job + 유저변수 파라미터 주행 지원 여부.

### 3.2 A안 — 스테이션별 티칭 + 인덱스 등록 (폴백, 인덱스 구동)

전제: 각 정차 스테이션을 로봇 툴로 티칭해 Job/Task를 만들고 인덱스를 부여.

```
[사전] 스테이션마다 티칭 → 인덱스 → ACS: node.metadata.amr.jobIndex 등록
[런타임] order 노드 → jobIndex 선택(Holding 32) + 상태제어 시작 → 도착판정(동일)
```

- ACS는 좌표를 **주행에 쓰지 못하고** 인덱스만 사용. T_W_D 좌표는 **검증·감시·도착판정**용.
- 등록 방식: (a) 티칭 시 운영자가 ACS UI에서 node↔index 입력, 또는 (b) AMR이 인덱스 목록을 노출하면 반자동 매칭.
- 한계: 스테이션 추가/맵 갱신마다 재티칭. 자동화 낮음.

### 3.3 비교

| 항목 | B안(좌표) | A안(인덱스) |
|---|---|---|
| 스테이션 티칭 | 불필요(범용 Job 1회) | 스테이션마다 |
| ACS 좌표 활용 | 주행에 직접 사용 | 검증·감시만 |
| "자동 전송" | 좌표 런타임 push | 인덱스 등록(수동) |
| 유연성(맵 변경) | 높음 | 낮음(재티칭) |
| 의존 | 유저변수 goto Job | Job 티칭 도구 |

---

## 4. 연결·이동 시퀀스 (B안 기준)

```
① AMR 접속 → connection ONLINE
② 측위: (필요 시) initPosition = 포즈탐색(Holding 20+21~26) → 맵일치율(Input 30) 확인
③ ACS: 해당 mapId의 유효 T_W_D 존재 확인(map_version 일치) — 없으면 이동 거부
④ ACS: 도면 Area 정차 → DrawingToMap → ref.node(STATION, 맵좌표)  [이미 구현]
⑤ order 발행: 노드 = 맵좌표, node.metadata.amr.gotoMode=VARIABLE
⑥ 어댑터: 노드별 (x,y,θ)→유저변수 push → goto Job 트리거 → 포즈 폴링 도착판정
⑦ 도착 후 노드 액션(startWeldInspection: 코봇·비전) 실행
⑧ 다음 노드 반복 → 마지막 노드 후 order 완료
```

- ②의 측위와 ③의 T_W_D는 별개다: **측위=로봇이 맵 위 자기 위치 확정**, T_W_D=**도면↔맵 좌표계 정합**(맵버전당 1회 등록). 둘 다 성립해야 도면기반 좌표로 주행 가능.

---

## 5. node-edge 맵을 ACS가 만드는가?

**그렇다 — 이미 만든다.** 다만 역할을 구분해야 한다.

| 계층 | 소유 | 용도 |
|---|---|---|
| node-edge 그래프(맵좌표) | **ACS** (`ref.node`/`ref.edge`, SeamPlanningService) | 스테이션 정의·**순서결정**·연결성(다익스트라)·order 구성 |
| 실제 경로(장애물 회피 궤적) | **AMR**(SLAM 내비) | 노드 간 실주행 |

즉 ACS 엣지는 AMR에 **다운로드되지 않는다**. VDA5050에서도 엣지는 논리적 연결이고 AGV가 자체 주행한다.
따라서 "ACS가 node-edge 맵을 만든다"는 맞지만, 그것은 **ACS의 라우팅 모델**이지 AMR 주행 지도가 아니다.
(맵/점유격자 원본은 AMR 소유 — ADR: ACS는 SLAM 맵 원본을 보유하지 않음.)

---

## 6. 결정 포크 & 벤더 확인 (선결)

| # | 질의 | 결정에 미치는 영향 |
|---|---|---|
| 1 | **범용 goto Job + 유저변수 주행** 지원? | 예 → **B안 채택**(권장). 아니오 → A안 |
| 2 | **Job/Task 정의 업로드 API**(경로 원격 등록) 유무 | 있으면 A안도 자동화 가능(ACS가 경로 업로드) |
| 3 | 유저변수(Holding 50~199) **주행 파라미터 규약** | B안 유저변수 레이아웃 확정 |
| 4 | 인덱스 목록 조회·티칭 관리 방법 | A안 등록 자동화 수준 |
| 5 | 도착·측위 완료 신호(맵일치율 임계) | 도착판정 기준 |

**권장 경로**: 1=예 이면 **B안**으로 확정 → 당신이 제안한 "도면 Area → T_W_D → 맵좌표 → (유저변수) 자동 구동"이 그대로 성립하며, 스테이션 티칭·테이블 업로드가 사라진다.

---

## 7. 다음 작업 (설계 확정 후)

- [ ] 벤더 회신(§6)으로 A/B 확정
- [ ] `ref.node.metadata.amr` 스키마 확정 + SeamPlanningService에 기본값 주입
- [ ] 어댑터 `IAmrMotion.MoveToNodeAsync` — B안(유저변수 goto) / A안(인덱스) 구현 분기
- [ ] 도착판정 유틸(포즈 폴링 vs 노드 tol + 맵일치율)
- [ ] Simulator에 goto Job/유저변수 에뮬레이션 추가
