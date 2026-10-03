using Avalonia.Controls;
using Avalonia.Threading;
using HD.Acs.UI.ViewModels;

namespace HD.Acs.UI.Desktop.Views;

public partial class AreaManagementView : UserControl
{
    private PlanningAssistantViewModel? _assistant;

    public AreaManagementView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_assistant is not null) _assistant.Lines.CollectionChanged -= OnLinesChanged;
            _assistant = (DataContext as AreaPlanningViewModel)?.Assistant;
            if (_assistant is null) return;
            _assistant.Lines.CollectionChanged += OnLinesChanged;
            _ = _assistant.CheckStatusAsync();   // 어시스턴트 연결 상태 안내(비활성·모델 미설치 등)
        };
    }

    // 새 대화가 붙으면 맨 아래로 스크롤
    private void OnLinesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        Dispatcher.UIThread.Post(() => ChatScroll.ScrollToEnd(), DispatcherPriority.Background);
}
