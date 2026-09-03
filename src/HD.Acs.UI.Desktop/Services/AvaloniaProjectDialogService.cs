using Avalonia.Platform.Storage;
using HD.Acs.UI.Desktop.Views;
using HD.Acs.UI.Services;
using HD.Acs.UI.ViewModels;

namespace HD.Acs.UI.Desktop.Services;

/// <summary>IProjectDialogService의 Avalonia 구현 — 새 프로젝트 팝업 + StorageProvider 파일 선택기(*.hdacs).</summary>
public sealed class AvaloniaProjectDialogService : IProjectDialogService
{
    private static readonly FilePickerFileType HdacsType = new("HD_ACS 프로젝트")
    {
        Patterns = new[] { "*" + ProjectService.Extension },
    };

    private readonly AreaPlanningViewModel _areaPlanning;

    public AvaloniaProjectDialogService(AreaPlanningViewModel areaPlanning) => _areaPlanning = areaPlanning;

    public async Task<bool> ShowNewProjectAsync()
    {
        var owner = AvaloniaDialogService.Owner;
        if (owner is null) return false;
        var dialog = new NewProjectDialog { DataContext = _areaPlanning };
        return await dialog.ShowDialog<bool>(owner);
    }

    public async Task<string?> PickSavePathAsync(string? suggestedFileName = null)
    {
        var owner = AvaloniaDialogService.Owner;
        if (owner is null) return null;
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "프로젝트 저장",
            SuggestedFileName = suggestedFileName ?? "새 프로젝트",
            DefaultExtension = ProjectService.Extension.TrimStart('.'),
            FileTypeChoices = new[] { HdacsType },
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickOpenPathAsync()
    {
        var owner = AvaloniaDialogService.Owner;
        if (owner is null) return null;
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "프로젝트 열기",
            AllowMultiple = false,
            FileTypeFilter = new[] { HdacsType, FilePickerFileTypes.All },
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
