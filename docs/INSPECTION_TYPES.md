# 검사 실행 타입 카탈로그 (Inspection Types)

> HD_ACS가 HD_AMR에 `startWeldInspection` 액션을 보낼 때 구분해야 하는 **검사 타입**을
> 면(Wall)·용접 형상 기준으로 정리한 표준안. 대상 화물창은 **KC-2B 멤브레인** 시스템.
> 상태: **부분 반영** — `seamType` 5값 카탈로그(`LINE`·`CROSS3`·`CROSS4`·`CORNER2`·`CORNER3`, §3)는 ACS가
> 수용·발행·UI 지정까지 구현(2026-09-15). 면 자세별 레시피 라이브러리(§5)·HD_AMR 실행은 제안·미구현([협의 N13]).
> 용접선 정의·거울 분리 등은 §9 결정 대기.

---

## 1. 책임 경계 (먼저 확인) ⚠️

| 구분 | 책임 | 내용 |
|------|------|------|
| **HD_ACS** | *무엇을 / 어디를* | 검사 대상 용접선 기하(위치·시작/끝), 검사 조건(profileId), 정차점·standoff 산출 |
| **HD_AMR** | *어떻게* | 툴 회전각(수직/수평)·접근 자세·검사 시퀀스 — **ACS가 명령하지 않음** |

- **툴 수직/수평 회전은 ACS 명령 축이 아니다.** HD_AMR이 seam 벡터(start→end)와 노드 theta(벽 정면)에서 **자동 유도**한다 (VDA5050 사양서 §4.4, ADR-004, 단일 상대 원칙).
- 따라서 검사 타입은 **별도 액션 동사가 아니라 `startWeldInspection` 하나 + 파라미터(주로 `profileId`) 조합**으로 표현한다.

---

## 2. 대상 면(Wall ID)과 면 자세 분류

화물창 단면은 팔각 프리즘 + 마구리 2면 = **10면** (`TankGeometry.GenerateWalls`).
정차점·standoff·접근 방향은 **면 자세**로 결정된다.

| 면 자세군 | Wall ID | 자세 | ACS 정차/접근 특성 |
|-----------|---------|------|--------------------|
| **바닥** | `B` | 수평(위 향함) | 바닥 위 정차, 위→아래 촬영 |
| **천장** | `T` | 수평(아래 향함) | 아래→위 촬영 |
| **수직 평면벽** | `SM` `PM` `F` `A` | 수직 | 벽 정면 이격 정차 |
| **하부 챔퍼** | `SL` `PL` | 경사(45°) | 경사 정면 이격 정차 |
| **상부 챔퍼** | `SU` `PU` | 경사(45°) | 경사 정면 이격 정차 |

→ **면 자세 = 5군.** standoff·정차각은 이미 `ref.wall` 법선에서 자동 산출된다(별도 명령 불필요).

---

## 3. 검사 형상 유형 = `seamType` 카탈로그 (5종, 1:1)

멤브레인 용접선은 코로게이션 격자를 따라 형성된다. 도면 규약: **360mm 주기 점선 = 코로게이션 top 마커(검사 대상 아님)**, **실선 = 용접선**.

용접선 **형상**은 ACS가 계획 단계에서 도면으로 추출해 VDA `params.seamType`로 전달하는 **5값 카탈로그**로 표현한다. seamType 값과 이 카탈로그는 **1:1**이다.

| `seamType` | 형상 유형 | 설명 | 위치 |
|-----------|-----------|------|------|
| `LINE` | **직선 seam** | 면 위의 직선 용접선 구간 | 면 내부 |
| `CROSS3` | **3갈래 교차(T/Y)** | 용접선 3개가 만나는 T/Y자 교차 | 면 내부 격자 교차 |
| `CROSS4` | **4갈래 교차(十)** | 용접선 4개가 만나는 십자 교차 | 면 내부 격자 교차 |
| `CORNER2` | **2면 코너** | 두 면이 만나는 이면각 모서리 용접부 | 두 면 접합 모서리 |
| `CORNER3` | **3면 코너** | 세 면(마구리+옆면 2)이 만나는 삼면 코너 용접부 | 삼면 코너 |

→ **형상(=`seamType`) = 5종.** `CROSS`(교차)는 갈래 수(3/4)로, `CORNER`(코너)는 접합 면 수(2/3)로 세분한다.

---

## 4. 검사 타입 매트릭스 (유효 조합)

