using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Logging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HD.Acs.UI.Desktop.Views;
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
            Assert.Equal(5, Find<DataGrid>(window, "AlarmGrid").Columns.Count);

            // 계획 ▸ 영역·작업(기본 탭) → AreaGrid 8열 / TaskGrid 6열, 시나리오 탭 → ScenarioGrid 5열, 캘리브레이션 → PointGrid 5열
            shell.CurrentMode = AppMode.Planning;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(8, Find<DataGrid>(window, "AreaGrid").Columns.Count);
            Assert.Equal(6, Find<DataGrid>(window, "TaskGrid").Columns.Count);

            var tabs = Find<TabControl>(window, "Tabs");
            tabs.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
            Assert.Equal(5, Find<DataGrid>(window, "ScenarioGrid").Columns.Count);
            tabs.SelectedIndex = 2; Dispatcher.UIThread.RunJobs();
            Assert.Equal(5, Find<DataGrid>(window, "PointGrid").Columns.Count);
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
            shell.CurrentMode = AppMode.History; Dispatcher.UIThread.RunJobs();
            window.Close();
        }

        foreach (var m in log.Messages) _out.WriteLine(m);   // 진단용 — 전체 바인딩 경고 덤프
        // 존재하지 않는 속성/명령 경로(오타)는 반드시 0건. (null 중간 경로 경고는 데이터 없음 상태에서 정상)
        var pathErrors = log.Messages.Where(m => m.Contains("Could not find", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(pathErrors.Count == 0, "바인딩 경로 오류:\n" + string.Join("\n", pathErrors));
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
