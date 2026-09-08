---
project: HD_ACS
type: feature
status: prototype
updated: 2026-09-07
tags:
  - hd-acs
  - llm-wiki
---

# DXF 영역 분할

독립 분석 도구로 영역과 작업을 산출하는 단계입니다. Desktop에 가져오기 버튼을 만들거나 서버에 일괄 등록하는 기능은 아직 연결하지 않았습니다.

## 입력과 처리

입력은 `drawing/2D도면/WALL *.dxf` 10개입니다.

1. ASCII DXF의 ENTITIES에서 LINE과 직선 LWPOLYLINE을 읽습니다.
2. Membrane Sheet 레이어의 같은 직선 위 구간을 합쳐 용접선 후보로 사용합니다.
3. Steel Wall의 CENTER 선과 교차하는 지점을 top 후보로 사용합니다.
4. 실제 교차점이 360 간격으로 3개 이어지는 720mm 작업을 만듭니다.
5. 800×1600mm 후보 창 중 아직 배정되지 않은 작업을 가장 많이 포함하는 창부터 선택합니다.
6. 포함 관계와 단일 배정 관계를 나눠 출력합니다.

탐욕적 선택이므로 전역적으로 Area 개수가 최소이거나 작업 밀도가 최적인 것은 보장하지 않습니다. 마지막 1피치 잔여 구간은 완전한 작업으로 만들지 않습니다.

## 코드와 한계

- `tools/analyze_dxf_regions.py`: 원시 엔티티 및 레이어·간격 확인.
- `tools/build_dxf_area_preview.py`: 작업 추출, 후보 영역 선택, 산출물 생성.
- `tools/dxf_area_preview.html`: 상세도·좌표·매칭 템플릿.

현재는 용접선 후보 판별, 면 방향, 경사면 실거리, 외곽·개구부와 로봇 리치가 최종 확정되지 않았습니다. 결과는 [[HD_ACS - 좌표 산출 결과]]에 정리합니다.

근거: [[HD_ACS - 결정 001 검사영역 분할]] · [[HD_ACS - 근거 목록]]
