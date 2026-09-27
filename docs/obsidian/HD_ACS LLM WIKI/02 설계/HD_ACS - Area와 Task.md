---
project: HD_ACS
type: domain
status: mixed
updated: 2026-09-27
tags:
  - hd-acs
  - llm-wiki
---

# Area와 Task

**Area는 여러 검사작업을 묶는 영역이고, Task는 한 용접선의 검사 구간입니다.**

## 기존 코드

- Area: P1~P4 사각형, 면 코드, 이름, 선택적 정차 pose를 등록합니다.
- Task: Area ID에 시작점·끝점, seamType, sectionDxfId, profileId를 연결합니다.
- 클라이언트는 `POST /api/areas`, `POST /api/areas/{areaId}/tasks`를 사용합니다.
- UI에서 v에 층 offset을 더하는 기존 경로가 있습니다.

## 도면 분할 기준 (2026-09-27 개정)

- Area 최대 크기: **1440 × 1440mm**, 층마다 새로 배치(한 층 범위 안).
- top 피치: **360mm** (경사면 경사 방향은 실제 254.6mm).
- 작업 1개: **top–top 1칸** 직선 구간. 가로·세로 모두.
- 교차점: CROSS3·2갈래에 닿는 작업은 제거, CROSS4는 교차점당 가로 작업 1개만 `CROSS4`.
- 상세·산출 결과: [[HD_ACS - 결정 001 검사영역 분할]]. 등록용 파일 생성: `tools/build_hdacs_area_tasks.py`.

> 아래 "포함과 배정"은 09-07 검토용 도구(800×1600 겹침 허용) 설명이다. 09-27 도구는 창이 겹치지 않아 포함 = 배정이다.

## 포함과 배정

`included_task_ids`는 Area 안에 완전히 들어오는 작업입니다. 겹친 Area에 같은 ID가 나타날 수 있습니다.
`assigned_task_ids`는 실제 수행을 위해 한 Area에 소속시킨 고유 작업입니다. 이번 산출물에서는 모든 작업이 정확히 한 번 배정됐습니다.

미리보기 ID는 `SM-A0001`, `SM-T00169` 같은 라벨입니다. 서버의 GUID나 운영 중인 작업 ID가 아닙니다.

관련: [[HD_ACS - 결정 001 검사영역 분할]] · [[HD_ACS - 좌표 산출 결과]] · [[HD_ACS - 근거 목록]]
