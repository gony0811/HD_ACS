using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Logging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HD.Acs.UI.Desktop.Views;
using HD.Acs.UI.Rendering;
using HD.Acs.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace HD.Acs.UI.Desktop.Tests;

/// <summary>
/// 셸 스모크 — 실제 DI 구성(AppHost)으로 MainWindow를 헤드리스로 띄워 XAML 로드·모드 전환·그리드 구조·다이얼로그를 확인한다.
/// 서버(:5199)는 없으므로 InitializeAsync는 호출하지 않는다(뷰·바인딩 검증만).
/// </summary>
public class ShellSmokeTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;
    public ShellSmokeTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    /// <summary>Avalonia 바인딩 로그(Warning 이상)를 수집 — 바인딩 경로 오타("Could not find a property") 검출용.</summary>
    private sealed class BindingLogCollector : ILogSink, IDisposable
    {
        private readonly ILogSink? _previous = Logger.Sink;
        public List<string> Messages { get; } = new();

        public BindingLogCollector() => Logger.Sink = this;

        public bool IsEnabled(LogEventLevel level, string area) =>
            level >= LogEventLevel.Warning && area == LogArea.Binding;

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
        {
            if (IsEnabled(level, area)) Messages.Add($"{source?.GetType().Name}: {messageTemplate}");
        }

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
        {
            if (!IsEnabled(level, area)) return;
            var text = messageTemplate;
            foreach (var v in propertyValues) text += " | " + v;
            Messages.Add($"{source?.GetType().Name}: {text}");
        }

        public void Dispose() => Logger.Sink = _previous;
    }

    private static (IHost Host, MainWindow Window, ShellViewModel Shell) CreateShell()
    {
        var host = AppHost.Build();
        var shell = host.Services.GetRequiredService<ShellViewModel>();
        var window = host.Services.GetRequiredService<MainWindow>();
        window.DataContext = shell;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (host, window, shell);
    }

    private static T Find<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    [AvaloniaFact]
    public void MainWindow_Loads_WithOperationModeVisible()
    {
        var (host, window, shell) = CreateShell();
        using (host)
        {
            Assert.Equal(AppMode.Operation, shell.CurrentMode);
            Assert.True(Find<OperationView>(window, "OperationView").IsVisible);
            Assert.False(Find<PlanningView>(window, "PlanningView").IsVisible);
            Assert.False(Find<HistoryView>(window, "HistoryView").IsVisible);
            Assert.Contains("HD_ACS", window.Title);
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ModeTabs_SwitchWorkspaces()
    {
        var (host, window, shell) = CreateShell();
        using (host)
        {
            shell.CurrentMode = AppMode.Planning;
            Dispatcher.UIThread.RunJobs();
            Assert.True(Find<PlanningView>(window, "PlanningView").IsVisible);
            Assert.False(Find<OperationView>(window, "OperationView").IsVisible);

            shell.CurrentMode = AppMode.History;
            Dispatcher.UIThread.RunJobs();
            Assert.True(Find<HistoryView>(window, "HistoryView").IsVisible);
            Assert.False(Find<PlanningView>(window, "PlanningView").IsVisible);
            window.Close();
        }
    }

    [AvaloniaFact]
    public void DataGrids_HaveExpectedColumns_AcrossTabs()
    {
        var (host, window, shell) = CreateShell();
        using (host)
        {
            // 운영 ▸ 작업 현황 탭(2번째) → WorkItemGrid(5열, RowDetails)
            Find<TabControl>(window, "LeftTabs").SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            var work = Find<DataGrid>(window, "WorkItemGrid");
            Assert.Equal(5, work.Columns.Count);
            Assert.Equal(DataGridRowDetailsVisibilityMode.VisibleWhenSelected, work.RowDetailsVisibilityMode);
            Assert.NotNull(work.RowDetailsTemplate);
            // 알람·이벤트 패널 → 현재 상태 카드 + 이벤트 로그
            Assert.NotNull(Find<Border>(window, "StatusCard"));
            Assert.NotNull(Find<Border>(window, "StationPanel"));   // 로봇 카드 ▸ 계획 정차점까지 거리
            Assert.False(string.IsNullOrEmpty(Find<TextBlock>(window, "StatusHeadline").Text));   // 바인딩 연결 확인

            // 계획 ▸ 영역·작업(기본 탭) → AreaGrid 8열 / TaskGrid 7열(유형 포함), 시나리오 탭 → ScenarioGrid 5열, 캘리브레이션 → PointGrid 5열
            shell.CurrentMode = AppMode.Planning;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(8, Find<DataGrid>(window, "AreaGrid").Columns.Count);
            Assert.Equal(7, Find<DataGrid>(window, "TaskGrid").Columns.Count);

            var tabs = Find<TabControl>(window, "Tabs");
            tabs.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
            Assert.Equal(5, Find<DataGrid>(window, "ScenarioGrid").Columns.Count);
            tabs.SelectedIndex = 2; Dispatcher.UIThread.RunJobs();
            Assert.Equal(5, Find<DataGrid>(window, "PointGrid").Columns.Count);
            // 배터리 교체 장소 탭 [HD_AMR 배터리관리] — 6열(층·이름·X·Y·theta·nodeId) + VM 바인딩
            tabs.SelectedIndex = 3; Dispatcher.UIThread.RunJobs();
            Assert.Equal(6, Find<DataGrid>(window, "NodesGrid").Columns.Count);
            Assert.Same(shell.BatterySwapNodes, Find<DataGrid>(window, "NodesGrid").DataContext);
            window.Close();
        }
    }

    [AvaloniaFact]
    public void AllViews_HaveNoBindingPathErrors()
    {
        using var log = new BindingLogCollector();
        var (host, window, shell) = CreateShell();
        using (host)
        {
            // 모든 모드·탭을 한 번씩 실체화해 바인딩을 평가시킨다
            Find<TabControl>(window, "LeftTabs").SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
            Find<TabControl>(window, "LeftTabs").SelectedIndex = 2; Dispatcher.UIThread.RunJobs();
            Find<TabControl>(window, "TankTabs").SelectedIndex = 1; Dispatcher.UIThread.RunJobs();   // 전개도 탭
            shell.CurrentMode = AppMode.Planning; Dispatcher.UIThread.RunJobs();
            var planningTabs = Find<TabControl>(window, "Tabs");
            planningTabs.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
            planningTabs.SelectedIndex = 2; Dispatcher.UIThread.RunJobs();
            planningTabs.SelectedIndex = 3; Dispatcher.UIThread.RunJobs();   // 배터리 교체 장소 탭 — 바인딩 평가
            shell.CurrentMode = AppMode.History; Dispatcher.UIThread.RunJobs();
            window.Close();
        }

        foreach (var m in log.Messages) _out.WriteLine(m);   // 진단용 — 전체 바인딩 경고 덤프
        // 존재하지 않는 속성/명령 경로(오타)는 반드시 0건. (null 중간 경로 경고는 데이터 없음 상태에서 정상)
        var pathErrors = log.Messages.Where(m => m.Contains("Could not find", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(pathErrors.Count == 0, "바인딩 경로 오류:\n" + string.Join("\n", pathErrors));
    }

    [AvaloniaFact]
    public void PlanningAssistantPanel_IsBoundToAssistantViewModel()
    {
        var (host, window, shell) = CreateShell();
        using (host)
        {
            shell.CurrentMode = AppMode.Planning; Dispatcher.UIThread.RunJobs();
            var panel = Find<Border>(window, "AssistantPanel");
            Assert.Same(shell.AreaPlanning.Assistant, panel.DataContext);
            var a = shell.AreaPlanning.Assistant;
            Assert.False(a.SendCommand.CanExecute(null));      // 입력 없음
            a.Input = "PM 2층 영역 몇 개?";
            Assert.True(a.SendCommand.CanExecute(null));
            Assert.False(a.ApplyCommand.CanExecute(null));     // 변경안 없음
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Tank3DControl_RendersDrawList_WithoutData()
    {
        var (host, window, _) = CreateShell();
        using (host)
        {
            var tank3d = window.GetVisualDescendants().OfType<Tank3DControl>().First();
            var draws = tank3d.BuildDrawList(800, 600);
            // 데이터 없음 → 지면 참조 격자(9+9 선분)만. 예외 없이 투영·정렬됨.
            Assert.Equal(18, draws.Count);
            Assert.All(draws, d => Assert.IsType<Segment2>(d));
            tank3d.InvalidateVisual();
            Dispatcher.UIThread.RunJobs();
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Tank3D_RightClick_OpensDisplayMenu_ButRightDragPansOnly()
    {
        using var log = new BindingLogCollector();
        var (host, window, shell) = CreateShell();
        using (host)
        {
            var tank3d = window.GetVisualDescendants().OfType<Tank3DControl>().First();
            var menu = tank3d.ContextMenu!;
            var center = tank3d.TranslatePoint(new Point(tank3d.Bounds.Width / 2, tank3d.Bounds.Height / 2), window)!.Value;

            // 우드래그(팬) — 메뉴가 열리면 안 된다
            window.MouseDown(center, MouseButton.Right);
            window.MouseMove(center + new Point(40, 10), RawInputModifiers.RightMouseButton);
            window.MouseUp(center + new Point(40, 10), MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            Assert.False(menu.IsOpen);

            // 제자리 우클릭 — 표시 항목 메뉴
            window.MouseDown(center, MouseButton.Right);
            window.MouseUp(center, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            Assert.True(menu.IsOpen);

            // 체크 항목 ↔ VM 양방향: VM을 바꾸면 체크가, 체크를 바꾸면(메뉴 클릭과 같은 SetCurrentValue) VM이 바뀐다
            var layers = shell.Tank.View3DLayers;
            var weld = menu.Items.OfType<MenuItem>().Single(i => (string?)i.Header == "용접선");
            var seq = menu.Items.OfType<MenuItem>().Single(i => (string?)i.Header == "작업 순번");
            Assert.Equal(MenuItemToggleType.CheckBox, weld.ToggleType);
            Assert.True(weld.IsChecked);
            Assert.False(seq.IsChecked);   // 기본: 작업 순번 끔
            layers.WeldLines = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(weld.IsChecked);
            seq.SetCurrentValue(MenuItem.IsCheckedProperty, true);
            Dispatcher.UIThread.RunJobs();
            Assert.True(layers.TaskSeq);
            Assert.DoesNotContain(log.Messages, m => m.Contains("Could not find", StringComparison.OrdinalIgnoreCase));
            menu.Close();
            window.Close();
        }
    }

    [AvaloniaFact]
    public void FlatView_HasDisplayMenu_BoundToFlatLayers()
    {
        using var log = new BindingLogCollector();
        var (host, window, shell) = CreateShell();
        using (host)
        {
            var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(s => s.Name == "FlatScroll");
            if (scroll is null)
            {
                var tabs = window.GetVisualDescendants().OfType<TabControl>().First(t => t.Name == "TankTabs");
                tabs.SelectedIndex = 1;
                Dispatcher.UIThread.RunJobs();
                scroll = window.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "FlatScroll");
            }
            var menu = scroll.ContextMenu!;
            menu.Open(scroll);
            Dispatcher.UIThread.RunJobs();
            var names = menu.Items.OfType<MenuItem>().Where(i => i.ToggleType == MenuItemToggleType.CheckBox)
                .Select(i => (string?)i.Header).ToList();
            Assert.Equal(new[] { "영역 (채움·윤곽)", "영역 이름", "용접선", "용접 시작·끝점", "작업 순번" }, names);   // 3D 격자 항목 없음

            var fills = menu.Items.OfType<MenuItem>().Single(i => (string?)i.Header == "영역 (채움·윤곽)");
            fills.SetCurrentValue(MenuItem.IsCheckedProperty, false);
            Dispatcher.UIThread.RunJobs();
            Assert.False(shell.Tank.FlatLayers.AreaFills);
            Assert.True(shell.Tank.View3DLayers.AreaFills);   // 두 뷰의 설정은 서로 독립
            Assert.DoesNotContain(log.Messages, m => m.Contains("Could not find", StringComparison.OrdinalIgnoreCase));
            menu.Close();
            window.Close();
        }
    }

    [AvaloniaFact]
    public void NewProjectDialog_And_MessageDialog_Construct()
    {
        var (host, window, _) = CreateShell();
        using (host)
        {
            var dlg = new NewProjectDialog { DataContext = host.Services.GetRequiredService<AreaPlanningViewModel>() };
            dlg.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("선창 3D 정의", dlg.Title!);
            Assert.True(dlg.GetVisualDescendants().OfType<NumericUpDown>().Count() >= 9);
            dlg.Close();

            var msg = new MessageDialog("본문", "제목", yesNo: true);
            msg.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("제목", msg.Title);
            var buttons = msg.GetVisualDescendants().OfType<Button>().Where(b => b.IsVisible).ToList();
            Assert.Equal(2, buttons.Count);   // 예/아니오만 보임
            msg.Close();
            window.Close();
        }
    }
}
