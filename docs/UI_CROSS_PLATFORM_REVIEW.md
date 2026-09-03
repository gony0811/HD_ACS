# HD.Acs.UI macOS 대응 검토 (UI 크로스플랫폼 검토서)

## 0. 배경

> 작성일 2026-09-03 · 상태 **검토(제안)** — ADR-005 개정 여부는 미결. 본 문서는 결정 자료이며 코드 변경을 포함하지 않는다.

HD.Acs.UI는 WPF(`net8.0-windows`) + Telerik UI for WPF(Fluent Dark) + HelixToolkit.Wpf로 구현돼 Windows에서만 실행된다.
요구사항: **동일한 프로그램 구조(MVVM·Generic Host DI·REST/SignalR 계약 계층·운영/계획/이력 모드 셸)를 유지하면서 macOS에서도 운영** 가능하게 할 방법을 검토한다.
백엔드(HD.Acs.App, ASP.NET Core)는 현장 서버에 그대로 두고 UI 클라이언트만 mac에서 뜨면 되며, UI는 ADR-005의 API-First 원칙상 REST+SignalR만 사용하므로 서버 측 변경은 없다.

## 1. 현황 진단 — WPF 결합 범위 (≈5,800줄 중)

조사 결과 WPF/Telerik/Helix 결합은 **좁게 집중**돼 있고, 계약·상태 계층은 이미 프레임워크 중립이다.

| 계층 | 파일 | 결합 여부 | 비고 |
|---|---|---|---|
| Models | `Models/Dtos.cs`, `Models/ProjectDoc.cs` | 없음 | using 0개, 순수 DTO |
| Services | `AcsApiClient.cs`, `ProjectService.cs`, `TankLayout.cs`, `AcsOptions.cs`, 인터페이스 4종 | 없음 | HttpClient / System.Text.Json / GZip |
| Services | `MonitoringClient.cs` | **Dispatcher**만 (L15, L32) | SignalR 자체는 중립 |
| Services | `ProjectDialogService.cs` | **Microsoft.Win32** 파일 대화상자, `Application.Current.MainWindow` | 이미 `IProjectDialogService`로 추상화됨 |
| ViewModels 7/10 | Mission·RobotStatus·Alarms·Calibration·ManualZoneChange·Slicing·AppMode | 없음 | CommunityToolkit.Mvvm만 사용 |
| ViewModels | `TankViewModel.cs` | `System.Windows.Media.Color`(StatusColors/WeldLineColor L64~79), `PointCollection`/`Point`(FacePlot L82, L204~207) | 렌더 좌표를 WPF 타입으로 노출 |
| ViewModels | `AreaPlanningViewModel.cs` | `PointCollection`/`Point`(AreaPoly L15, FaceOutline L71~73, Project() L297~383) | 위와 동일 패턴 |
| ViewModels | `ShellViewModel.cs` | `MessageBox.Show` 4곳(L135, L183, L198, L211) | 확인/오류 대화상자 |
| Views XAML 13개 | Telerik `Rad*` 168곳 | 전부 | NumericUpDown 40 · GridView 39 · Button 35 · TabItem 16 · ComboBox 15 · Menu 9 · ToggleButton 5 · TabControl 6 · BusyIndicator 2 · ProgressBar 1. **RadDocking은 더 이상 미사용**(모드 탭 셸로 대체됨) |
| Views 2D 전개도 | `AreaLayoutView.xaml`, `TankView.xaml` 전개도 탭, `SlicingView.xaml` | 플레인 WPF Canvas/Polygon/Line | Telerik·Helix 무관 → 이식 용이 |
| Views 3D | `TankView.xaml`(HelixViewport3D) + `TankView.xaml.cs` 400줄 | **HelixToolkit + Media3D 전부** | 셸 10면 메시·층 밴드·영역/용접선 오버레이·빌보드 라벨·로봇 구·바닥 격자 히트테스트(수동 이동 클릭) |
| 부트스트랩 | `App.xaml.cs` | Telerik FluentPalette Dark + Generic Host | Host/DI 부분은 중립 |
| 기타 | `Infrastructure/Converters.cs`, `Themes/Brushes.xaml` | WPF Visibility·Brush | 소규모 |

