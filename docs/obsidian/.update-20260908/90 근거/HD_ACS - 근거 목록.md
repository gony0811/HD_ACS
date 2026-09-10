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

이 위키는 아래 로컬 자료와 이번 사용자 대화를 바탕으로 작성했습니다. 인터넷 조사나 전 시스템 재검증은 수행하지 않았습니다.


## 2026-09-08 추가 근거

- [[HD_ACS - 2026-09-08 현장 메모 원문]] — 사용자가 이 대화에 제공한 치수·좌표·결과·예정·현장 스케치.
- [[HD_ACS - 2026-09-08 현장평가]] — 완료 보고와 예정 사항을 분리한 요약.
- [[HD_ACS - L2 도면 SLAM 캘리브레이션]] — 기준점 대응표와 축 규약 확인 사항.

현장 완료·±120mm 결과는 사용자 보고이며 별도 재시험 결과가 아닙니다. 아래의 저장소 HEAD와 원문 snapshot은 9/7 당시 기준을 유지합니다.

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
