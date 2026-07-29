using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Media.Media3D;
using HD.Acs.UI.ViewModels;

namespace HD.Acs.UI.Views;

/// <summary>
/// 화물창 좌측 뷰. 3D 로봇 마커 위치는 XAML 바인딩이 까다로워 코드비하인드에서 VM 변화에 반응해 갱신한다.
/// 3D 씬(placeholder 셸)과 전개도는 동일한 벽면 코드/좌표계(TankLayout)를 공유한다.
/// </summary>
public partial class TankView : UserControl
{
    private TankViewModel? _vm;

    public TankView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = e.NewValue as TankViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmPropertyChanged;
        UpdateRobotMarker();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TankViewModel.RobotX)
            or nameof(TankViewModel.RobotY)
            or nameof(TankViewModel.HasRobotPosition))
            UpdateRobotMarker();
    }

    private void UpdateRobotMarker()
    {
        if (_vm is null) return;
        // 로봇 월드 좌표를 3D 씬 좌표에 직접 매핑(placeholder). 실제 좌표 캘리브레이션은 후속.
        double x = _vm.RobotX ?? 0;
        double y = _vm.RobotY ?? 0;
        RobotMarker.Transform = new TranslateTransform3D(x, y, 0);
    }
}
