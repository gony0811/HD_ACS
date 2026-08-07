using System.Windows;
using HD.Acs.UI.ViewModels;
using HD.Acs.UI.Views;
using Microsoft.Win32;

namespace HD.Acs.UI.Services;

/// <summary>IProjectDialogService의 WPF 구현.</summary>
public sealed class ProjectDialogService : IProjectDialogService
{
    private const string Filter = "HD_ACS 프로젝트 (*.hdacs)|*.hdacs|모든 파일 (*.*)|*.*";

    private readonly AreaPlanningViewModel _areaPlanning;

    public ProjectDialogService(AreaPlanningViewModel areaPlanning) => _areaPlanning = areaPlanning;

    public bool ShowNewProject()
    {
        var dialog = new NewProjectDialog
        {
            DataContext = _areaPlanning,
            Owner = Application.Current.MainWindow,
        };
        return dialog.ShowDialog() == true;
    }

    public string? PickSavePath(string? suggestedFileName = null)
    {
        var dlg = new SaveFileDialog
        {
            Filter = Filter,
            DefaultExt = ProjectService.Extension,
            AddExtension = true,
            FileName = suggestedFileName ?? "새 프로젝트",
        };
        return dlg.ShowDialog(Application.Current.MainWindow) == true ? dlg.FileName : null;
    }

    public string? PickOpenPath()
    {
        var dlg = new OpenFileDialog
        {
            Filter = Filter,
            DefaultExt = ProjectService.Extension,
            CheckFileExists = true,
        };
        return dlg.ShowDialog(Application.Current.MainWindow) == true ? dlg.FileName : null;
    }
}
