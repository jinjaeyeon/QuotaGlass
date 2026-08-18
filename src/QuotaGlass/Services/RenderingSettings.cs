using System.Windows;
using System.Windows.Media;

namespace QuotaGlass.Services;

internal static class RenderingSettings
{
    public static void Apply(
        DependencyObject target,
        bool antiAliasingEnabled)
    {
        RenderOptions.SetEdgeMode(
            target,
            antiAliasingEnabled
                ? EdgeMode.Unspecified
                : EdgeMode.Aliased);
        RenderOptions.SetBitmapScalingMode(
            target,
            antiAliasingEnabled
                ? BitmapScalingMode.Unspecified
                : BitmapScalingMode.NearestNeighbor);
        TextOptions.SetTextRenderingMode(
            target,
            antiAliasingEnabled
                ? TextRenderingMode.Auto
                : TextRenderingMode.Aliased);
    }

    public static void ApplyToVisualTree(
        DependencyObject root,
        bool antiAliasingEnabled)
    {
        Apply(root, antiAliasingEnabled);

        for (var index = 0;
             index < VisualTreeHelper.GetChildrenCount(root);
             index++)
        {
            ApplyToVisualTree(
                VisualTreeHelper.GetChild(root, index),
                antiAliasingEnabled);
        }
    }
}
