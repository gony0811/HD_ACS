"""Create a self-contained Obsidian wiki in repository staging for review/copy."""
from pathlib import Path
from datetime import date
import hashlib
import json
import re
import shutil
import subprocess

ROOT=Path(__file__).resolve().parents[1]
OUT=ROOT/'docs'/'obsidian'/'HD_ACS LLM WIKI'
TODAY='2026-09-07'
pages={}

def note(folder,title,kind,status,body):
    name='HD_ACS - '+title
    pages[name]=(folder,kind,status,body.strip())

note('', '홈','index','active', '''
# HD_ACS 개발 위키

LNG 화물창 용접검사로봇 관제 프로젝트의 현재 지식과 결정, 진행 상황을 연결한 위키입니다. **기준일: 2026-09-07.** 저장소 코드·문서와 이번 개발 대화를 근거로 초기 구성했습니다.

## 지금 어디까지 왔는가

- 운영 UI는 `HD.Acs.UI.Desktop`(Avalonia)이며 공용 로직은 `HD.Acs.UI.Core`에 있습니다.
- DXF 6개 면에서 **검토용 Area 1,522개 / 고유 작업 2,864개**를 산출했습니다. 실제 자동등록은 미구현입니다.
- 경사면 4개는 좌표 변환 미확정입니다. 외곽·개구부·로봇 도달 범위도 별도 검증이 필요합니다.
- 현재 코드의 좌표 입력창 최소 너비는 **150**입니다. 처음 변경한 100은 이전 상태입니다.

## 시작점

| 알아볼 내용 | 문서 |
|---|---|
| 프로젝트 목적과 책임 | [[HD_ACS - 프로젝트 개요]] |
| 현재 구현 상태 | [[HD_ACS - 현재 상태]] |
| 시스템과 통신 | [[HD_ACS - 시스템 구조]] · [[HD_ACS - AMR 인터페이스]] |
| 좌표와 도면 | [[HD_ACS - 좌표계와 10개 면]] |
| 영역과 작업 | [[HD_ACS - Area와 Task]] · [[HD_ACS - DXF 영역 분할]] |
| 계산 결과 | [[HD_ACS - 좌표 산출 결과]] |
| 결정 근거 | [[HD_ACS - 결정 목록]] |
| 다음 작업 | [[HD_ACS - 다음 작업]] · [[HD_ACS - 미해결 질문]] |
| 개발 이력 | [[HD_ACS - 2026-09-07 개발일지]] |
| 자료 출처 | [[HD_ACS - 근거 목록]] |
| LLM으로 유지하는 방법 | [[HD_ACS - 위키 운영 규칙]] |

## 이 위키의 범위

기존 일기와 별도로 유지하는 프로젝트 지식 공간입니다. Obsidian 기본 Markdown·위키 링크·속성만 사용합니다. 별도 플러그인이나 자동 갱신은 설정하지 않았습니다.

[[HD_ACS - 용어집]] · [[HD_ACS - 문서 충돌 기록]]
''')
note('01 프로젝트','프로젝트 개요','project','documented','''
# 프로젝트 개요

HD_ACS는 LNG 화물창의 용접검사 시나리오·영역·작업을 관리하고, HD_AMR에 이동 및 검사 명령을 전달하는 관제 시스템입니다.

## 책임

- HD_ACS: 검사 계획, 영역·작업 관리, 정차점·정차각 계산, 배차, 상태 대조, 기록 및 운영 UI.
- HD_AMR: AMR 주행, 협동로봇 자세·검사 시퀀스, 검사장비 제어.
- 이미지 전달 경로와 판정은 별도 검사 시스템의 책임이며 ACS가 하드웨어를 직접 제어하지 않습니다.

통신 상대와 계약은 [[HD_ACS - AMR 인터페이스]]가 정리합니다. 오래된 프로젝트 개요의 ROS2/REST 후보 설명을 현재 계약으로 읽지 않습니다.

## 개발 범위의 읽는 법

기존 개발 가이드는 PHASE 1(온보드 검사 시퀀스)을 완료, PHASE 2(ACS 관제)를 진행 중으로 기록합니다. 이 위키 작성 중 실제 로봇이나 전체 시스템을 재검증한 것은 아닙니다.

현재 확인한 코드·산출물의 범위는 [[HD_ACS - 현재 상태]], 향후 작업은 [[HD_ACS - 다음 작업]]을 봅니다.

근거: [[HD_ACS - 근거 목록]]의 `PROJECT_OVERVIEW.md`, `VDA5050_INTERFACE_SPEC.md`, `DEVELOPMENT_GUIDE.md`.
''')
note('01 프로젝트','시스템 구조','architecture','code-observed','''
# 시스템 구조

| 저장소 프로젝트 | 역할 |
|---|---|
| HD.Acs.UI.Desktop | Avalonia 데스크톱 UI, 뷰와 플랫폼 어댑터 |
| HD.Acs.UI.Core | 공용 ViewModel, DTO, API 클라이언트, 렌더링 로직 |
| HD.Acs.UI | 기존 WPF 헤드; 저장소에 존재하며 처분 결정은 별도 |
| HD.Acs.App | ACS 서버 애플리케이션 |
| HD.Acs.Core | 핵심 도메인·계획·기하 로직 |
| HD.Acs.Data | 데이터 접근 계층 |
| HD.Acs.Vda5050 | VDA 5050 통신 계층 |
| HD.Acs.Simulator / HD.Acs.SimTest | 시뮬레이터와 시나리오 검증 프로젝트 |

Desktop 프로젝트는 `net8.0`, Avalonia 주요 패키지 `11.3.20`을 사용합니다. UI Core를 참조하며 ViewModel의 영역 등록은 API 클라이언트로 이어집니다.

```mermaid
flowchart LR
  UI[Desktop UI] --> Shared[UI Core]
  Shared -->|REST / SignalR| ACS[ACS 서버]
  ACS --> DB[데이터 계층]
  ACS <-->|VDA 5050 / MQTT| AMR[HD_AMR]
```

이 구조도는 구성 관계를 요약하며 실제 서버 실행 여부를 뜻하지 않습니다.

관련: [[HD_ACS - Desktop UI]] · [[HD_ACS - AMR 인터페이스]] · [[HD_ACS - 근거 목록]]
''')
note('01 프로젝트','현재 상태','status','as-of-2026-09-07','''
# 현재 상태

## 이번에 확인한 상태

| 항목 | 상태 | 확인 범위 |
|---|---|---|
| Desktop 입력창 크기 | 코드 반영 | 현재 P1~P4 u/v, x/y/θ에 MinWidth=150; 최초 100 수정 이후 상태 |
| 수동 Area·Task 등록 경로 | 코드 존재 | ViewModel → AcsApiClient 호출 확인; 이번에 서버 등록 실행 안 함 |
| DXF 10개 입력 | 파일 확인 | drawing/2D도면 |
| DXF 분석·Area 배치 | 검토용 구현 | 독립 Python 도구, UI/API 미연결 |
| 6개 면 좌표·매칭 | 산출·정합 확인 | 1,522 Area, 2,864 고유 작업 |
| 경사면 4개 | 미확정 | 세로 도면 간격 약 254.6의 실거리 변환 필요 |
| 외곽·개구부·로봇 리치 | 미검증 | Area 경계상자 검증과 구분 |
| 전체 회귀·실장비 검증 | 이번 작업 미수행 | 과거 문서의 통과 기록과 구분 |

## 과거 기록과 현재 관찰

기존 문서는 Avalonia 이식, 3D 렌더러, macOS 패키징 완료를 기록합니다. 이번 위키 작성은 해당 시험을 재수행하지 않았습니다. 과거 E2E 통과 기록도 같은 원칙으로 읽습니다.

UI 최초 100 수정은 별도 출력 폴더 빌드에 성공했습니다. 이후 관찰한 150 수정에 대해 같은 빌드 결과를 적용하지 않습니다.

이 위키 생성 시 저장소에 미커밋 변경이 존재했습니다. 위키나 산출물 생성은 커밋·배포 완료를 의미하지 않습니다.

관련: [[HD_ACS - 2026-09-07 개발일지]] · [[HD_ACS - 문서 충돌 기록]] · [[HD_ACS - 다음 작업]]
''')
note('02 설계','AMR 인터페이스','contract','documented','''
# AMR 인터페이스

저장소의 `docs/VDA5050_INTERFACE_SPEC.md`가 인터페이스 계약의 기준 문서입니다. 확인한 버전은 **1.2**, 최종 개정일은 **2026-09-03**, 기반 표준은 **VDA 5050 2.0**입니다. 이는 프로젝트 문서의 계약 상태이며 외부 표준 최신판 조사 결과가 아닙니다.

- ACS의 로봇측 상대는 HD_AMR 하나이며 VDA 5050 over MQTT를 사용합니다.
- 계획과 배차는 ACS, 주행·검사 자세·장비 실행은 HD_AMR의 책임입니다.
- 주요 채널은 order, instantActions, state, connection입니다.
- factsheet는 사양서상 예약 채널입니다.
- 상세 계약과 실제 구현 차이는 원문 각주를 확인합니다.

Area와 Task 자동생성도 이 책임 경계를 유지해야 합니다. 도면에서 작업 좌표를 만들었다고 협동로봇의 자세나 도달 가능성을 확정한 것은 아닙니다.

관련: [[HD_ACS - 시스템 구조]] · [[HD_ACS - Area와 Task]] · [[HD_ACS - 근거 목록]]
''')
note('02 설계','좌표계와 10개 면','geometry','documented','''
# 좌표계와 10개 면

## 세 종류의 좌표를 분리한다

1. **DXF 원시 좌표**: 도면에 저장된 x/y. 파일의 단위 설정은 미지정입니다.
2. **검토용 좌표**: 멤브레인 도형 경계상자 좌하단을 (0,0)으로 평행이동하고 mm로 가정한 u/v.
3. **ACS 면 좌표**: 면의 P0, U, V로 정의한 로컬 좌표. 단위 m이며 검토용 좌표와 바로 동일시하지 않습니다.

검토용 원시 복원식: `DXF 좌표 = 검토용 좌표 + 해당 면 origin_dxf`.

ACS 전역 도면 좌표는 코드·사양상 바닥 중심 기준, x=길이, y=폭(+좌현), z=상방입니다. `P = P0 + u·U + v·V`로 면 좌표를 전역 위치로 바꿉니다. 맵 보정 `T_W_D`는 별도 단계입니다.

| 코드 | 면 |
|---|---|
| A / F | 선미 / 선수 격벽 |
| B / T | 바닥 / 천장 |
| SM / PM | 우현 / 좌현 수직벽 |
| SL / PL | 우현 / 좌현 하부 경사면 |
| SU / PU | 우현 / 좌현 상부 경사면 |

수동 등록 UI는 선택 층의 로컬 v에 VOff를 더해 면 전체 v로 저장합니다. 층의 도달 범위도 별도로 검사해야 합니다.

경사면 4개의 도면 세로 피치는 약 254.6입니다. **360으로 바꾸는 배율이나 투영 방식을 임의로 확정하지 않았습니다.** A면 U 방향의 문서 불일치도 원문 사양에 남아 있습니다.

관련: [[HD_ACS - 미해결 질문]] · [[HD_ACS - DXF 영역 분할]] · [[HD_ACS - 근거 목록]]
''')
note('02 설계','Area와 Task','domain','mixed','''
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
''')
note('03 기능','Desktop UI','feature','code-observed','''
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
''')
note('03 기능','DXF 영역 분할','feature','prototype','''
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
''')
data=json.loads((ROOT/'drawing/area-preview/area-task-coordinates.json').read_text(encoding='utf-8'))
table='\n'.join('| '+w['wall']+' | '+(' | '.join(str(w['counts'][k]) for k in ('areas','H','V','tasks')) if 'counts' in w else '미산출 | — | — | —')+' |' for w in data['walls'])
note('03 기능','좌표 산출 결과','result','review-only',f'''
# 좌표 산출 결과

기준: Area 800×1600mm / top 360mm / Task 720mm / 가로·세로 모두 포함.

| 면 | Area | 가로 작업 | 세로 작업 | 고유 작업 |
|---|---:|---:|---:|---:|
{table}

**합계: 6개 면, Area 1,522개, 고유 작업 2,864개.** 경사면 4개는 합계에 포함하지 않았습니다.

| Area당 배정 작업 | Area 수 |
|---:|---:|
| 1 | 568 |
| 2 | 647 |
| 3 | 226 |
| 4 | 81 |

## 예시

`SM-A0001`: P1=(0,0), P2=(800,0), P3=(800,1600), P4=(0,1600)mm.
포함 4개, 배정 4개(세로 4개): SM-T00169, SM-T00170, SM-T00177, SM-T00178.

`SM-A0002`는 포함 3개이지만 실제 배정은 1개입니다. 겹친 영역의 작업을 중복 실행하지 않기 때문입니다.

## 확인한 내용

- 모든 작업 ID가 정확히 한 Area에 배정됩니다.
- 작업의 시작·중간 top·끝이 배정 Area 안에 있습니다.
- 작업 길이는 허용오차 내 720mm이며 Area 크기는 800×1600mm입니다.
- HTML JavaScript 구문 검사는 통과했습니다. 로컬 파일 브라우저 정책으로 화면 렌더링 검증은 못 했습니다.

검토용 좌표의 원점은 [[HD_ACS - 좌표계와 10개 면]]을 따릅니다. 실제 ACS 등록 좌표·로봇 실행 검증 결과가 아닙니다.

전체 표: [[HD_ACS - Area 좌표 전체 목록]]

[좌표 및 매칭 JSON](../91%20첨부/HD_ACS-area-task-coordinates.json) · [상세도 HTML](../91%20첨부/HD_ACS-area-task-preview.html)

관련: [[HD_ACS - DXF 영역 분할]] · [[HD_ACS - 미해결 질문]] · [[HD_ACS - 근거 목록]]
''')
note('04 결정','결정 목록','index','active','''
# 결정 목록

| 결정 | 상태 | 요지 |
|---|---|---|
| [[HD_ACS - 결정 001 검사영역 분할]] | 사용자 확인 | 360 피치, 720 작업, 800×1600 Area, 양방향, 겹침 허용 |
| [[HD_ACS - 결정 002 검토 좌표와 운영 등록 분리]] | 현재 구현 범위 | 미리보기와 운영 등록을 구분 |
| [[HD_ACS - 결정 003 위키 운영 방식]] | 초기 운영안 | 주제·결정·일지·근거를 연결 |

기존 시스템 ADR 전체를 재승인하거나 이 목록으로 대체하지 않습니다. 원본 `docs/ARCHITECTURE_DECISIONS.md` 및 인터페이스 사양의 결정은 [[HD_ACS - 근거 목록]]에서 추적합니다.

관련: [[HD_ACS - 홈]] · [[HD_ACS - 문서 충돌 기록]]
''')
note('04 결정','결정 001 검사영역 분할','decision','user-confirmed','''
# 결정 001 검사영역 분할

날짜: 2026-09-07. 근거: 이번 개발 대화의 사용자 지시.

## 배경

시트 한 칸씩이 아니라 여러 칸을 묶은 로봇 검사 구역을 만들고, 연속된 top–top–top 작업을 최대한 포함해야 합니다.

## 확인된 요구

- 기준 Area 크기 800×1600mm.
- top 피치 360mm로 계산; 따라서 작업 길이는 720mm.
- 가로·세로 용접선 모두를 계산.
- 이웃 작업은 마지막 top을 공유하며 연결.
- Area 겹침 허용.

## 초기 가정의 정정

이전 설명의 400mm 피치는 도면 확인 전 가정이었습니다. DXF에서 반복 간격을 확인한 뒤 사용자가 360mm 기준을 선택했습니다. **400mm는 현재 계산 기준이 아닙니다.**

## 구현 선택과 미확정

단일 Area에 작업을 배정해 중복 실행을 방지하고, 미배정 작업을 많이 담는 후보를 우선 선택한 것은 현재 도구의 구현 선택입니다. 전역 최적화를 확정한 요구가 아닙니다. 경사면 254.6 좌표를 실거리로 바꾸는 방법은 이번 합의에 포함되지 않습니다.

관련: [[HD_ACS - Area와 Task]] · [[HD_ACS - DXF 영역 분할]] · [[HD_ACS - 근거 목록]]
''')
note('04 결정','결정 002 검토 좌표와 운영 등록 분리','decision','implementation-scope','''
# 결정 002 검토 좌표와 운영 등록 분리

현재 요청은 영역 분할 좌표와 Area별 작업 매칭을 검토하는 것입니다. 독립 도구의 계산 결과는 운영 API에 전달하지 않았습니다.

## 분리한 이유

- DXF 단위 metadata 미지정, ACS 원점·방향과의 변환 미완료.
- 경사면 4개의 세로 실거리 미확정.
- 외곽·개구부·층·로봇 도달 범위 미검증.
- 용접선 후보에 세부 형상선이 포함될 수 있음.

이는 영구적인 자동등록 금지 결정이 아닙니다. [[HD_ACS - 다음 작업]]의 검증을 완료한 뒤 UI 미리보기·일괄 등록 경로를 구현할 수 있습니다.

관련: [[HD_ACS - 좌표 산출 결과]] · [[HD_ACS - 미해결 질문]]
''')
note('04 결정','결정 003 위키 운영 방식','decision','initial-proposal','''
# 결정 003 위키 운영 방식

사용자 의도: iCloud의 일기용 Obsidian 보관함에 새 폴더를 만들어 HD_ACS 개발을 LLM WIKI 방식으로 정리.

## 초기 구성

- 주제 문서는 현재 이해를 설명합니다.
- 결정 문서는 무엇을 왜 선택했는지와 대체된 가정을 보존합니다.
- 개발일지는 날짜별 작업·검증 결과를 남깁니다.
- 근거에는 원문 snapshot, 소스 경로, hash, 기준 커밋을 둡니다.
- 미해결 질문은 확정 사실과 분리합니다.

문서 이름에 `HD_ACS -` 접두어를 붙여 일기 보관함 안에서 링크 이름 충돌을 줄입니다. 기존 일기나 Obsidian 설정을 수정하는 구성은 아닙니다.

이 구조는 초기 운영안이며, 개인 보관함의 기존 규칙에 맞춰 이후 조정할 수 있습니다. 자동 실행이나 자동 동기화 봇은 설정하지 않았습니다.

관련: [[HD_ACS - 위키 운영 규칙]] · [[HD_ACS - 홈]]
''')
note('05 진행','2026-09-07 개발일지','daily','recorded','''
# 2026-09-07 개발일지

## 작업과 결과

1. P1~P4 u/v, x/y/θ 입력 최소 너비 요청을 반영해 최초 100으로 변경. 별도 출력 빌드 성공.
2. 위키 작성 시 코드 재확인: 현재 150, θ 별도 행. 이 후속 수정의 작성자·검증은 미확인.
3. drawing/2D도면의 면별 DXF 10개를 조사. LINE/LWPOLYLINE과 Membrane Sheet·Steel Wall 레이어 확인.
4. 사용자와 top 360mm, 작업 720mm, 양방향 용접선, Area 800×1600mm·겹침 허용 기준 확인.
5. 검토용 분석·배치 도구와 좌표 목록 생성. 6개 면, 1,522 Area, 2,864 고유 작업.
6. 중복 배정 없음, 작업 길이·영역 포함 관계 검증. HTML 구문 검증 완료, 브라우저 화면 검증 제한.
7. 프로젝트 위키 초안 구성. iCloud 설치·동기화 완료 여부는 배치 기록에서 별도로 확인.

## 남은 것

- 경사면 좌표 변환, 용접선 확정, 외곽·개구부·로봇 도달 범위.
- ACS 좌표계로의 변환 및 UI/API 일괄 등록.
- 과거 문서와 최신 코드의 차이 정리.

관련: [[HD_ACS - Desktop UI]] · [[HD_ACS - 결정 001 검사영역 분할]] · [[HD_ACS - 좌표 산출 결과]] · [[HD_ACS - 다음 작업]] · [[HD_ACS - 위키 배치 기록]]
''')
note('05 진행','다음 작업','backlog','proposed','''
# 다음 작업

아래 목록은 이번 위키의 제안 우선순위입니다. 완료 여부를 현재 산출물과 혼동하지 않습니다.

## 자동등록 전 필요한 작업

- [ ] 경사면 PL/SL/PU/SU의 DXF→면 실거리 변환 기준 확인.
- [ ] 면별 도면 원점·축 방향·단위와 ACS P0/U/V 정합 확인.
- [ ] 실제 용접선 레이어/선종 확정 및 상세 형상선 제외.
- [ ] 팔각 외곽과 개구부를 넘는 Area·Task 처리 규칙 확정.
- [ ] 층 도달 밴드, 로봇 리치, 정차점 검증.
- [ ] 잔여 1피치와 검사 불가 구간의 표시·처리 규칙 결정.

## 제품 연결

- [ ] Desktop DXF 가져오기 및 Area/Task 미리보기 UI.
- [ ] 검토 결과를 기존 등록 API로 변환하는 어댑터.
- [ ] 재실행 중복 방지, 실패 복구, 기존 등록 Area와의 충돌 처리.
- [ ] 대표 면 기준 수동 정답 좌표와 자동 결과 비교.
- [ ] DB 포함 등록 검증과 별도 실장비 검증.

## 문서 유지

- [ ] [[HD_ACS - 문서 충돌 기록]]의 오래된 설명을 원본 문서와 함께 정리.
- [ ] 현재 150 입력창 변경을 빌드·화면에서 확인.
- [ ] 완료 작업은 주제 문서와 결정·개발일지에 함께 반영.

관련: [[HD_ACS - 현재 상태]] · [[HD_ACS - 미해결 질문]]
''')
note('06 질문','미해결 질문','questions','open','''
# 미해결 질문

| 질문 | 필요한 근거 | 영향 |
|---|---|---|
| 경사면 세로 254.6은 어떤 투영인가? | 도면 정의/치수 또는 담당자 확인 | 4개 면 작업 수와 좌표 |
| 각 면 원점과 축은 ACS P0/U/V와 일치하는가? | 기준점 매칭 | 실제 등록 위치 |
| CENTER 선이 전부 검사 top인가? | 도면 레이어 규칙 | 작업 분할 정확도 |
| Membrane Sheet 직선 중 실제 용접선은 무엇인가? | 검사 대상 선종 정의 | 잘못된 작업 제외 |
| 외곽/개구부에서 Area를 허용할 조건은? | 작업 가능 영역 정의 | 부분 영역·안전한 정차 |
| 1피치 잔여 구간은 별도 작업인가? | 검사 프로세스 요구 | 미검사 구간 관리 |
| 800×1600은 고정 영역 크기인가 최대 리치인가? | 실제 작업영역 계약 | 최적화·경계 처리 |
| A면 U축 방향의 문서 차이는 어떻게 정리하는가? | 대외 정본과 코드 대조 | 면 방향 변환 |

사용자가 360mm 계산을 확인했지만 모든 원시 CENTER 선의 물리적 의미까지 검증한 것은 아닙니다. 상태가 바뀌면 결론과 근거를 해당 주제에 반영하고 이 질문의 해소 이력을 남깁니다.

관련: [[HD_ACS - 좌표계와 10개 면]] · [[HD_ACS - 다음 작업]] · [[HD_ACS - 결정 목록]]
''')
note('06 질문','문서 충돌 기록','conflicts','open','''
# 문서 충돌 기록

오래된 설명을 삭제해 이력을 지우지 않고 현재 기준과 구분합니다.

| 충돌 | 현재 읽는 기준 | 후속 조치 |
|---|---|---|
| PROJECT_OVERVIEW의 ROS2/REST 후보 vs VDA 확정 | 인터페이스 사양 v1.2의 단일 HD_AMR·VDA/MQTT 계약 | 원문 개요 최신화 |
| DEVELOPMENT_GUIDE의 WPF UI·3D 골격 설명 vs Avalonia 완료 기록 | 최신 Desktop 코드와 UI 크로스플랫폼 검토서 | 과거 상태 날짜 명시 |
| 자동 SeamSlicer 보류 vs 이번 DXF 분석 | 기존 운영 경로는 수동; 이번 도구는 검토용 prototype | 통합 시 새 결정 필요 |
| 대화 초기 400 피치 vs 사용자 확인 360 | 360 | 400은 폐기된 가정으로 보존 |
| 대화 초기 MinWidth=100 vs 현재 코드=150 | 현재 코드 150 | 검증 결과도 버전별 구분 |
| A면 U 방향의 원문 차이 | 임의 통일하지 않음 | 기준 문서 대조 필요 |

위키의 요약이 원본 계약을 대신하지 않습니다. 사실·계약·사용자 요구가 충돌하면 해당 종류의 근거를 나눠 기록합니다.

관련: [[HD_ACS - 근거 목록]] · [[HD_ACS - 현재 상태]] · [[HD_ACS - 미해결 질문]]
''')
note('01 프로젝트','용어집','glossary','active','''
# 용어집

| 용어 | 이 위키에서의 의미 |
|---|---|
| HD_ACS | 검사 계획·배차·상태 관제 |
| HD_AMR | 주행·협동로봇·검사장비를 통합 제어하는 상대 시스템 |
| Area | 여러 검사작업을 묶는 사각형 영역 |
| Task | 용접선의 한 검사 구간; 이번 분할은 top–top–top |
| top | 계산에 사용한 코로게이션 기준 위치 |
| pitch | 이웃 top 간격, 이번 계산 360mm |
| included | 영역 안에 포함 가능; 여러 Area와 중복 관계 가능 |
| assigned | 실제 배정; 현재 미리보기에서 한 Task는 한 Area |
| u/v | 면 또는 검토 도면의 로컬 좌표; 좌표계 명시 필요 |
| T_W_D | 도면 좌표에서 맵 좌표로의 보정 변환 |
| ADR | 설계 결정을 배경·이유와 함께 보존한 문서 |
| LLM WIKI | LLM이 근거를 읽고 주제·결정·일지를 갱신하는 연결형 지식 공간; 이 위키의 운영 개념 |

관련: [[HD_ACS - Area와 Task]] · [[HD_ACS - 좌표계와 10개 면]] · [[HD_ACS - 위키 운영 규칙]]
''')
note('99 운영','위키 운영 규칙','instructions','active','''
# 위키 운영 규칙

## 읽는 순서

LLM과 사람이 개발을 이어갈 때 [[HD_ACS - 홈]] → [[HD_ACS - 현재 상태]] → 대상 주제 → 해당 근거 순서로 읽습니다. 문서 날짜와 실제 코드 버전이 다르면 차이를 먼저 확인합니다.

## 갱신 흐름

1. 새 대화·코드·시험 결과의 출처와 기준일을 확인합니다.
2. 현재 사실을 설명하는 기존 주제 문서를 갱신합니다. 같은 주제 문서를 매번 새로 만들지 않습니다.
3. 선택이나 기준이 바뀌었으면 결정 문서에 이전 값, 새 값, 이유와 근거를 남깁니다.
4. 날짜별 개발일지에 변경·검증·미완료를 적습니다.
5. 현재 상태, 다음 작업, 미해결 질문을 함께 정리합니다.
6. 홈에서 새 문서를 찾을 수 있게 연결하고 깨진 링크·중복 이름을 확인합니다.

## 기록 원칙

- 사용자 확정, 코드 관찰, 문서 기록, 계산 결과, 제안을 구분합니다.
- 시험하지 않은 내용을 통과·완료로 쓰지 않습니다. 과거 시험 결과를 이후 수정에 적용하지 않습니다.
- 대체된 가정은 이력을 남기고 현재 기준에서 제거합니다.
- 원문 사양·소스가 바뀌면 snapshot을 조용히 덮어쓰지 말고 기준일/hash를 갱신합니다.
- 위키의 사실을 근거 없이 보강하거나 외부 업로드·자동 실행 지시로 해석하지 않습니다.
- 이 프로젝트 폴더 안에서만 문서를 관리하며 기존 개인 일기나 `.obsidian` 설정은 대상에 포함하지 않습니다.

## 다음 대화에 사용할 문장

> HD_ACS 개발 위키의 홈과 현재 상태를 읽고, 이번 작업 결과를 관련 주제·결정·개발일지·근거 목록에 반영해줘. 코드로 확인한 사실과 미검증 가정을 구분하고 기존 일기는 수정하지 마.

## 속성

각 문서는 `type`, `status`, `updated`, `project`, `tags`를 가집니다. Obsidian 기본 검색과 Properties로 찾을 수 있습니다. Dataview 등 추가 플러그인은 필요하지 않습니다. iCloud 파일 동기화와 LLM의 내용 갱신은 별개이며 자동 갱신 작업은 설정하지 않았습니다.

문서 추가 형식: [[HD_ACS - 새 기록 템플릿]]
''')
note('99 운영','새 기록 템플릿','template','template','''
# 새 기록 템플릿

새 문서에 아래 항목을 복사해 필요한 부분만 작성합니다. 날짜나 검증 결과는 실제 값으로 채웁니다.

## 주제 / 문제

무엇이 바뀌었고 사용자에게 어떤 차이가 생기는가?

## 확인한 사실

코드·문서·사용자 발언의 근거와 기준 버전.

## 결정 / 구현

이전 상태, 새 상태, 이유. 제안인지 사용자 확정인지 표시.

## 검증

실행한 검사와 결과, 하지 않은 검사.

## 미해결 / 다음 작업

- [ ] 근거가 더 필요한 항목

## 연결

[[HD_ACS - 홈]] · [[HD_ACS - 현재 상태]] · [[HD_ACS - 결정 목록]] · [[HD_ACS - 근거 목록]]
''')
note('99 운영','위키 배치 기록','deployment','staged','''
# 위키 배치 기록

위키 원본은 HD_ACS 저장소의 `docs/obsidian/HD_ACS LLM WIKI`에 준비했습니다.

현재 기록 상태: **iCloud 보관함 경로 확인 대기 / 복사 전**.

보관함 경로가 확인되면 새 하위 폴더로 복사하고 파일 수·해시·내부 링크를 확인합니다. iCloud 서버 또는 다른 기기로의 동기화 완료는 로컬 복사 성공과 구분합니다.

관련: [[HD_ACS - 홈]] · [[HD_ACS - 2026-09-07 개발일지]]
''')

