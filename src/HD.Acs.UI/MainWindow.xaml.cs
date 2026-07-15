using System.Windows;
using Microsoft.AspNetCore.SignalR.Client;

namespace HD.Acs.UI;

public partial class MainWindow : Window
{
    private HubConnection? _hub;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        // API-First: WPF도 REST + SignalR만 사용 [ADR-005]
        _hub = new HubConnectionBuilder()
            .WithUrl("http://localhost:5100/hubs/monitoring")
            .WithAutomaticReconnect()
            .Build();

        _hub.On<object>("RobotState", payload =>
            Dispatcher.Invoke(() => ConnStatus.Text = $"RobotState: {payload}"));

        try
        {
            await _hub.StartAsync();
            ConnStatus.Text = "서버 연결됨";
        }
        catch
        {
            ConnStatus.Text = "서버 연결 실패 — HD.Acs.App 실행 확인";
        }
    }
}
