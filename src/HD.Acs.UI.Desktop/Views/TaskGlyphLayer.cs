using System.Collections.Specialized;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using HD.Acs.UI.Primitives;
using HD.Acs.UI.Rendering;
using HD.Acs.UI.ViewModels;

namespace HD.Acs.UI.Desktop.Views;

/// <summary>
/// 계획 전개도의 작업(용접선) 레이어 — <see cref="TaskSeg"/> 목록을 DrawingContext로 그린다.
/// LINE은 종전 표시(주황 선·끝점·seq 배지)와 같고, CROSS3/4는 <see cref="SeamGlyph"/> 규칙으로 T/十자·▲/■·회전 라벨.
/// 클릭은 아래 캔버스로 통과(IsHitTestVisible=false — 4점·교차 그리기 픽 유지).
/// </summary>
public sealed class TaskGlyphLayer : Control
{
    public static readonly StyledProperty<IEnumerable<TaskSeg>?> TasksProperty =
        AvaloniaProperty.Register<TaskGlyphLayer, IEnumerable<TaskSeg>?>(nameof(Tasks));

    public IEnumerable<TaskSeg>? Tasks
    {
        get => GetValue(TasksProperty);
        set => SetValue(TasksProperty, value);
    }

    private static readonly Rgba LineColor = Rgba.FromRgb(0xE6, 0x7E, 0x22);      // 계획 용접선 주황(종전과 동일)
    private static readonly Rgba EndColor = Rgba.FromRgb(0xCA, 0x6F, 0x1E);
    private static readonly Rgba MissingColor = Rgba.FromRgb(0xE7, 0x4C, 0x3C);   // 가지 미지정(점선)
    private static readonly IImmutableBrush BadgeBg = new ImmutableSolidColorBrush(Color.Parse("#FEF9E7"));
    private static readonly IPen BadgeBorder = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#B7950B")), 1);
    private static readonly IImmutableBrush BadgeText = new ImmutableSolidColorBrush(Color.Parse("#7D6608"));
    private static readonly Typeface BadgeFace = new("Segoe UI, Apple SD Gothic Neo, Malgun Gothic, Noto Sans CJK KR, sans-serif", FontStyle.Normal, FontWeight.Bold);

    private INotifyCollectionChanged? _subscribed;

    public TaskGlyphLayer() => IsHitTestVisible = false;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != TasksProperty) return;
        if (_subscribed is not null) _subscribed.CollectionChanged -= OnTasksChanged;
        _subscribed = change.NewValue as INotifyCollectionChanged;
        if (_subscribed is not null) _subscribed.CollectionChanged += OnTasksChanged;
        InvalidateVisual();
    }

    private void OnTasksChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext ctx)
    {
        if (Tasks is null) return;
        foreach (var t in Tasks)
        {
            var g = SeamGlyph.Build(t, markerR: 6, arrowLen: 10);
            var color = g.Dashed ? MissingColor : LineColor;
            SeamGlyphPainter.Draw(ctx, g, color, 2.5, 4.5);
            if (t.Kind == SeamKind.Line)
                ctx.DrawEllipse(SeamGlyphPainter.Brush(EndColor), null, new Rect(t.EndX, t.EndY, 8, 8));
            DrawBadge(ctx, t.Badge, g.LabelAnchor);
        }
    }

    private static void DrawBadge(DrawingContext ctx, string text, Pt2 at)
    {
        if (string.IsNullOrEmpty(text)) return;
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, BadgeFace, 10, BadgeText);
        var box = new Rect(at.X, at.Y, ft.Width + 4, ft.Height);
        ctx.DrawRectangle(BadgeBg, BadgeBorder, box, 2, 2);
        ctx.DrawText(ft, new Point(at.X + 2, at.Y));
    }
}