sources=[
 ('프로젝트 개요 원문','docs/PROJECT_OVERVIEW.md'),
 ('개발 가이드 원문','docs/DEVELOPMENT_GUIDE.md'),
 ('영역 작업 사양 원문','docs/SPEC_AREA_TASK_MANUAL.md'),
 ('UI 전환 검토 원문','docs/UI_CROSS_PLATFORM_REVIEW.md'),
 ('벽면 정의 원문','docs/TANK_WALL_LAYOUT.md'),
 ('AMR 계약 원문','docs/VDA5050_INTERFACE_SPEC.md'),
]
manifest=[]
for label,rel in sources:
    raw=(ROOT/rel).read_bytes()
    sha=hashlib.sha256(raw).hexdigest()
    manifest.append({'path':rel,'sha256':sha})
    note('90 근거/원문',label,'source-snapshot','snapshot',f'# {label}\n\n기준일: {TODAY}. 저장소 원문: `{rel}`. SHA256: `{sha}`.\n\n이하 내용은 원문 snapshot이며 작성 당시 상태가 포함됩니다. 현재 상태는 [[HD_ACS - 현재 상태]]와 대조합니다.\n\n---\n\n'+raw.decode('utf-8-sig'))

codepaths=['src/HD.Acs.UI.Desktop/HD.Acs.UI.Desktop.csproj','src/HD.Acs.UI.Desktop/Views/AreaManagementView.axaml','src/HD.Acs.UI.Core/ViewModels/AreaPlanningViewModel.cs','src/HD.Acs.UI.Core/Services/AcsApiClient.cs','src/HD.Acs.Core/Planning/TankGeometry.cs','tools/analyze_dxf_regions.py','tools/build_dxf_area_preview.py']
codebody=['# 코드 관찰 snapshot','','이 snapshot은 현재 코드의 근거를 휴대할 수 있도록 보존합니다. 이후 수정은 자동 반영되지 않습니다. [[HD_ACS - 현재 상태]]와 함께 읽습니다.']
for rel in codepaths:
    raw=(ROOT/rel).read_bytes(); sha=hashlib.sha256(raw).hexdigest()
    manifest.append({'path':rel,'sha256':sha})
    text=raw.decode('utf-8-sig')
    if rel.endswith('AreaPlanningViewModel.cs'): text='\n'.join(text.splitlines()[148:217])
    if rel.endswith('AcsApiClient.cs'): text='\n'.join(text.splitlines()[248:297])
    if rel.endswith('TankGeometry.cs'): text='\n'.join(text.splitlines()[:50])
    codebody += ['',f'## {rel}',f'전체 파일 SHA256: `{sha}`. 아래는 전체 또는 관련 발췌입니다.','','```'+('python' if rel.endswith('.py') else 'xml' if rel.endswith(('.axaml','.csproj')) else 'csharp'),text.rstrip(),'```']
