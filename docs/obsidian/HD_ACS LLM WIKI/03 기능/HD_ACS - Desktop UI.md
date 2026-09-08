---
project: HD_ACS
type: feature
status: code-observed
updated: 2026-09-07
tags:
  - hd-acs
  - llm-wiki
---

# Desktop UI

## 좌표 입력창 개선

사용자 요청은 P1~P4 u/v 및 x/y/θ NumericUpDown의 최소 너비를 100 이상으로 늘리는 것이었습니다. 대화 초기에 11개 컨트롤에 MinWidth=100을 추가했습니다.

위키 작성 시점의 파일을 다시 읽은 결과, **현재는 MinWidth=150**이며 x/y와 θ가 별도 행으로 나뉘어 있습니다. 정차 이격도 150입니다. 이 후속 변경의 작성자나 작업 시점은 확인하지 않았습니다.

파일: `src/HD.Acs.UI.Desktop/Views/AreaManagementView.axaml`.

## 검증 이력

- 최초 빌드는 기존 산출물 쓰기 권한 문제로 실패했습니다.
- 권한을 높인 기본 출력 빌드는 실행 중인 앱의 DLL 잠금으로 실패했습니다.
- 별도 출력 폴더 빌드는 성공했습니다. NU1900 취약성 정보 조회 경고 2건이 있었습니다.
- 위 성공은 **100 수정 시점**의 결과입니다. 현재 150 상태를 다시 빌드했다는 뜻이 아닙니다.

관련: [[HD_ACS - 현재 상태]] · [[HD_ACS - 2026-09-07 개발일지]] · [[HD_ACS - 근거 목록]]
