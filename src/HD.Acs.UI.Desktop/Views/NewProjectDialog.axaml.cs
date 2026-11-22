using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using HD.Acs.UI.Models;
using HD.Acs.UI.Services;
using HD.Acs.UI.ViewModels;

namespace HD.Acs.UI.Desktop.Views;

/// <summary>
/// 새 프로젝트 팝업 — 선창 3D 파라미터를 입력받아 선창/면 등록을 수행한다.
/// DataContext = AreaPlanningViewModel(공유 싱글턴). 확인 시 등록 성공하면 Close(true), 취소는 Close(false).
/// 베이스 프로젝트 파일을 고르면 등록하지 않고 Close(true) — 복제는 ShellViewModel 이 진행 표시와 함께 한다.
/// </summary>
public partial class NewProjectDialog : Window
{
    public NewProjectDialog() => InitializeComponent();

    /// <summary>프로젝트 파일 읽기(DB 무변경) — 대화상자 서비스가 연결. 없으면 베이스 선택 불가.</summary>
    public Func<string, Task<ProjectDoc>>? ReadProject { get; set; }

    private async void BrowseBase_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AreaPlanningViewModel vm || ReadProject is null) return;
        ErrorText.IsVisible = false;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "베이스 프로젝트 선택",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("HD_ACS 프로젝트") { Patterns = new[] { "*" + ProjectService.Extension } },
                FilePickerFileTypes.All,
            },
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        try
        {
            vm.ApplyBaseProject(path, await ReadProject(path));
        }
        catch (Exception ex)
        {
            vm.ClearBaseProject();
            ShowError($"베이스 프로젝트를 읽지 못했습니다: {ex.Message}");
        }
    }

    private void ClearBase_Click(object? sender, RoutedEventArgs e) =>
        (DataContext as AreaPlanningViewModel)?.ClearBaseProject();

    private async void Ok_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AreaPlanningViewModel vm) return;

        OkButton.IsEnabled = false;
        ErrorText.IsVisible = false;
        try
        {
            if (vm.HasBaseProject)
            {
                // 복제는 창을 닫은 뒤 Shell 이 한다 — 여기서는 선창 ID 형식만 본다
                if (ProjectService.TankIdProblem(vm.TankId?.Trim()) is { } problem) { ShowError(problem); return; }
                Close(true);
                return;
            }
            if (await vm.TryRegisterGeometryAsync())
            {
                Close(true);
                return;
            }
            ShowError(vm.StatusMessage ?? "선창 등록에 실패했습니다.");
        }
        finally
        {
            OkButton.IsEnabled = true;
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