검사 **레시피**는 `seamType`(형상) × **면 자세**의 조합으로 결정된다. ACS 계약은 `seamType`(5값) + `wall_code`(면 자세 판정 키)까지이고, 조합→레시피 유도·실행은 HD_AMR의 몫이다(§5, VDA §8.5.1).

| `seamType` \ 면자세 | 바닥 B | 천장 T | 수직벽 SM/PM/F/A | 하부챔퍼 SL/PL | 상부챔퍼 SU/PU | 코너 |
|---|:--:|:--:|:--:|:--:|:--:|:--:|
| `LINE` 직선 | ✓ | ✓ | ✓ | ✓ | ✓ | — |
| `CROSS3` 3갈래 | ✓ | ✓ | ✓ | ✓ | ✓ | — |
| `CROSS4` 4갈래 | ✓ | ✓ | ✓ | ✓ | ✓ | — |
| `CORNER2` 2면 코너 | — | — | — | — | — | ✓ |
| `CORNER3` 3면 코너 | — | — | — | — | — | ✓ (1종±거울) |

**형상(=`seamType`) 카탈로그 = 5종.** 면 자세까지 펼친 레시피 라이브러리는 3(면 위 형상=LINE/CROSS3/CROSS4)×5(면 자세) + 2(코너 형상=CORNER2/CORNER3) = **17종**(코너 거울 L/R 분리 시 확대). ACS는 레시피 개수를 알 필요가 없다 — `seamType`+`wall_code`만 보낸다.

> **3면 코너(`CORNER3`)가 각도상 1종인 근거**: 마구리(A·F) 도면 실측 결과 팔각 단면 8개 꼭짓점 내각이 **전부 135°**(챔퍼 전부 45° 등각). 따라서 16개 삼면 코너의 각도 구성이 모두 **(135°·90°·90°)로 동일** → 각도상 한 종류(+거울). 자세한 검증은 §7. `CORNER2`(2면 코너)는 면 쌍(이면각)별로 형상이 갈릴 수 있어 별도 확인 대상(§9).

---

## 5. 레시피 카탈로그 (표준안) — `seamType` × 면 자세

액션은 `startWeldInspection` 하나 유지. ACS는 `params.seamType`(5값) + `drawingPos.wall_code`(면 자세 판정 키)를 보내고, HD_AMR이 아래 표대로 **레시피 id**를 유도해 실행한다.

> **명칭 정합**: 아래 레시피 id는 HD_AMR 온보드 레시피 라이브러리의 키이며, VDA 계약의 `params.inspectionProfileId`([VDA5050_INTERFACE_SPEC](VDA5050_INTERFACE_SPEC.md) §8.5)와는 **다른 축**이다(inspectionProfileId=촬영/측정 프리셋). 계약에 싣는 방식(현행 유지 / 신규 필드)은 **N13 협의 확정 대기** — 레시피 유도는 HD_AMR 도메인이다.

| `seamType` \ 면자세 | 바닥 `B` | 천장 `T` | 수직벽 `SM`·`PM`·`F`·`A` | 하부챔퍼 `SL`·`PL` | 상부챔퍼 `SU`·`PU` |
|---|---|---|---|---|---|
| `LINE` | `LINE-FLOOR` | `LINE-CEIL` | `LINE-WALL` | `LINE-CHMR-LO` | `LINE-CHMR-UP` |
| `CROSS3` | `CROSS3-FLOOR` | `CROSS3-CEIL` | `CROSS3-WALL` | `CROSS3-CHMR-LO` | `CROSS3-CHMR-UP` |
| `CROSS4` | `CROSS4-FLOOR` | `CROSS4-CEIL` | `CROSS4-WALL` | `CROSS4-CHMR-LO` | `CROSS4-CHMR-UP` |

| `seamType` | 레시피 id | 비고 |
|-----------|-----------|------|
| `CORNER2` | `CORNER2` | 2면 코너 — 이면각별 세분·거울은 `wall_code`(면 쌍)로 판별(선택) |
| `CORNER3` | `CORNER3` | 3면 코너 — 각도 균일(§7)이라 면 자세 무관 단일. 거울 시 `CORNER3-L`/`CORNER3-R` |

→ 면 위 형상 3종 × 면 자세 5 = 15 + 코너 2종 = **17개 레시피 id**(코너 거울 분리 시 확대). ACS는 이 개수와 무관하게 `seamType`+`wall_code`만 전달한다.

---

## 6. 액션 파라미터 매핑 (`startWeldInspection`)

