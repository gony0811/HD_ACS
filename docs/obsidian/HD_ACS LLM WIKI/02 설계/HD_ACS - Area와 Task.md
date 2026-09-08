---
project: HD_ACS
type: domain
status: mixed
updated: 2026-09-07
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

## 이번에 합의한 도면 분할 기준

- Area 기준 크기: **800 × 1600mm**.
- top 피치: **360mm**.
- 작업 1개: **top–top–top = 720mm** 직선 구간.
- 가로·세로 용접선 모두 포함합니다.
- 연속 작업은 끝 top을 공유합니다: T1–T2–T3 다음 T3–T4–T5.
- Area는 겹쳐도 됩니다. 검토용 도구는 같은 작업 ID를 한 Area에만 배정합니다.

## 포함과 배정

`included_task_ids`는 Area 안에 완전히 들어오는 작업입니다. 겹친 Area에 같은 ID가 나타날 수 있습니다.
`assigned_task_ids`는 실제 수행을 위해 한 Area에 소속시킨 고유 작업입니다. 이번 산출물에서는 모든 작업이 정확히 한 번 배정됐습니다.

미리보기 ID는 `SM-A0001`, `SM-T00169` 같은 라벨입니다. 서버의 GUID나 운영 중인 작업 ID가 아닙니다.

관련: [[HD_ACS - 결정 001 검사영역 분할]] · [[HD_ACS - 좌표 산출 결과]] · [[HD_ACS - 근거 목록]]
