using Avalonia.Controls;
using Avalonia.Interactivity;
using HD.Acs.UI.ViewModels;

namespace HD.Acs.UI.Desktop.Views;

/// <summary>
/// 새 프로젝트 팝업 — 선창 3D 파라미터를 입력받아 선창/면 등록을 수행한다.
/// DataContext = AreaPlanningViewModel(공유 싱글턴). 확인 시 등록 성공하면 Close(true), 취소는 Close(false).
/// </summary>
public partial class NewProjectDialog : Window
{
    public NewProjectDialog() => InitializeComponent();

    private async void Ok_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AreaPlanningViewModel vm) return;

        OkButton.IsEnabled = false;
        ErrorText.IsVisible = false;
        try
        {
            if (await vm.TryRegisterGeometryAsync())
            {
                Close(true);
                return;
            }
            ErrorText.Text = vm.StatusMessage ?? "선창 등록에 실패했습니다.";
            ErrorText.IsVisible = true;
        }
        finally
        {
            OkButton.IsEnabled = true;
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
