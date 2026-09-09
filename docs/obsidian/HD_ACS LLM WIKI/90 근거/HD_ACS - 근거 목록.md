---
project: HD_ACS
type: sources
status: snapshot
updated: 2026-09-08
tags:
  - hd-acs
  - llm-wiki
---

# 근거 목록

[[HD_ACS - 캘리브레이션 작업 절차서]]: 2026-09-08 GenerateWalls, DrawingTransform, calibration API, TankViewModel 캐시 코드를 재확인. HEAD 39f5f1d 및 작업 트리 기준이며 이전 snapshot의 hash나 운영 DB 값을 갱신한 것은 아닙니다.

## 2026-09-08 추가 근거

이 대화의 사용자 요청, 수정 코드와 빌드 출력에 근거합니다. 이전 snapshot과 SHA256 목록은 그대로 두며 이번 수정의 hash로 간주하지 않습니다.

- 캘리브레이션: `CalibrationViewModel.cs`, Desktop/WPF `CalibrationView`, `IAcsApiClient.cs`, `AcsApiClient.cs`, App `Program.cs`.
- 수동 이동: `TankViewModel.cs`, Desktop `TankView.axaml`, `Tank3DControl.cs`.
- 검증: 캘리브레이션 Desktop/App 빌드 성공; 수동 이동 Desktop 빌드 성공(NU1900 경고 1). 실장비 명령 미실행.
- 상세: [[HD_ACS - 2026-09-08 개발일지]] · [[HD_ACS - Desktop UI]].

이 위키는 아래 로컬 자료와 이번 사용자 대화를 바탕으로 작성했습니다. 인터넷 조사나 전 시스템 재검증은 수행하지 않았습니다.

## 저장소 기준

기준 HEAD: `d72ce7ef6d663a6fb54ec27b48885c2452ed4d12`. 작업 트리에는 미커밋 UI 변경·DXF·산출 도구가 존재했습니다. 따라서 HEAD만으로 이번 산출물을 재현할 수는 없습니다.

## 원문 snapshot

- [[HD_ACS - 프로젝트 개요 원문]] — `docs/PROJECT_OVERVIEW.md`
- [[HD_ACS - 개발 가이드 원문]] — `docs/DEVELOPMENT_GUIDE.md`
- [[HD_ACS - 영역 작업 사양 원문]] — `docs/SPEC_AREA_TASK_MANUAL.md`
- [[HD_ACS - UI 전환 검토 원문]] — `docs/UI_CROSS_PLATFORM_REVIEW.md`
- [[HD_ACS - 벽면 정의 원문]] — `docs/TANK_WALL_LAYOUT.md`
- [[HD_ACS - AMR 계약 원문]] — `docs/VDA5050_INTERFACE_SPEC.md`

## 코드와 산출물

- [[HD_ACS - 코드 관찰 snapshot]] — 프로젝트 정의, UI, 등록 경로, 기하, DXF 분석 도구.
- [[HD_ACS - Area 좌표 전체 목록]] — 실제 생성된 Area별 P1~P4·작업 ID.
- [전체 좌표 JSON](../91%20첨부/HD_ACS-area-task-coordinates.json) — 작업 시작/중간/끝과 출처 DXF handle.
- [상세도 HTML](../91%20첨부/HD_ACS-area-task-preview.html) — 검토용 시각화.

원시 DXF와 실행파일은 이 위키에 복제하지 않았습니다. DXF 원본은 저장소 `drawing/2D도면`에 있습니다.

## 대화 근거

이번 대화에서 사용자는 Area를 여러 칸을 묶은 검사 구역으로 정의하고 가로 800·세로 1600 기준을 제시했습니다. 이어 양축 top–top–top 연결과 Area 겹침을 허용했고, 확인 질문에 **360mm 피치**, **가로·세로 용접선 모두 포함**을 선택했습니다. 이는 대화 요약이며 외부 회의록을 가장하지 않습니다.

원래 400mm 피치와 현재 코드 이전의 MinWidth=100은 [[HD_ACS - 문서 충돌 기록]]에 분리했습니다.

자료별 SHA256은 각 snapshot과 첨부 `HD_ACS-source-manifest.json`에 기록합니다.