note('90 근거','코드 관찰 snapshot','source-snapshot','snapshot','\n'.join(codebody))
githead=subprocess.check_output(['git','rev-parse','HEAD'],cwd=ROOT,text=True).strip()
sourcebody='''# 근거 목록

이 위키는 아래 로컬 자료와 이번 사용자 대화를 바탕으로 작성했습니다. 인터넷 조사나 전 시스템 재검증은 수행하지 않았습니다.

## 저장소 기준

'''+f'기준 HEAD: `{githead}`. 작업 트리에는 미커밋 UI 변경·DXF·산출 도구가 존재했습니다. 따라서 HEAD만으로 이번 산출물을 재현할 수는 없습니다.\n\n'+'''## 원문 snapshot

'''+ '\n'.join(f'- [[HD_ACS - {label}]] — `{rel}`' for label,rel in sources)+'''

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
'''
note('90 근거','근거 목록','sources','snapshot',sourcebody)

OUT.mkdir(parents=True,exist_ok=True)
for name,(folder,kind,status,body) in pages.items():
    path=OUT/folder/(name+'.md'); path.parent.mkdir(parents=True,exist_ok=True)
    path.write_text(f'---\nproject: HD_ACS\ntype: {kind}\nstatus: {status}\nupdated: {TODAY}\ntags:\n  - hd-acs\n  - llm-wiki\n---\n\n'+body+'\n',encoding='utf-8')
