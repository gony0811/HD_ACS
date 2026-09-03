using HD.Acs.UI.Abstractions;
using HD.Acs.UI.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HD.Acs.UI.Core.Tests;

/// <summary>MonitoringClient가 프레임워크 Dispatcher 대신 주입된 IUiDispatcher로 이벤트를 마샬링하는지 확인.</summary>
public class MonitoringClientTests
{
    private sealed class RecordingDispatcher : IUiDispatcher
    {
        public int Posted;
        public void Post(Action action) { Posted++; action(); }
    }

    [Fact]
    public async Task StartAsync_WithoutServer_ReportsStatusThroughDispatcher()
    {
        var dispatcher = new RecordingDispatcher();
        var opts = Options.Create(new AcsOptions { BaseUrl = "http://127.0.0.1:1" });   // 즉시 연결 거부
        await using var client = new MonitoringClient(opts, NullLogger<MonitoringClient>.Instance, dispatcher);

        var seen = new List<HubStatus>();
        client.StatusChanged += (_, s) => seen.Add(s);

        await client.StartAsync();

        Assert.Equal(HubStatus.Failed, client.Status);
        Assert.Equal(new[] { HubStatus.Connecting, HubStatus.Failed }, seen);
        Assert.Equal(2, dispatcher.Posted);   // 상태 변화 2건 모두 디스패처 경유
    }
}
