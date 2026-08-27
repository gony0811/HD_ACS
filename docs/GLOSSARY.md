# 용어 정의 (Glossary)

| 용어 | 정의 |
|---|---|
| **HD_ACS** | HD현대중공업 LNG 화물창 용접검사로봇 관제 시스템 (본 프로젝트) |
| **LNG 화물창 (Cargo Containment System, CCS)** | LNG 운반선의 화물(액화천연가스) 저장 공간. 멤브레인 타입은 주름형 스테인리스 시트 용접 구조 |
| **HD_AMR** | 로봇 온보드의 통합 운영 S/W — AMR 주행, 협동로봇 검사 시퀀스/자세, 검사장비 제어를 모두 담당. HD_ACS의 유일한 통신 상대 (VDA 5050) |
| **VDA 5050** | AGV/AMR ↔ 마스터 컨트롤 간 표준 통신 인터페이스 (MQTT + JSON). 본 프로젝트는 v2.0 채택 — order/instantActions/state/connection 채널 사용. 계약 상세는 `VDA5050_INTERFACE_SPEC.md` |
| **T_W_D** | 도면(Drawing) 좌표 → 맵/월드(World) 좌표 강체변환 — 층별 기준점 캡처로 산출, payload의 seamStartW/EndW 생성에 사용 |
| **drawingPos** | 검사 액션 payload에 echo되는 도면 좌표 정보(tank/level/wall_code/u/v/x/y/z) — 결과 역추적·티칭 키(wall_code) 용도, 주행에는 미사용 |
| **멤브레인 (Membrane)** | 화물창 내벽의 얇은 주름형(corrugated) 스테인리스 시트 — 검사 대상 용접부가 위치하며, 바닥 요철의 원인 |
| **AMR** | Autonomous Mobile Robot. 자율 이동 로봇 — 본 프로젝트에서는 현대 AMR 플랫폼 |
| **협동로봇 (Cobot)** | AMR 상단에 장착되어 검사 자세를 만드는 다관절 협동 로봇 |
| **검사 시나리오 (Scenario)** | 검사 지점 목록 + 지점별 검사 작업 + 실행 순서 + 운영 정책의 정의 단위 |
| **검사 지점 (Inspection Point / POI)** | AMR이 정차하여 검사를 수행하는 하나의 위치 |
| **검사 작업 (Inspection Task)** | HD_AMR에 정의된 검사 작업을 참조하는 ACS의 최소 지시 단위 — 실행 세부(자세·장비 제어)는 HD_AMR 소관 |
| **미션 (Mission)** | 시나리오의 1회 실행 인스턴스. 실행 이력과 결과 데이터가 귀속됨 |
| **웨이포인트 (Waypoint)** | 협동로봇 검사 자세를 정의하는 지점 — HD_AMR 내부 개념으로 ACS 데이터 모델에는 존재하지 않음 |
| **디스패치 (Dispatch)** | 관제가 미션을 하위 시스템 명령으로 분해하여 전달하는 행위 |
| **용접 비드 (Weld Bead)** | 용접으로 형성된 금속 덧살 — 주요 검사 대상 |
| **용접 심 (Weld Seam)** | 용접 이음부 라인 |
| **추적 가능성 (Traceability)** | 검사 데이터를 시나리오/미션/지점/시각/장비설정으로 역추적할 수 있는 성질 |