| 파라미터 | 출처 | 검사 타입과의 관계 |
|----------|------|--------------------|
| `seamType` | §3 카탈로그(5값) | **형상 선택자** — `LINE`/`CROSS3`/`CROSS4`/`CORNER2`/`CORNER3`. 꺾인 직선은 세그먼트별 `LINE`으로 분할 |
| `wall_code` | 검사 영역 소속 면 | **면 자세 판정 키** — `seamType`과 조합해 레시피 유도(§5) |
| `inspectionProfileId` | 촬영/측정 프리셋 | 형상 선택자와 다른 축(§5 명칭 정합) |
| `seamStartW` / `seamEndW` | 도면(u,v)→T_W_D 변환 | 용접선 기하 = **툴 방향(수직/수평)을 AMR이 유도하는 근거** |
| `drawingPos(u,v)` | 도면 좌표 echo | 위치 참조 |
| `sectionDxfId` | 면별 DXF(§8) | 상세 형상 참조 |
| `standoff` / 정차각 | `ref.wall` 법선 자동 산출 | 면 자세별 접근 |
| `anchorGroupId` | `{tank}-L{n}-{wall}-{영역}` | 앵커 공유 판정 |

**핵심**: 툴 수직/수평은 파라미터로 넘기지 않는다 — `seamStartW→EndW` 방향으로 AMR이 결정.

---

## 7. 3면 코너 균일성 근거

- 근거 도면: `drawing/2D도면/WALL A.dxf`, `WALL F.dxf` (마구리, 단면=팔각).
- `KC-2B Steel Wall` 외곽 8정점 내각 = **전부 135°** (양쪽 동일), 챔퍼 전부 45° 등각.
- 결론: 16개 삼면 코너의 각도 구성이 전부 (135°·90°·90°) → **각도상 1종(+거울)**.
- ⚠️ 한계: "각도 동일"은 필요조건. 실제 코너 **용접선 실형상**(코로게이션 정렬·시트 이음 위치)까지 동일한지는 **KC-2B 표준 코너 부재 사양** 또는 실선/점선 분리 후 코너별 비교로 확정해야 한다.

---

## 8. 근거 도면·참고

- 면별 도면: `drawing/2D도면/WALL {B,SL,PL,SM,PM,SU,PU,T,F,A}.dxf` — 용접선 정본(KC-2B).
- 주요 레이어: `KC-2B Membrane Sheet(UM)`(용접선 후보 + 코로게이션), `KC-2B Steel Wall`(면 경계).
- 관련 문서: [VDA5050_INTERFACE_SPEC](VDA5050_INTERFACE_SPEC.md) §4.4(정차점)·§8.1(seamType)·**§8.5(타입 카탈로그)·§8.5.1(`seamType`×`wall_code`→레시피 매핑)**, [TANK_WALL_LAYOUT](TANK_WALL_LAYOUT.md), [INSPECTION_SCENARIO](INSPECTION_SCENARIO.md).

---

## 9. 미결·결정 필요 항목

1. **종/횡 방향을 레시피에 넣을지** — 넣으면 레시피 수 배가. 현재 권장: **넣지 않음**(AMR이 seam 기하로 유도).
2. **용접선 확정 정의** — "실선 = 용접선, 360mm 점선 = 코로게이션 top" 규칙을 데이터(레이어/선종) 필터로 확정.
3. **`CORNER2`(2면 코너) 세분** — 이면각(면 쌍)별로 형상이 갈리면 `wall_code`로 판별(CORNER3처럼 단일화 가능한지 실측 확인).
4. **코너 거울(L/R) 분리 여부** — AMR 접근이 좌우 대칭이면 단일 유지, 아니면 `-L`/`-R` 분리.
5. **레시피 라이브러리 구현** — HD_AMR이 `(seamType, wall_code)` → 레시피 id 매핑·실행(2차 연동, [HD_AMR_INSPECTION_RECIPE_INTEGRATION](HD_AMR_INSPECTION_RECIPE_INTEGRATION.md)).

> **완료(2026-09-15)**: `seamType` enum을 5값(`LINE`·`CROSS3`·`CROSS4`·`CORNER2`·`CORNER3`)으로 확장 —
> `param_schema`(schema.sql·ActionCatalogSeed·마이그레이션 3곳)·등록 게이트·계획 UI 드롭다운 반영.
> 이후 결정이 내려지면 본 문서와 `param_schema`, 관련 코드/DB를 함께 갱신한다.
