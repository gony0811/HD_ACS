using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using HD.Acs.UI.ViewModels;

namespace HD.Acs.UI.Desktop.Views;

/// <summary>계획 › 리치·간섭. 탭을 처음 열 때 선창 면을 읽고 조사를 시작한다(조사는 서버 계산이라 미리 돌리지 않는다).</summary>
public partial class ReachSimView : UserControl
{
    private bool _loaded;

    public ReachSimView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => { if (IsAttachedToVisualTree()) LoadOnce(); };
    }

    private bool IsAttachedToVisualTree() => this.GetVisualRoot() is not null;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        LoadOnce();
    }

    private void LoadOnce()
    {
        if (_loaded || DataContext is not ReachSimViewModel vm) return;
        _loaded = true;
        _ = vm.LoadAsync();
    }

    private void OnPresetSection(object? sender, RoutedEventArgs e) => View3D.ApplyPreset(ReachSim3DControl.Preset.Section);
    private void OnPresetFront(object? sender, RoutedEventArgs e) => View3D.ApplyPreset(ReachSim3DControl.Preset.Front);
    private void OnPresetTop(object? sender, RoutedEventArgs e) => View3D.ApplyPreset(ReachSim3DControl.Preset.Top);
    private void OnPresetIso(object? sender, RoutedEventArgs e) => View3D.ApplyPreset(ReachSim3DControl.Preset.Iso);
}
