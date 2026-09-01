# 아덴트로봇 TARS-M v3 REST API — 실물 엔드포인트 목록

| 항목 | 내용 |
|---|---|
| 출처 | 로봇 `http://<robot>/api/v3/swagger-ui-init.js` 내 인라인 `swaggerDoc` (2026-09-01 수집) |
| 스펙 | **Swagger 2.0**, title `TARS-AMR`, version **3.18.0**, schemes `[http]` |
| 성격 | **경로·요청 body만 존재. 응답 스키마는 전 항목 `200 OK` 뿐** — 응답 필드·에러코드는 여전히 미확보 |
| 관련 | `ADENT_VENDOR_INQUIRY.md`(1차+회신) · `ADENT_VENDOR_INQUIRY_2.md`(2차) · `ADENT_TARSM_V3_OPENAPI.yaml`(의미 주석본) |

> ⚠️ **문서가 아니라 코드에서 추출한 것**이다. 벤더가 제공한 사양서가 아니므로, 여기 없는 파라미터가 실제로는 동작할 수 있고 그 반대도 가능하다.

---

## 0. 설계에 영향이 큰 발견 4가지

### 0-1. `POST /robot/pose` = REST 재측위 — **층 전환을 REST 단일 경로로 구현 가능**
body `{x, y, rz, tuneFlag}`, 설명 "로봇 위치 보정". 1차 질의 3장(Modbus PoseSearch)의 REST 대체 경로가 존재한다는 뜻.
→ 층 전환 게이트를 **Modbus 없이** 설계할 수 있다. `tuneFlag`의 의미(미세보정 여부? 탐색 수행 여부?)만 확인하면 된다.

### 0-2. `GET /robot/pose`에 **맵 일치율 포함**
설명: "로봇의 현재 좌표 조회 (좌표기반 라이다 센싱 값 및 **맵 일치율** 포함)".
→ 층 전환 검증 지표(Modbus Input 30)를 REST로 읽을 수 있다. **남은 건 "얼마 이상이면 신뢰"인 임계값뿐**(2차 A-3).

### 0-3. `POST /robot/modbus` — 홀딩 레지스터를 REST로 쓸 수 있음
`{address, value}` 및 `/robot/modbus/bulk`.
→ Modbus TCP 세션을 따로 열지 않고도 PoseSearch(Holding 20~26)·주행정지(Holding 12) 등 **레지스터 기반 기능 전부를 REST로 트리거 가능**. 통신 경로를 REST 하나로 통일할 수 있다.

### 0-4. `POST /map/load` — 층별 맵 전환이 REST로 가능 → **통합맵의 대안이 존재**
`/map`(목록), `/map/info`(현재 로드된 맵), `/map/load {name}`.
→ 벤더가 권한 통합맵 방식 외에, **층별 맵 4장을 두고 전환**하는 선택지가 살아 있다. 맵 로드 소요시간과 로드 직후 재측위 절차만 확인하면 비교 가능(2차 D-5 재작성 대상).

---

## 1. 로봇 상태·제어 (`/api/v3/robot/...`)

| Method | Path | 설명 | ACS/온보드 사용 |
|---|---|---|---|
| GET | `/robot/info` | 로봇 제조 정보 | – |
| GET/POST | `/robot/device/info` | 로봇 설정 조회/갱신 (wheel·size·movement·obstacle·camera·lidar·sound) | 참고 |
| GET/POST | `/robot/validation/info` | 활성화키 조회/설정 | – |
| **GET** | **`/robot/status`** | **현재 상태 조회** — 회신상 `schedule`·`error` 필드로 이동 완료·실패 판정 | **주행 판정 정본** |
| GET | `/robot/brief` | 현재 상태 조회 (**Modbus 정보 포함**) | 에러코드/레지스터 확인용 |
| **GET** | **`/robot/pose`** | **현재 좌표 + 라이다 + 맵 일치율** | **층 전환 게이트 판정** |
| **POST** | **`/robot/pose`** | **위치 보정(재측위)** `{x, y, rz, tuneFlag}` | **initPosition 구현체** |
| GET | `/robot/state` | 현재 주행 단계 조회 | 보조 |
| **POST** | **`/robot/state`** | **주행 제어(시작/정지/일시정지)** `{state: "stop"}` | **Order 취소·비상정지** |
| POST | `/robot/hold` | 주행 일시정지 `{block: bool}` | 일시정지 |
| **POST** | **`/robot/go`** | **좌표 이동** `{x, y, rz, stopFlag}` — 회신상 **큐 append** | **주행 발행** |
| POST | `/robot/job/add` | 현재 Task에 좌표 기반 거점 Job 추가 `{x, y, rz, stopFlag}` | `/go`와 구분 필요 |
| POST | `/robot/task` | 작업 시작할 Task 설정 `{name, index}` | 큐 운용 |
| **POST** | **`/robot/task/clear`** | **설정한 Task 클리어** | **큐 비우기 후보** |
| POST | `/robot/move` | 속도 직접 제어 `{linear, angular}` (200 ms timeout) | 미사용(teleop 불요) |
| POST | `/robot/cart` | 주행모드/수동모드 전환 `{cart: bool}` | 운영 |
| GET | `/robot/lidar`, `/robot/lidar/all` | 라이다 센싱값(필터/원본) | 진단 |
| GET | `/robot/obstacle` | 검출된 장애물의 맵상 좌표 | 진단·오검사 분석 |
| POST | `/robot/odom` | Odometry 초기화 | – |
| **POST** | **`/robot/modbus`**, `/robot/modbus/bulk` | **홀딩 레지스터 값 설정** `{address, value}` | **Modbus 기능 REST화** |
| POST | `/robot/recover` | 맵·계획 초기화 (Empty Map 로드) | ⚠️ 위험, 사용 금지 |
| POST | `/robot/shutdown`, `/robot/reboot`, `/robot/restart` | 종료 / 재부팅 / SW 재시작 | 운영 |
| GET/POST | `/robot/sound/device`, `/robot/sound`, `/robot/sound/play`, `/robot/sound/volume` | 사운드 장치·파일·재생·볼륨 | 알람 연동 여지 |
| DELETE | `/robot/sound/{name}` | 커스텀 사운드 삭제 | – |
| POST | `/robot/motor/mode` | 모터 PWM 모드 `{mode: pulse\|serial}` | – |