Windows 전용 API(레지스트리·WinForms·P/Invoke·백슬래시 경로)는 **없다**.
UI 프레임워크 결정은 ADR-005/Q5′("3D 요구 우선, WPF 확정")에 기록돼 있고, 참조 플랫폼 NAMUGA_ACS의 UI는 이미 **Avalonia 11 + CommunityToolkit.Mvvm**이다(`docs/REFERENCE_NAMUGA_ACS.md`).

## 2. 대안 비교

| 안 | 구조 유지 | mac 지원 | Telerik | 3D | 평가 |
|---|---|---|---|---|---|
| **A. Avalonia UI 11** | ◎ XAML+MVVM+DI 그대로, VM/Service 재사용 | ◎ Win/mac/Linux 네이티브, .NET 8 | ✕ Avalonia 라인 없음 → 내장 컨트롤/OSS/타사(DevExpress·Actipro) 치환 | △ 3D 라이브러리 부재 → 자체 구현 필요 | **권장**. NAMUGA_ACS Avalonia 자산과도 정렬 |
| B. .NET MAUI | △ XAML 방언 상이, 데스크톱 컨트롤 빈약(DataGrid 없음) | △ Mac Catalyst(iPad 앱 계열), 데스크톱 UX 제약 | ✕ | ✕ | 비권장 — 관제 데스크톱용 부적합 |
| C. Uno Platform | ○ WinUI XAML(WPF에 가까움) | ○ Skia 렌더 | ✕(Telerik WinUI는 Windows 전용) | ✕ | Avalonia 대비 데스크톱 성숙도·커뮤니티 열세 |
| D. Web(Blazor/React)+Three.js | ✕ 별도 스택(구조 재작성) | ◎ 브라우저면 어디서나 | 해당 없음 | ◎ Three.js | ADR-005 "보조 UI=Web 대시보드" 경로. 주 운영 UI를 대체하려면 전면 재작성 — **별도 결정 사항**(태블릿 요구와 묶어 검토) |
| E. 가상화(Parallels/VM, Wine) | ◎ 무변경 | △ Windows 라이선스·성능·운영 부담 | ○ | ○ | 코드 변경 없는 **임시 우회책**. 정식 운영 답이 아님 |

결론: "동일 구조로 mac 운영"이라는 요구에 부합하는 것은 **A안**뿐이다. B/C는 구조는 유지돼도 컨트롤·3D 문제가 A와 같거나 더 크고, D는 구조를 버린다.

## 3. 권장안 — Avalonia 11 + 공용 코어 분리(Shared Core, Two Heads → One Head)

### 3.1 목표 구조

```
src/
├── HD.Acs.UI.Core/          net8.0 (신규) — 프레임워크 중립
│   ├── Models/              Dtos.cs, ProjectDoc.cs (이동)
│   ├── Services/            AcsApiClient, MonitoringClient(디스패처 추상화), ProjectService, TankLayout, AcsOptions
│   ├── Abstractions/        IUiDispatcher, IDialogService(확인·오류·파일 열기/저장)
│   ├── ViewModels/          10개 전부 (WPF 타입 → 중립 타입)
│   └── Rendering/           순수 기하: 전개도 투영(FaceClipPolygon·HalfWidthU·Proj), 3D 카메라·투영·페인터 정렬
├── HD.Acs.UI/               net8.0-windows — 기존 WPF 헤드 (뷰·Telerik·Helix·WPF 어댑터만 남김)
└── HD.Acs.UI.Avalonia/      net8.0 — Avalonia 헤드 (Win/mac/Linux)
```

- **Phase 0에서 WPF 헤드는 동작을 유지**한 채 코어만 빼낸다(안전한 리팩터, 현장 운영 무영향).
- Avalonia 헤드가 기능 동등성에 도달하면 **WPF 헤드 은퇴를 권장**(Telerik 라이선스·피드 자격증명·이중 유지보수 제거, 단일 코드베이스). 은퇴 시점은 사용자 결정.

### 3.2 코어 추출 시 WPF 타입 치환 규칙

