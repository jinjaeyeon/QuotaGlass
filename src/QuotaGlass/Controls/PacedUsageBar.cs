using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;

namespace QuotaGlass.Controls;

public sealed class PacedUsageBar : FrameworkElement
{
    private static readonly Brush DefaultTrack = CreateBrush("#28FFFFFF");
    private static readonly Brush DefaultSafe = CreateBrush("#FF78D6A3");
    private static readonly Brush DefaultWarning = CreateBrush("#FFFF7185");
    private static readonly Brush DefaultMarker = CreateBrush("#FFF7F8FA");

    public static readonly DependencyProperty RemainingRatioProperty =
        DependencyProperty.Register(
            nameof(RemainingRatio),
            typeof(double),
            typeof(PacedUsageBar),
            new FrameworkPropertyMetadata(
                0d,
                FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SafeRemainingRatioProperty =
        DependencyProperty.Register(
            nameof(SafeRemainingRatio),
            typeof(double),
            typeof(PacedUsageBar),
            new FrameworkPropertyMetadata(
                0d,
                FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsWarningProperty =
        DependencyProperty.Register(
            nameof(IsWarning),
            typeof(bool),
            typeof(PacedUsageBar),
            new FrameworkPropertyMetadata(
                false,
                FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty =
        RegisterBrush(nameof(TrackBrush), DefaultTrack);

    public static readonly DependencyProperty SafeBrushProperty =
        RegisterBrush(nameof(SafeBrush), DefaultSafe);

    public static readonly DependencyProperty WarningBrushProperty =
        RegisterBrush(nameof(WarningBrush), DefaultWarning);

    public static readonly DependencyProperty MarkerBrushProperty =
        RegisterBrush(nameof(MarkerBrush), DefaultMarker);

    public double RemainingRatio
    {
        get => (double)GetValue(RemainingRatioProperty);
        set => SetValue(RemainingRatioProperty, value);
    }

    public double SafeRemainingRatio
    {
        get => (double)GetValue(SafeRemainingRatioProperty);
        set => SetValue(SafeRemainingRatioProperty, value);
    }

    public bool IsWarning
    {
        get => (bool)GetValue(IsWarningProperty);
        set => SetValue(IsWarningProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush SafeBrush
    {
        get => (Brush)GetValue(SafeBrushProperty);
        set => SetValue(SafeBrushProperty, value);
    }

    public Brush WarningBrush
    {
        get => (Brush)GetValue(WarningBrushProperty);
        set => SetValue(WarningBrushProperty, value);
    }

    public Brush MarkerBrush
    {
        get => (Brush)GetValue(MarkerBrushProperty);
        set => SetValue(MarkerBrushProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(
            double.IsInfinity(availableSize.Width)
                ? 320
                : Math.Max(0, availableSize.Width),
            30);

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var width = Math.Max(0, ActualWidth);
        var height = Math.Max(0, ActualHeight);
        if (width < 2 || height < 2)
        {
            return;
        }

        var markerRadius = Math.Clamp(height * 0.14, 2.5, 4);
        var horizontalInset = markerRadius + 2;
        var drawableWidth = Math.Max(0, width - (horizontalInset * 2));
        var y = markerRadius + 2;
        var barHeight = Math.Max(4, height - y - 3);
        var radius = Math.Min(5, barHeight / 2);
        var remaining = Math.Clamp(RemainingRatio, 0, 1);
        var safeRemaining = Math.Clamp(SafeRemainingRatio, 0, 1);

        drawingContext.DrawRoundedRectangle(
            TrackBrush,
            null,
            new Rect(horizontalInset, y, drawableWidth, barHeight),
            radius,
            radius);

        if (remaining > 0)
        {
            var remainingWidth = drawableWidth * remaining;
            var remainingRadius = Math.Min(radius, remainingWidth / 2);
            drawingContext.DrawRoundedRectangle(
                IsWarning ? WarningBrush : SafeBrush,
                null,
                new Rect(
                    horizontalInset,
                    y,
                    remainingWidth,
                    barHeight),
                remainingRadius,
                remainingRadius);
        }

        var markerX = horizontalInset + (drawableWidth * safeRemaining);
        var markerPen = new Pen(
            IsWarning ? WarningBrush : MarkerBrush,
            2);
        markerPen.Freeze();

        drawingContext.DrawLine(
            markerPen,
            new Point(markerX, 0.5),
            new Point(markerX, height - 1));

        var diamond = new StreamGeometry();
        using (var context = diamond.Open())
        {
            context.BeginFigure(new Point(markerX, 0), true, true);
            context.LineTo(
                new Point(markerX + markerRadius, markerRadius),
                true,
                false);
            context.LineTo(
                new Point(markerX, markerRadius * 2),
                true,
                false);
            context.LineTo(
                new Point(markerX - markerRadius, markerRadius),
                true,
                false);
        }

        diamond.Freeze();
        drawingContext.DrawGeometry(
            IsWarning ? WarningBrush : MarkerBrush,
            null,
            diamond);
    }

    private static DependencyProperty RegisterBrush(
        string name,
        Brush defaultValue) =>
        DependencyProperty.Register(
            name,
            typeof(Brush),
            typeof(PacedUsageBar),
            new FrameworkPropertyMetadata(
                defaultValue,
                FrameworkPropertyMetadataOptions.AffectsRender));

    private static Brush CreateBrush(string color)
    {
        var brush = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
