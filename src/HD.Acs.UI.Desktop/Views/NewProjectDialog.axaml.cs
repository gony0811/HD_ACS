using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using HD.Acs.UI.ViewModels;

namespace HD.Acs.UI.Desktop.Views;

/// <summary>
/// 새 프로젝트 팝업 — 두 가지 방식으로 선창을 만든다.
///  ① 직접 입력: 팔각 단면 파라미터(L/w_floor/θ/h)를 수기 입력 → <see cref="AreaPlanningViewModel.TryRegisterGeometryAsync"/>.
///  ② 도면(DXF)에서 생성: 면별 CAD를 등록 → 도면 역산 <see cref="AreaPlanningViewModel.TryReconstructGeometryFromCadAsync"/>.
/// level_z·reach_z·원점 등 운영 파라미터는 두 방식 공통. DataContext = AreaPlanningViewModel(공유 싱글턴).
/// 확인 시 등록 성공하면 Close(true), 취소는 Close(false).
/// </summary>
public partial class NewProjectDialog : Window
{
    public NewProjectDialog()
    {
        InitializeComponent();
        // 팝업이 열릴 때마다 직전 프로젝트의 면 CAD 잔재를 비운다.
        Opened += (_, _) => (DataContext as AreaPlanningViewModel)?.ResetFaceCad();
    }

    /// <summary>생성 방식 라디오 전환 — 해당 입력 패널만 표시한다.</summary>
    private void Mode_Changed(object? sender, RoutedEventArgs e)
    {
        // InitializeComponent 이전(초기 IsChecked 설정 시)에는 패널이 아직 없다.
        if (ManualPanel is null || CadPanel is null) return;
        bool cad = CadRadio.IsChecked == true;
        ManualPanel.IsVisible = !cad;
        CadPanel.IsVisible = cad;
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
            bool cad = CadRadio.IsChecked == true;
            // ② 도면(DXF) 역산 또는 ① 수기 파라미터 등록.
            bool ok = cad
                ? await vm.TryReconstructGeometryFromCadAsync()
                : await vm.TryRegisterGeometryAsync();
            if (ok)
            {
                Close(true);
                return;
            }
            ErrorText.Text = vm.StatusMessage
                ?? (cad ? "도면에서 선창을 생성하지 못했습니다." : "선창 등록에 실패했습니다.");
            ErrorText.IsVisible = true;
        }
        finally
        {
            OkButton.IsEnabled = true;
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
