using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using HD.Acs.UI.ViewModels;

namespace HD.Acs.UI.Desktop.Views;

/// <summary>
/// 새 프로젝트 팝업 — 선창 3D 파라미터를 입력받아 선창/면 등록을 수행하고, 면별 CAD(DXF)를 등록한다.
/// DataContext = AreaPlanningViewModel(공유 싱글턴). 확인 시 등록 성공하면 Close(true), 취소는 Close(false).
/// </summary>
public partial class NewProjectDialog : Window
{
    public NewProjectDialog()
    {
        InitializeComponent();
        // 팝업이 열릴 때마다 직전 프로젝트의 면 CAD 잔재를 비운다.
        Opened += (_, _) => (DataContext as AreaPlanningViewModel)?.ResetFaceCad();
    }

    /// <summary>면 DXF 선택 — 로컬 경로를 얻어 해당 면 CAD로 등록(파싱·자동 분류).</summary>
    private async void PickDxf_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AreaPlanningViewModel vm) return;
        if (sender is not Control { Tag: string wallCode }) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"{wallCode} 면 DXF 선택",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("DXF 도면") { Patterns = new[] { "*.dxf" } } },
        });
        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) { vm.StatusMessage = "로컬 파일 경로를 얻지 못했습니다."; return; }
        vm.RegisterFaceCad(wallCode, path);
    }

    private void ClearDxf_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AreaPlanningViewModel vm && sender is Control { Tag: string wallCode })
            vm.ClearFaceCad(wallCode);
    }

    private async void Ok_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AreaPlanningViewModel vm) return;

        OkButton.IsEnabled = false;
        ErrorText.IsVisible = false;
        try
        {
            // 도면(DXF)에서 팔각 단면·길이를 추출해 선창 생성 (파라미터 수기 입력 없음)
            if (await vm.TryReconstructGeometryFromCadAsync())
            {
                Close(true);
                return;
            }
            ErrorText.Text = vm.StatusMessage ?? "도면에서 선창을 생성하지 못했습니다.";
            ErrorText.IsVisible = true;
        }
        finally
        {
            OkButton.IsEnabled = true;
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
