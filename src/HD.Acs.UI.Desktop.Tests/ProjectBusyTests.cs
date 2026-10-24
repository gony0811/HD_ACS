using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HD.Acs.UI.Models;
using HD.Acs.UI.Services;
using HD.Acs.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HD.Acs.UI.Desktop.Tests;

/// <summary>
/// 프로젝트 열기 진행 중 저장 차단 — 대형 선창 열기는 DB를 지우고 수천 건을 다시 등록하는 동안(수 분)
/// 저장하면 반쯤 적재된 상태가 파일에 덮어써진다(현장 제보). 열기 중에는 파일 메뉴 4개 명령이 모두 꺼지고
/// 앱바에 진행 칩이 보여야 한다.
/// </summary>
public class ProjectBusyTests
{
    /// <summary>OpenAsync가 Release() 전까지 끝나지 않는 가짜 — 진행 보고만 한다.</summary>
    private sealed class BlockingProjectService : IProjectService
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IProgress<ProjectProgress>? OpenProgress { get; private set; }
        public int SaveCalls { get; private set; }
        public string? CurrentPath { get; private set; }

        public void Release() => _release.SetResult();

        public Task SaveAsync(string tankId, string path, CancellationToken ct = default, IProgress<ProjectProgress>? progress = null)
        {
            SaveCalls++;
            return Task.CompletedTask;
        }

        public async Task<ProjectDoc> OpenAsync(string path, CancellationToken ct = default, IProgress<ProjectProgress>? progress = null)
        {
            OpenProgress = progress;
            progress?.Report(new ProjectProgress("영역·작업 등록", 1234, 8789));
            await _release.Task;
            CurrentPath = path;
            return new ProjectDoc(3, "CT1",
                new GeometryDoc(45, 8.2, 45, 1.9, 5.4, 45, 1.9, new[] { 0.0 }, 0, 0, null, null),
                Array.Empty<AreaDoc>(), Array.Empty<CalibrationDoc>(), Array.Empty<ScenarioDoc>());
        }
    }

    private sealed class FixedPathDialogs : IProjectDialogService
    {
        public Task<bool> ShowNewProjectAsync() => Task.FromResult(false);
        public Task<string?> PickSavePathAsync(string? suggestedFileName = null) => Task.FromResult<string?>("C:/tmp/x.hdacs");
        public Task<string?> PickOpenPathAsync() => Task.FromResult<string?>("C:/tmp/CT1-KC-2.hdacs");
    }

    [AvaloniaFact]
    public async Task WhileOpening_FileCommandsDisabled_AndProgressChipShown()
    {
        var project = new BlockingProjectService();
        using var host = AppHost.Build(s =>
        {
            s.AddSingleton<IProjectService>(project);
            s.AddSingleton<IProjectDialogService>(new FixedPathDialogs());
        });
        var shell = host.Services.GetRequiredService<ShellViewModel>();
        var window = host.Services.GetRequiredService<MainWindow>();
        window.DataContext = shell;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var chip = window.GetVisualDescendants().OfType<Border>().First(b => b.Name == "ProjectProgressChip");
        Assert.False(chip.IsVisible);
        Assert.True(shell.SaveProjectCommand.CanExecute(null));

        var opening = shell.OpenProjectCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(shell.IsProjectBusy);
        Assert.False(shell.SaveProjectCommand.CanExecute(null));
        Assert.False(shell.SaveProjectAsCommand.CanExecute(null));
        Assert.False(shell.NewProjectCommand.CanExecute(null));
        Assert.False(shell.OpenProjectCommand.CanExecute(null));
        Assert.True(chip.IsVisible);
        Assert.Equal("열기 중 · 영역·작업 등록 1,234/8,789 (14%)", shell.ProjectProgressText);
        Assert.False(shell.ProjectProgressIndeterminate);
        var label = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "ProjectProgressLabel");
        Assert.Equal(shell.ProjectProgressText, label.Text);

        // 메뉴 경로로 눌러도 저장이 실행되지 않는다(명령 비활성)
        shell.SaveProjectCommand.Execute(null);
        Assert.Equal(0, project.SaveCalls);

        project.Release();
        await opening;   // 화면 갱신(서버 없음 — 각 VM이 조회 실패를 자체 처리)까지 끝난다
        Dispatcher.UIThread.RunJobs();

        Assert.False(shell.IsProjectBusy);
        Assert.Equal("", shell.ProjectProgressText);
        Assert.False(chip.IsVisible);
        Assert.True(shell.SaveProjectCommand.CanExecute(null));
        await shell.SaveProjectCommand.ExecuteAsync(null);
        Assert.Equal(1, project.SaveCalls);   // 열기 완료 후 저장은 정상
        window.Close();
    }
}
