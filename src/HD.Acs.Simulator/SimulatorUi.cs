using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

internal static class SimulatorUi
{
    public static void Run(SimulatorControlState control)
    {
        SimulatorApplication.Control = control;
        AppBuilder.Configure<SimulatorApplication>()
            .UsePlatformDetect()
            .LogToTrace()
            .StartWithClassicDesktopLifetime(Array.Empty<string>());
    }
}

internal sealed class SimulatorApplication : Application
{
    public static SimulatorControlState? Control { get; set; }

    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && Control is { } control)
            desktop.MainWindow = new SimulatorControlWindow(control);
        base.OnFrameworkInitializationCompleted();
    }
}

internal sealed class SimulatorControlWindow : Window
{
    private readonly SimulatorControlState _control;
    private readonly ComboBox _mapId;
    private readonly NumericUpDown _battery;
    private readonly NumericUpDown _x;
    private readonly NumericUpDown _y;
    private readonly NumericUpDown _theta;
    private readonly TextBlock _published;
    private bool _dirty;
    private bool _updatingInputs;

    public SimulatorControlWindow(SimulatorControlState control)
    {
        _control = control;
        Title = "HD ACS · AMR Simulator Control";
        Width = 460;
        Height = 520;
        MinWidth = 420;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var initial = control.Initial;
        _mapId = new ComboBox
        {
            ItemsSource = BuildMapChoices(initial.MapId),
            SelectedItem = initial.MapId,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _battery = Number(initial.BatteryPct, 0, 100, 1, "0.0");
        _x = Number(initial.X, -100000, 100000, 0.01m, "0.000");
        _y = Number(initial.Y, -100000, 100000, 0.01m, "0.000");
        _theta = Number(initial.Theta, -1000, 1000, 0.01m, "0.000");
        _published = new TextBlock
        {
            Text = "MQTT 연결 및 최초 상태 발행 대기 중…",
            Foreground = Brushes.LightGray,
            TextWrapping = TextWrapping.Wrap
        };

        var apply = new Button
        {
            Content = "상태 적용 및 즉시 보고",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(12),
            FontWeight = FontWeight.SemiBold
        };
        apply.Click += (_, _) => Apply();

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = "AMR 상태 제어", FontSize = 24, FontWeight = FontWeight.Bold },
                    new TextBlock
                    {
                        Text = "변경한 값은 VDA 5050 state로 즉시 발행되며 이후 2초마다 계속 보고됩니다.",
                        Foreground = Brushes.LightGray,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 0, 0, 8)
                    },
                    Field("현재 층 (mapId)", _mapId),
                    Field("배터리 잔량 (%)", _battery),
                    new Separator { Margin = new Thickness(0, 5) },
                    new TextBlock { Text = "현재 위치 (SLAM 좌표계)", FontSize = 17, FontWeight = FontWeight.SemiBold },
                    Field("X (m)", _x),
                    Field("Y (m)", _y),
                    Field("Theta (rad)", _theta),
                    apply,
                    new Border
                    {
                        Background = new SolidColorBrush(Color.Parse("#202936")),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(12),
                        Margin = new Thickness(0, 6, 0, 0),
                        Child = _published
                    }
                }
            }
        };

        _mapId.SelectionChanged += (_, _) => MarkDirty();
        _battery.ValueChanged += (_, _) => MarkDirty();
        _x.ValueChanged += (_, _) => MarkDirty();
        _y.ValueChanged += (_, _) => MarkDirty();
        _theta.ValueChanged += (_, _) => MarkDirty();
        control.StatePublished += OnStatePublished;
        Closed += (_, _) => control.StatePublished -= OnStatePublished;
    }

    private void Apply()
    {
        var mapId = _mapId.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(mapId))
        {
            _published.Text = "층(mapId)을 선택하세요.";
            _published.Foreground = Brushes.Orange;
            return;
        }

        _control.RequestUpdate(new SimulatorStateUpdate(
            mapId,
            (double)(_battery.Value ?? 0),
            (double)(_x.Value ?? 0),
            (double)(_y.Value ?? 0),
            (double)(_theta.Value ?? 0)));
        _dirty = false;
        _published.Text = "변경 요청 전송됨 — MQTT 발행 확인 대기 중…";
        _published.Foreground = Brushes.Gold;
    }

    private void OnStatePublished(object? sender, SimulatorStateSnapshot state)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // 사용자가 값을 편집 중이면 2초 주기 보고가 입력란을 덮어쓰지 않는다.
            if (!_dirty)
            {
                _updatingInputs = true;
                _mapId.SelectedItem = state.MapId;
                _battery.Value = (decimal)state.BatteryPct;
                _x.Value = (decimal)state.X;
                _y.Value = (decimal)state.Y;
                _theta.Value = (decimal)state.Theta;
                _updatingInputs = false;
            }
            _published.Text = $"보고 완료  ·  {state.MapId}  ·  " +
                              $"({state.X:F3}, {state.Y:F3}, {state.Theta:F3} rad)  ·  " +
                              $"배터리 {state.BatteryPct:F1}%";
            _published.Foreground = Brushes.LightGreen;
        });
    }

    private void MarkDirty()
    {
        if (!_updatingInputs)
            _dirty = true;
    }

    private static Control Field(string label, Control input) => new StackPanel
    {
        Spacing = 5,
        Children = { new TextBlock { Text = label, FontWeight = FontWeight.Medium }, input }
    };

    private static NumericUpDown Number(double value, decimal min, decimal max, decimal increment, string format)
        => new()
        {
            Value = (decimal)value,
            Minimum = min,
            Maximum = max,
            Increment = increment,
            FormatString = format,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

    private static string[] BuildMapChoices(string initial)
        => new[] { initial, "CT1-L1", "CT1-L2", "CT1-L3", "CT1-L4" }
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}
