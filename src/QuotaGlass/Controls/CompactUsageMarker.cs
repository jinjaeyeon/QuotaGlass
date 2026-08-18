using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;

namespace QuotaGlass.Controls;

public sealed class CompactUsageMarker : FrameworkElement
{
    private static readonly Brush DefaultMarker =
        new SolidColorBrush(Color.FromRgb(247, 248, 250));
    private static readonly Brush DefaultWarning =
        new SolidColorBrush(Color.FromRgb(255, 113, 133));

    public static readonly DependencyProperty PositionRatioProperty =
        DependencyProperty.Register(
            nameof(PositionRatio),
            typeof(double),
            typeof(CompactUsageMarker),
            new FrameworkPropertyMetadata(
                0d,
                FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsWarningProperty =
        DependencyProperty.Register(
            nameof(IsWarning),
            typeof(bool),
            typeof(CompactUsageMarker),
            new FrameworkPropertyMetadata(
                false,
                FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MarkerBrushProperty =
        RegisterBrush(nameof(MarkerBrush), DefaultMarker);

    public static readonly DependencyProperty WarningBrushProperty =
        RegisterBrush(nameof(WarningBrush), DefaultWarning);

    public double PositionRatio
    {
        get => (double)GetValue(PositionRatioProperty);
        set => SetValue(PositionRatioProperty, value);
    }

    public bool IsWarning
    {
        get => (bool)GetValue(IsWarningProperty);
        set => SetValue(IsWarningProperty, value);
    }

    public Brush MarkerBrush
    {
        get => (Brush)GetValue(MarkerBrushProperty);
        set => SetValue(MarkerBrushProperty, value);
    }

    public Brush WarningBrush
    {
        get => (Brush)GetValue(WarningBrushProperty);
        set => SetValue(WarningBrushProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(
            double.IsInfinity(availableSize.Width)
                ? 120
                : Math.Max(0, availableSize.Width),
            17);

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var width = Math.Max(0, ActualWidth);
        var height = Math.Max(0, ActualHeight);
        if (width < 2 || height < 2)
        {
            return;
        }

        const double markerWidth = 1;
        const double horizontalInset = 1;
        var drawableWidth = Math.Max(0, width - horizontalInset * 2);
        var ratio = Math.Clamp(PositionRatio, 0, 1);
        var markerX = horizontalInset + drawableWidth * ratio;
        var markerHeight = Math.Min(11, Math.Max(4, height - 4));
        var markerY = (height - markerHeight) / 2;

        drawingContext.DrawRoundedRectangle(
            IsWarning ? WarningBrush : MarkerBrush,
            null,
            new Rect(
                markerX - markerWidth / 2,
                markerY,
                markerWidth,
                markerHeight),
            markerWidth / 2,
            markerWidth / 2);
    }

    private static DependencyProperty RegisterBrush(
        string name,
        Brush defaultValue) =>
        DependencyProperty.Register(
            name,
            typeof(Brush),
            typeof(CompactUsageMarker),
            new FrameworkPropertyMetadata(
                defaultValue,
                FrameworkPropertyMetadataOptions.AffectsRender));
}
