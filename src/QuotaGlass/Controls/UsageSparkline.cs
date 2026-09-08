using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;

namespace QuotaGlass.Controls;

public sealed class UsageSparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty =
        DependencyProperty.Register(
            nameof(Values),
            typeof(IReadOnlyList<double>),
            typeof(UsageSparkline),
            new FrameworkPropertyMetadata(
                Array.Empty<double>(),
                FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty =
        DependencyProperty.Register(
            nameof(Stroke),
            typeof(Brush),
            typeof(UsageSparkline),
            new FrameworkPropertyMetadata(
                Brushes.White,
                FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BaselineBrushProperty =
        DependencyProperty.Register(
            nameof(BaselineBrush),
            typeof(Brush),
            typeof(UsageSparkline),
            new FrameworkPropertyMetadata(
                Brushes.Transparent,
                FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty =
        DependencyProperty.Register(
            nameof(StrokeThickness),
            typeof(double),
            typeof(UsageSparkline),
            new FrameworkPropertyMetadata(
                1.4,
                FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SecondaryValuesProperty =
        DependencyProperty.Register(
            nameof(SecondaryValues),
            typeof(IReadOnlyList<double>),
            typeof(UsageSparkline),
            new FrameworkPropertyMetadata(
                Array.Empty<double>(),
                FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SecondaryStrokeProperty =
        DependencyProperty.Register(
            nameof(SecondaryStroke),
            typeof(Brush),
            typeof(UsageSparkline),
            new FrameworkPropertyMetadata(
                Brushes.White,
                FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double> Values
    {
        get => (IReadOnlyList<double>)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public Brush BaselineBrush
    {
        get => (Brush)GetValue(BaselineBrushProperty);
        set => SetValue(BaselineBrushProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public IReadOnlyList<double> SecondaryValues
    {
        get => (IReadOnlyList<double>)GetValue(SecondaryValuesProperty);
        set => SetValue(SecondaryValuesProperty, value);
    }

    public Brush SecondaryStroke
    {
        get => (Brush)GetValue(SecondaryStrokeProperty);
        set => SetValue(SecondaryStrokeProperty, value);
    }

    protected override WpfSize MeasureOverride(WpfSize availableSize) =>
        new(
            double.IsInfinity(availableSize.Width)
                ? 80
                : Math.Max(0, availableSize.Width),
            14);

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var baselinePen = new Pen(BaselineBrush, 1);
        drawingContext.DrawLine(
            baselinePen,
            new WpfPoint(0, height - 1),
            new WpfPoint(width, height - 1));

        DrawSeries(drawingContext, Values, Stroke, width, height);
        DrawSeries(
            drawingContext,
            SecondaryValues,
            SecondaryStroke,
            width,
            height);
    }

    private void DrawSeries(
        DrawingContext drawingContext,
        IReadOnlyList<double> values,
        Brush stroke,
        double width,
        double height)
    {
        if (values.Count == 0)
        {
            return;
        }

        var pen = new Pen(stroke, StrokeThickness)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };

        if (values.Count == 1)
        {
            var point = ToPoint(values[0], width / 2, width, height);
            drawingContext.DrawEllipse(
                stroke,
                null,
                point,
                Math.Max(1, StrokeThickness),
                Math.Max(1, StrokeThickness));
            return;
        }

        var previous = ToPoint(values[0], 0, width, height);
        for (var index = 1; index < values.Count; index++)
        {
            var current = ToPoint(
                values[index],
                index * (width - 1) / (values.Count - 1),
                width,
                height);
            drawingContext.DrawLine(pen, previous, current);
            previous = current;
        }
    }

    private static WpfPoint ToPoint(
        double value,
        double x,
        double width,
        double height)
    {
        var usableHeight = Math.Max(1, height - 2);
        var normalized = Math.Clamp(value, 0, 100) / 100d;
        return new WpfPoint(
            Math.Clamp(x, 0, Math.Max(0, width)),
            height - 1 - normalized * usableHeight);
    }
}
