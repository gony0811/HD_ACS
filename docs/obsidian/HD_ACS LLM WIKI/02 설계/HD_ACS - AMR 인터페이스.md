---
project: HD_ACS
type: contract
status: documented
updated: 2026-09-07
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

관련: [[HD_ACS - 시스템 구조]] · [[HD_ACS - Area와 Task]] · [[HD_ACS - 근거 목록]]