| 현행 | 코어 타입 | 헤드 어댑터 |
|---|---|---|
| `PointCollection`, `System.Windows.Point` (TankViewModel.FacePlot, AreaPlanningViewModel.AreaPoly/FaceOutline/ActiveBand) | `IReadOnlyList<Pt2>` (`readonly record struct Pt2(double X, double Y)`) | WPF: `Pt2[]→PointCollection` 컨버터 / Avalonia: `→Avalonia.Points` 컨버터 |
| `System.Windows.Media.Color` (StatusColors, WeldLineColor) | `readonly record struct Rgba(byte A,R,G,B)` | 각 헤드 `Rgba→Brush` 컨버터(기존 `WorkStatusToBrushConverter` 자리) |
| `MessageBox.Show` (ShellViewModel) | `IDialogService.ConfirmAsync/ErrorAsync` | WPF: MessageBox / Avalonia: 커스텀 Window 또는 MessageBox.Avalonia |
| `Microsoft.Win32` 대화상자 (ProjectDialogService) | 기존 `IProjectDialogService` 유지 | Avalonia: `TopLevel.StorageProvider.OpenFilePickerAsync/SaveFilePickerAsync` |
| `Dispatcher` (MonitoringClient) | `IUiDispatcher.Post(Action)` 주입 | WPF: `Application.Current.Dispatcher` / Avalonia: `Dispatcher.UIThread` |
| `Visibility` 컨버터 | Avalonia는 `IsVisible`(bool) → `EnumToBooleanConverter`만 재사용 | — |

### 3.3 Telerik → Avalonia 컨트롤 대응표

| Telerik (사용 수) | Avalonia | 비고 |
|---|---|---|
| RadNumericUpDown (40) | `NumericUpDown` 내장 | 폼 입력 대부분 |
| RadGridView (39) | `Avalonia.Controls.DataGrid` (공식 패키지) | 컬럼·정렬·**RowDetailsTemplate/RowDetailsVisibilityMode** 지원 → 작업 현황 드릴다운 유지 가능 |
| RadButton / RadToggleButton (40) | `Button` / `ToggleButton` | 모드 탭은 ToggleButton 스타일 |
| RadTabControl / RadTabItem (22) | `TabControl` / `TabItem` | |
| RadComboBox (15) | `ComboBox` | |
| RadMenu / RadMenuItem (9) | `Menu`/`MenuItem` + mac은 `NativeMenu`(시스템 메뉴바) | 파일 메뉴 4항목 |
| RadBusyIndicator (2) / RadProgressBar (1) | `ProgressBar IsIndeterminate` 오버레이 / `ProgressBar` | |
| FluentPalette Dark | `FluentTheme` + `RequestedThemeVariant="Dark"` | `Themes/Brushes.xaml` → `.axaml` ResourceDictionary 그대로 |
| Canvas/Polygon/Polyline/Line (전개도) | 동명 컨트롤 존재 | `Points` 타입만 `Avalonia.Points`(List<Point>) |
| `pack://…;component/Assets` | `avares://HD.Acs.UI.Avalonia/Assets/…` | 로고 PNG 8개 |
| `KeyBinding Escape` | `KeyBindings` | 픽 모드 해제 |

상용 대안: DevExpress Avalonia(DataGrid 등)·Actipro Avalonia(도킹·테마)가 존재하나, 현행 RadGridView 사용은 표시·RowDetails 수준이라 **내장 DataGrid로 충분**하다고 판단. 상용은 내장 부족 시 후보로만 둔다(라이선스 확인 필요).

### 3.4 3D 뷰 전략 — 소프트웨어 투영 렌더러(권장)

Avalonia에는 HelixToolkit 등가물이 없다. 후보 3종:

| 방식 | 장점 | 단점 |
|---|---|---|
| **① 소프트웨어 투영(권장)** — 코어 `Rendering/`에 카메라(오빗·줌·ZoomExtents)·원근 투영·면 깊이 정렬(페인터), 헤드는 `Control.Render(DrawingContext)`에 2D 도형으로 그림 | 네이티브 의존 0, Win/mac/Linux 동일, 반투명·빌보드 텍스트·히트테스트(광선-평면 해석해)가 2D라서 오히려 단순, 기존 전개도 캔버스와 같은 패턴 | 조명은 면 법선·광원 내적으로 플랫 셰이딩(Helix 수준 음영은 아님). 수천 면 이상이면 부적합 |
| ② `OpenGlControlBase` + Silk.NET 자체 렌더러 | GPU, 실제 3D 파이프라인 | 셰이더·텍스트 아틀라스·카메라 전부 자작(코드량 ↑), macOS OpenGL은 deprecated(현재 동작은 함) |
| ③ WebView + Three.js | 3D 완성도 최고 | WebView 컴포넌트(Avalonia Accelerate 상용 또는 CefGlue) 무게·폐쇄망 배포 부담, MVVM 경계 붕괴 |