attachment=OUT/'91 첨부'; attachment.mkdir(exist_ok=True)
for src,dst in [('area-task-coordinates.json','HD_ACS-area-task-coordinates.json'),('area-task-preview.html','HD_ACS-area-task-preview.html')]:
    shutil.copyfile(ROOT/'drawing/area-preview'/src,attachment/dst)
    manifest.append({'path':'drawing/area-preview/'+src,'sha256':hashlib.sha256((attachment/dst).read_bytes()).hexdigest()})
coords=ROOT/'drawing/area-preview/area-coordinates.md'
(attachment/'HD_ACS - Area 좌표 전체 목록.md').write_text('---\nproject: HD_ACS\ntype: generated-data\nstatus: review-only\nupdated: '+TODAY+'\n---\n\n[[HD_ACS - 좌표 산출 결과]] · [[HD_ACS - 홈]]\n\n'+coords.read_text(encoding='utf-8'),encoding='utf-8')
(attachment/'HD_ACS-source-manifest.json').write_text(json.dumps({'as_of':TODAY,'git_head':githead,'sources':manifest},ensure_ascii=False,indent=2),encoding='utf-8')

names={p.stem for p in OUT.rglob('*.md')}
broken=[]
for p in OUT.rglob('*.md'):
    for target in re.findall(r'\[\[([^\]|#]+)',p.read_text(encoding='utf-8')):
        if target.startswith('HD_ACS - ') and target not in names: broken.append((str(p),target))
assert not broken,broken
assert len(names)==len(list(OUT.rglob('*.md'))),'duplicate note names'
print(json.dumps({'folder':str(OUT),'markdown_notes':len(names),'total_files':len(list(p for p in OUT.rglob('*') if p.is_file())),'broken_project_links':len(broken)},ensure_ascii=False))