## 2. 맵 (`/api/v3/map/...`)

| Method | Path | 설명 | 비고 |
|---|---|---|---|
| GET | `/map` | 저장된 맵 목록 | 층별 맵 운용 시 핵심 |
| GET | `/map/info` | **현재 로드된 맵 정보** | 맵 버전 식별(2차 D-4) 후보 |
| **POST** | **`/map/load`** | **맵 로드** `{name: "example.map"}` | **층별 맵 전환 경로** |
| GET | `/map/size` | 맵 크기 정보 | 200m 한도 확인 |
| GET | `/map/cache`, `/map/cost/`, `/map/content/name/{name}` | 맵/비용지도 이미지(png) | UI·진단 |
| POST | `/map/save` | 현재 데이터(맵/플랜/구역) 저장 `{mapName}` | |
| POST | `/map/import` / `/map/export` | 맵 파일 업로드 / 다운로드 | 통합맵 배포 |
| POST | `/map/scan/on` / `/map/scan/off` | 맵 스캔 시작/종료 | 층별 스캔 |
| POST | `/map/wall/shape` | 가상벽 유형(사각/다각) | |
| DELETE | `/map/{name}` | 맵 삭제 | ⚠️ |

## 3. 플랜 — 거점·마커·구역 (`/api/v3/plan/...`)

전부 `list` / `{id}` GET·PATCH·DELETE / POST 생성의 CRUD 세트다.

| 리소스 | 경로 | 생성 body 요지 |
|---|---|---|
| **거점(waypoint)** | `/plan/waypoint[...]` | **`{name, pose:{x,y,rz}}`** — 좌표 직접 등록·수정 가능 |
| 마커(marker) | `/plan/marker[...]` | `{name, type, pose}` |
| 가상벽(wall) / 그룹 | `/plan/wall[...]`, `/plan/wallgroup[...]` | `{name, points[]}` / `{name, parts[]}` |
| 구역(area) | `/plan/area[...]` | `{name, points[]}` |
| 일방통행(oneway) | `/plan/oneway[...]` | `{name, width, height, pose}` |
| 속도제한(speedLimit) | `/plan/speedLimit[...]` | `{name, points[], velocity}` |
| 가변구역(dynamicarea) | `/plan/dynamicarea[...]` | `{name, points[]}` |
| 태스크(task) | `/plan/task/list`, `/plan/task/{id}`, `/plan/task`, `/plan/task/append/{taskId}`, `/plan/task/remove/{taskId}`, `/plan/task/job/{taskId}/{jobId}` | 태스크·잡 구성 |
| 태스크 병합 | `POST /plan/task/combine` | `{tasks, type}` — 여러 Task 병합해 스케줄 로드 |

> **거점 등록이 REST로 가능**하다는 점이 중요하다(2차 D-2 해소). ACS가 산출한 정차 좌표를 맵 에디터 수작업 없이 밀어 넣을 수 있다. 단 통합맵 좌표계 기준이어야 한다.

## 4. 기타

| Method | Path | 설명 |
|---|---|---|
| POST | `/api/v3/proxy` | 로봇의 유선/무선 네트워크 간 REST 요청 프록시 `{method, url, data}` |

---

## 5. 이 스펙으로도 답이 안 나온 것 (2차 질의 유효)

1. **응답 스키마 전무** — 모든 엔드포인트가 `200 OK`뿐. `status.schedule`·`status.error`·`pose`의 실제 필드명과 값 목록을 알 수 없다.
2. **에러코드 목록 없음** — 실패 응답 `code` 체계도, 로봇 에러코드도 스펙에 없다.
3. **`/robot/state`의 `state` 값 목록** — 예시는 `"stop"` 하나. `start`/`pause` 외에 큐를 비우는 값이 있는지 불명.
4. **`/robot/pose`의 `tuneFlag` 의미** — 재측위 탐색 수행 여부인지, 미세 보정 스위치인지.
5. **`/go` vs `/job/add` vs `/task`의 관계** — 어느 것이 회신에서 말한 "큐"인지, `task/clear`가 `/go` 큐도 비우는지.
6. **맵 일치율의 신뢰 임계값** — 값은 읽히지만 판정 기준이 없다.
