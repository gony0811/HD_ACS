---
project: HD_ACS
type: contract
status: documented
updated: 2026-09-15
tags:
  - hd-acs
  - llm-wiki
---

# AMR 인터페이스

저장소의 `docs/VDA5050_INTERFACE_SPEC.md`가 인터페이스 계약의 기준 문서입니다. 확인한 버전은 **1.2**, 최종 개정일은 **2026-09-03**, 기반 표준은 **VDA 5050 2.0**입니다. 이는 프로젝트 문서의 계약 상태이며 외부 표준 최신판 조사 결과가 아닙니다.

- ACS의 로봇측 상대는 HD_AMR 하나이며 VDA 5050 over MQTT를 사용합니다.
- 계획과 배차는 ACS, 주행·검사 자세·장비 실행은 HD_AMR의 책임입니다.
- 주요 채널은 order, instantActions, state, connection입니다.
- factsheet는 사양서상 예약 채널입니다.
- 상세 계약과 실제 구현 차이는 원문 각주를 확인합니다.

Area와 Task 자동생성도 이 책임 경계를 유지해야 합니다. 도면에서 작업 좌표를 만들었다고 협동로봇의 자세나 도달 가능성을 확정한 것은 아닙니다.

## startWeldInspection 의 drawingPos.wall_code

`startWeldInspection` 액션의 `position.drawingPos.wall_code`에 실제로 발행되는 값은 **`TankGeometry`가 자동 생성한 10개 면 코드 중 하나**입니다.

- 값 집합: `B`(바닥) · `SL`·`PL`(하부챔퍼 우/좌현) · `SM`·`PM`(수직벽 우/좌현) · `SU`·`PU`(상부챔퍼 우/좌현) · `T`(천장) · `F`(선수 마구리) · `A`(선미 마구리).
- 출처: 검사 작업이 등록된 **검사 영역의 소속 면 코드**(`ref.inspection_area.wall_code`, FK→`ref.wall`)를 발행 시 그대로 echo합니다. 자유 문자열이 아니며 DB FK로 이 10종에 강제됩니다.
- 발행 경로: `InspectionDispatcher`가 `area.WallCode`를 `WeldInspectionPayload.BuildPosition`에 넘겨 `drawingPos.wall_code`에 씁니다. `param_schema`는 문자열로만 검증(enum 미강제)하지만 값 도메인은 DB가 보장합니다.
- 역할: `wall_code`는 **면(surface) 식별자 = HD_AMR의 티칭 자세 선택 키**입니다. 용접선 **형상**은 별도 파라미터 `seamType`(`LINE`·`CROSS`·`CORNER`)로 전달됩니다 — 둘은 다른 축입니다.
- 사양서 근거: `docs/VDA5050_INTERFACE_SPEC.md` §8.1·§8.2·§8.4(골든 예시)·§8.5.1(2) (개정 1.3b에서 값 도메인 명문화).

관련: [[HD_ACS - 시스템 구조]] · [[HD_ACS - Area와 Task]] · [[HD_ACS - 좌표계와 10개 면]] · [[HD_ACS - 근거 목록]]