현행 씬은 **셸 10면 + 팔각 모서리 와이어 + 층 밴드 + 영역 폴리곤/용접선 + 라벨 + 로봇 구 + 바닥 격자(클릭 히트)** 로 수십 개 도형 규모라 ①이 충분하고 위험이 가장 낮다. ①로 가되 `Rendering/`을 순수 기하로 두면 나중에 ②로 교체할 때 카메라·투영 코드는 재사용된다.
겸사로 `docs/TANK_RENDERING.md`에 기록된 3중복(`TankView.HalfWidth`·`TankViewModel.FaceOutlineUv`·`AreaPlanningViewModel.HalfWidthU`)을 코어 `Rendering/`으로 단일화한다.

## 4. 단계별 이행(안)

### Phase 0 — 공용 코어 추출 (WPF 무변경 동작)
1. `src/HD.Acs.UI.Core/HD.Acs.UI.Core.csproj`(net8.0) 신설: CommunityToolkit.Mvvm 8.3.2 · SignalR.Client 8.0.8 · Extensions.Http/Options 참조. `HD.Acs.sln`에 추가(겸사 `HD.Acs.SimTest` 미등재 확인).
2. `Models/`, `Services/`(ProjectDialogService 제외) 이동. `MonitoringClient`의 Dispatcher를 `IUiDispatcher`로 치환.
3. `Abstractions/IUiDispatcher.cs`, `IDialogService.cs` 신설. `ShellViewModel` MessageBox 4곳 → `IDialogService`.
4. `TankViewModel`·`AreaPlanningViewModel`의 `PointCollection/Point/Color` → `Pt2/Rgba`. ViewModels 10개 이동. 전개도 투영 순수 함수(`Project()`·`FaceClipPolygon`·`HalfWidthU`·`BuildFacePlots`)를 `Rendering/FaceProjection.cs`로 추출.
5. WPF 헤드: `Infrastructure/Converters.cs`에 `Pt2→PointCollection`, `Rgba→Brush` 컨버터 추가, `WpfDispatcher`·`WpfDialogService` 어댑터, `App.xaml.cs` DI 등록. XAML 바인딩 경로는 변경 없음(컨버터만 삽입).
6. 검증: WPF 빌드 0 error, 운영/계획 화면 회귀 확인.

### Phase 1 — Avalonia 헤드 골격
1. `src/HD.Acs.UI.Avalonia/`(Avalonia 11.x Desktop 템플릿, `Avalonia.Controls.DataGrid`, `Avalonia.Themes.Fluent`). `App.axaml.cs`에 기존 `App.xaml.cs`와 동일한 Generic Host DI(코어 VM 싱글턴, `AvaloniaDispatcher`·`AvaloniaDialogService`·`AvaloniaProjectDialogService`).
2. `MainWindow.axaml`: 앱바(로고·파일 메뉴/NativeMenu·모드 ToggleButton 3개·연결칩·비상정지)·상태바·본문 3뷰 `IsVisible` 전환 — 현행 `MainWindow.xaml` 1:1.
3. `Themes/Brushes.axaml` 이식, Dark variant.
4. `OperationView`·`PlanningView`·`HistoryView`·`RobotStatusView`·`AlarmsView`·`ManualZoneChangeView`·`CalibrationView`·`NewProjectDialog`·`AreaManagementView`를 §3.3 대응표로 치환(XAML ≈1,460줄 이식). `SlicingView`·`MissionView`는 dormant/미배치라 **이식 제외**.
5. Windows에서 먼저 App(:5199)+시뮬레이터 대상 동작 확인(mac 없이도 검증 가능).

### Phase 2 — 전개도 캔버스
- `AreaLayoutView.axaml`(+픽 모드·ESC·우클릭 해제·커서), `TankView.axaml` 전개도 탭(WrapPanel 셀 격자). 코어 `FaceProjection` 결과(`Pt2[]`)를 `Points` 컨버터로 바인딩.

