using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace IptvPlayer.Controls;


public sealed partial class HubWrapPanel : Panel
{

    // Gap between items
    public double Spacing { get; set; } = 24;

    // Measure with line wrapping
    protected override Size MeasureOverride(Size availableSize)
    {
        var constraintWidth = double.IsFinite(availableSize.Width) ? availableSize.Width : double.PositiveInfinity;
        double lineW = 0, lineH = 0, totalH = 0, maxW = 0;

        foreach (FrameworkElement child in Children)
        {
            child.Measure(availableSize);
            var w = child.DesiredSize.Width;
            var h = child.DesiredSize.Height;

            if (lineW > 0 && lineW + Spacing + w > constraintWidth)
            {
                maxW = Math.Max(maxW, lineW);
                totalH += lineH + Spacing;
                lineW = 0;
                lineH = 0;
            }

            lineW += (lineW > 0 ? Spacing : 0) + w;
            lineH = Math.Max(lineH, h);
        }

        maxW = Math.Max(maxW, lineW);
        totalH += lineH;

        return new Size(
            double.IsFinite(constraintWidth) ? Math.Min(maxW, constraintWidth) : maxW,
            totalH);
    }

    // Arrange wrapped lines
    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, lineH = 0;
        var start = 0;

        for (var i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            var w = child.DesiredSize.Width;

            if (x > 0 && x + Spacing + w > finalSize.Width)
            {
                CenterLine(start, i, y, lineH, finalSize.Width);
                y += lineH + Spacing;
                x = 0;
                lineH = 0;
                start = i;
            }

            x += (x > 0 ? Spacing : 0) + w;
            lineH = Math.Max(lineH, child.DesiredSize.Height);
        }

        if (start < Children.Count)
        {
            CenterLine(start, Children.Count, y, lineH, finalSize.Width);
        }

        return finalSize;
    }


    // Center one line of items
    private void CenterLine(int from, int to, double y, double lineH, double finalWidth)
    {
        double lineWidth = 0;
        for (var i = from; i < to; i++)
        {
            lineWidth += Children[i].DesiredSize.Width;
        }
        lineWidth += Spacing * (to - from - 1);

        var x = Math.Max(0, (finalWidth - lineWidth) / 2);
        for (var i = from; i < to; i++)
        {
            Children[i].Arrange(new Rect(x, y, Children[i].DesiredSize.Width, lineH));
            x += Children[i].DesiredSize.Width + Spacing;
        }
    }
}
