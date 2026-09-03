# HD_ACS 솔루션

## 프로젝트 구성
| 프로젝트 | 역할 |
|---|---|
| HD.Acs.Core | 도메인 (미션 상태머신 Stateless, MapGraph/Dijkstra, 계획 모델) |
| HD.Acs.Data | EF Core 엔티티/DbContext (ref/run/hist/alarm/sys 스키마), GraphLoader |
| HD.Acs.Vda5050 | VDA 5050 메시지 모델, 마스터 MQTT 클라이언트, OrderBuilder |
| HD.Acs.App | ASP.NET Core 호스트 — REST API + SignalR + VDA 브릿지 (단일 프로세스 [ADR-011]) |
| HD.Acs.Simulator | VDA 5050 로봇(HD_AMR) 시뮬레이터 |
| HD.Acs.UI.Core | UI 공용 코어(net8.0, 프레임워크 중립) — Models·REST/SignalR 서비스·ViewModels·2D 투영 |
| HD.Acs.UI.Core.Tests | UI.Core xUnit 테스트 (상태색 골든·투영·SignalR 디스패처) |
| HD.Acs.UI | WPF 운영 앱 헤드 (Windows 전용 — 뷰·Telerik·Helix 3D·WPF 어댑터만, UI.Core 참조) |
| HD.Acs.UI.Desktop | **Avalonia 크로스플랫폼 운영 앱 헤드**(Win/macOS/Linux, net8.0) — 전 뷰 + 3D(소프트웨어 투영) 이식 완료. `dotnet run --project HD.Acs.UI.Desktop`, 배포는 `tools/publish_desktop.sh <rid>`(mac .app) |
| HD.Acs.UI.Desktop.Tests | Avalonia 헤드리스 스모크(XAML 로드·모드 전환·DataGrid·바인딩 경로) — GUI 없이 실행 |

## 실행 (개발 환경)
```bash
# 1. 인프라: PostgreSQL + Mosquitto 기동, db/schema.sql 적용
# 2. 서버
dotnet run --project HD.Acs.App          # :5199 (5100은 NAMUGA 계열 제품과 충돌 회피)
# 3. 시뮬레이터
dotnet run --project HD.Acs.Simulator -- localhost HHI AMR-01 CT1-L1
# 4. (Windows) UI
dotnet run --project HD.Acs.UI
```
※ HD.Acs.UI는 net8.0-windows(WPF) — Linux/Mac 빌드 시 솔루션에서 제외하거나 개별 프로젝트만 빌드. HD.Acs.UI.Core(+Tests)는 net8.0이라 어디서나 빌드·테스트 가능.
