// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;

namespace CMUmcl;

internal sealed class FolderCardsPanel : Panel
{
    private const double CardWidth = 220, Gap = 12;
    private int Columns(double width) => Math.Max(1, Math.Min(InternalChildren.Count, (int)Math.Floor((width + Gap) / (CardWidth + Gap))));
    protected override Size MeasureOverride(Size availableSize)
    {
        if (InternalChildren.Count == 0) return new Size();
        double width = double.IsInfinity(availableSize.Width) ? CardWidth : availableSize.Width;
        int columns = Columns(width); double cellWidth = Math.Max(0, (width - Gap * (columns - 1)) / columns);
        double height = 0;
        foreach (UIElement child in InternalChildren) { child.Measure(new Size(cellWidth, double.PositiveInfinity)); height = Math.Max(height, child.DesiredSize.Height); }
        int rows = (InternalChildren.Count + columns - 1) / columns;
        return new Size(width, rows * height + (rows - 1) * Gap);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (InternalChildren.Count == 0) return finalSize;
        int columns = Columns(finalSize.Width); double width = Math.Max(0, (finalSize.Width - Gap * (columns - 1)) / columns);
        double height = InternalChildren.Cast<UIElement>().Max(child => child.DesiredSize.Height);
        for (int i = 0; i < InternalChildren.Count; i++) InternalChildren[i].Arrange(new Rect((i % columns) * (width + Gap), (i / columns) * (height + Gap), width, height));
        return finalSize;
    }
}
