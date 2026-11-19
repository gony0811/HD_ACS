using System.Reflection;
using HD.Acs.UI.Abstractions;
using HD.Acs.UI.Models;
using HD.Acs.UI.Services;
using HD.Acs.UI.ViewModels;
using Xunit;

namespace HD.Acs.UI.Core.Tests;

/// <summary>
/// 미션 시작 전 확인 — 이어할 수 있는 중단 run이 있으면 묻는다.
/// 2026-11-11 현장: 중단 run 대신 '미션 시작'으로 새 run이 생겨 끝낸 영역부터 다시 검사했고, 그 뒤로 이어하기는 새 run만 이어갔다.
/// </summary>
public class StartRunResumableTests
{
    public class Api : DispatchProxy
    {
        public static ResumableRunDto? Resumable;
        public static List<bool> Starts = new();
        public static readonly Guid NewRun = Guid.NewGuid();

        protected override object? Invoke(MethodInfo? m, object?[]? args)
        {
            object? v = null;
            switch (m!.Name)
            {
                case nameof(IAcsApiClient.GetResumableRunAsync): v = Resumable; break;
                case nameof(IAcsApiClient.StartRunAsync):
                    Starts.Add(args!.Length == 5 && (bool)args[3]!);
                    v = NewRun;
                    break;
                case nameof(IAcsApiClient.GetRunAsync):
                    v = new ScenarioRunDto(NewRun, Guid.NewGuid(), 1, "AMR-01", "RUNNING", DateTimeOffset.Now, null, new List<MissionDto>());
                    break;
            }
            var t = m.ReturnType.IsGenericType ? m.ReturnType.GetGenericArguments()[0] : null;
            if (t is null) return Task.CompletedTask;
            if (v is null && t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
                v = Array.CreateInstance(t.GetGenericArguments()[0], 0);
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(t).Invoke(null, new[] { v });
        }
    }

    private sealed class Dialogs(bool answer) : IDialogService
    {
        public List<string> Asked { get; } = new();
        public Task ShowErrorAsync(string message, string caption) => Task.CompletedTask;
        public Task<bool> ConfirmAsync(string message, string caption) { Asked.Add(message); return Task.FromResult(answer); }
    }

    private static MissionViewModel Vm(IDialogService? dialogs, ResumableRunDto? resumable)
    {
        Api.Resumable = resumable; Api.Starts = new();
        var vm = new MissionViewModel(DispatchProxy.Create<IAcsApiClient, Api>(),
            DispatchProxy.Create<IMonitoringClient, OverlayLoadTests.NoMonitoring>(), dialogs) { SelectedRobotId = "AMR-01" };
        vm.Scenarios.Add(new ScenarioSummaryDto(Guid.NewGuid(), "L4-천장", 1, "CT1", "ACTIVE"));
        vm.SelectedScenario = vm.Scenarios[0];
        return vm;
    }

    private static ResumableRunDto Aborted() =>
        new(Guid.NewGuid(), Guid.NewGuid(), "ABORTED", DateTimeOffset.Now.AddMinutes(-5), Pending: 2, Done: 2, Skipped: 0, ScenarioName: "L4-천장");

    [Fact]
    public async Task AbortedRunWithWork_AsksFirst_AndNoMeansNoNewRun()
    {
        var dialogs = new Dialogs(answer: false);
        var vm = Vm(dialogs, Aborted());
        await vm.StartRunCommand.ExecuteAsync(null);

        Assert.Single(dialogs.Asked);
        Assert.Contains("완료 2", dialogs.Asked[0]);
        Assert.Contains("남은 정차 2", dialogs.Asked[0]);
        Assert.Contains("이어하기", dialogs.Asked[0]);
        Assert.Empty(Api.Starts);
        Assert.Contains("이어하기", vm.StatusMessage);
    }

    [Fact]
    public async Task AbortedRunWithWork_YesStartsOverWithRestartFlag()
    {
        var vm = Vm(new Dialogs(answer: true), Aborted());
        await vm.StartRunCommand.ExecuteAsync(null);
        Assert.Equal(new[] { true }, Api.Starts);
    }

    [Fact]
    public async Task NoResumableRun_StartsWithoutAsking()
    {
        var dialogs = new Dialogs(answer: false);
        var vm = Vm(dialogs, null);
        await vm.StartRunCommand.ExecuteAsync(null);
        Assert.Empty(dialogs.Asked);
        Assert.Equal(new[] { false }, Api.Starts);
    }

    [Fact]
    public async Task NoDialogService_DoesNotStartOverAResumableRun()
    {
        var vm = Vm(null, Aborted());
        await vm.StartRunCommand.ExecuteAsync(null);
        Assert.Empty(Api.Starts);
    }
}