### Phase 3 — 3D 뷰(소프트웨어 투영)
1. 코어 `Rendering/Camera3.cs`(오빗·줌·팬·ZoomExtents), `Scene3.cs`(면·선·점·라벨 프리미티브 + 깊이 정렬), `TankSceneBuilder.cs`(현행 `TankView.xaml.cs`의 BuildShell/BuildLevelHighlight/BuildOverlays/BuildFloorGrid를 프리미티브 생성으로 이식 — `BulkheadPolygon`·`PolygonMesh`·`NormalOffset` 로직 승계).
2. Avalonia `Tank3DControl : Control` — `Render(DrawingContext)`에서 투영·그리기, 포인터 드래그=오빗/휠=줌, 수동 이동 모드 클릭=광선-바닥평면 교차 → `TankViewModel.RequestMoveAsync`.
3. 로봇 마커·상태색 material은 `StatusColors(Rgba)` 재사용.

### Phase 4 — macOS 패키징·운영
- `dotnet publish -c Release -r osx-arm64 --self-contained`(+ `osx-x64` 필요 시), `.app` 번들(Info.plist, 아이콘) + 코드서명(폐쇄망 배포는 ad-hoc 서명으로 시작, 외부 배포 시 notarization). `appsettings.json` BaseUrl=현장 서버 :5199. 한글 폰트는 시스템 폰트(Apple SD Gothic Neo) 자동.
- `.hdacs` 프로젝트 파일은 GZip+JSON이라 Win↔mac 왕복 호환(경로 구분자 의존 없음 확인됨).

### Phase 5 — WPF 헤드 처분 결정 (사용자 결정)
- Avalonia 헤드 동등성 확인 후 WPF 헤드·Telerik 피드 은퇴 여부 결정. 은퇴 시 `nuget.config` Telerik 소스 제거, CLAUDE.md 저장소 구조 갱신.

## 5. 문서 갱신
- `docs/ARCHITECTURE_DECISIONS.md`: ADR-005 개정("WPF 확정" → "Avalonia 11 크로스플랫폼 UI, 3D=자체 소프트웨어 투영") + Q5/Q5′ 재개·해소 기록, macOS 운영 요구를 결정 근거로 명시.
- `docs/TANK_RENDERING.md`: 3중복 단일화·투영 코드 위치 갱신.
- `CLAUDE.md`: 기술 스택·저장소 구조(UI.Core/UI/UI.Avalonia)·변경 이력.

## 6. 검증
1. Phase 0: `dotnet build src/HD.Acs.UI.Core`(Linux/mac 가능) + Windows에서 WPF 전체 빌드 0 error, 운영 화면 회귀(연결·시나리오 시작·work item 색·전개도·3D·프로젝트 열기/저장).
2. Phase 1~3: Avalonia 헤드를 **Windows와 mac 양쪽**에서 App(:5199)+`HD.Acs.Simulator` 대상 E2E — 연결칩 ONLINE, 시나리오 선택/시작/중단/이어하기, WorkItemProgress·TaskActionProgress 푸시 반영(그리드·전개도·3D 상태색), 수동 층 변경, 캘리브레이션 4점 캡처, 영역/작업 등록(픽 모드 포함), 3D 격리 모드 바닥 클릭 goto, 비상정지, `.hdacs` 저장→다른 OS에서 열기.
3. 코어 순수 함수(투영·카메라·깊이 정렬·광선-평면)는 `HD.Acs.Core.Tests`와 같은 xUnit 프로젝트(`HD.Acs.UI.Core.Tests`) 골든 테스트.

## 7. 리스크·미결
- **3D 시각 품질**: 소프트웨어 투영은 Helix 조명·안티앨리어싱 수준과 다를 수 있음 → Phase 3 초기에 스크린샷 비교로 수용 여부 판단, 불충분 시 ② OpenGL로 전환(카메라·씬 코드는 재사용).
- **DataGrid 기능 격차**: Telerik 필터/그룹 등을 쓰는 화면이 생기면 내장 DataGrid로 부족할 수 있음(현행은 없음).
- **Avalonia 버전 고정**: 11.x LTS 계열로 고정하고 `Avalonia.Controls.DataGrid` 동일 버전 정렬.
- **폐쇄망 NuGet**: Avalonia 패키지는 nuget.org 공개라 Telerik 피드 문제는 없음. 오프라인 복원용 패키지 캐시 준비 필요.
- **미결 결정(사용자)**: (a) WPF 헤드 유지 기간/은퇴 여부, (b) 3D ① vs ② 최종 선택은 Phase 3 프로토타입 후, (c) Web 대시보드(ADR-005 보조 UI)로 mac 요구를 대신 충족할지 여부 — 이 계획은 "동일 구조 유지" 요구에 따라 데스크톱 앱 이식을 전제한다.
