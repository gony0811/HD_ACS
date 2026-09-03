using System.Windows;
using HD.Acs.UI.ViewModels;
using HD.Acs.UI.Views;
using Microsoft.Win32;

namespace HD.Acs.UI.Services;

/// <summary>IProjectDialogService의 WPF 구현 — 동기 대화상자를 Task로 감싼다(계약은 Avalonia와 공유되어 비동기).</summary>
public sealed class ProjectDialogService : IProjectDialogService
{
    private const string Filter = "HD_ACS 프로젝트 (*.hdacs)|*.hdacs|모든 파일 (*.*)|*.*";

    private readonly AreaPlanningViewModel _areaPlanning;

    public ProjectDialogService(AreaPlanningViewModel areaPlanning) => _areaPlanning = areaPlanning;

    public Task<bool> ShowNewProjectAsync()
    {
        var dialog = new NewProjectDialog
        {
            DataContext = _areaPlanning,
            Owner = Application.Current.MainWindow,
        };
        return Task.FromResult(dialog.ShowDialog() == true);
    }

    public Task<string?> PickSavePathAsync(string? suggestedFileName = null)
    {
        var dlg = new SaveFileDialog
        {
            Filter = Filter,
            DefaultExt = ProjectService.Extension,
            AddExtension = true,
            FileName = suggestedFileName ?? "새 프로젝트",
        };
        return Task.FromResult(dlg.ShowDialog(Application.Current.MainWindow) == true ? dlg.FileName : null);
    }

    public Task<string?> PickOpenPathAsync()
    {
        var dlg = new OpenFileDialog
        {
            Filter = Filter,
            DefaultExt = ProjectService.Extension,
            CheckFileExists = true,
        };
        return Task.FromResult(dlg.ShowDialog(Application.Current.MainWindow) == true ? dlg.FileName : null);
    }
}
