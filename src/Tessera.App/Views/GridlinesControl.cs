using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Tessera.App.Views;

/// <summary>View › Gridlines: Power BI's dotted grid across the page, to line visuals up against.</summary>
public sealed class GridlinesControl : Control
{
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<GridlinesControl, double>(nameof(Spacing), 16);

    static GridlinesControl() => AffectsRender<GridlinesControl>(SpacingProperty);

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    static readonly IPen Line = new Pen(new SolidColorBrush(Color.Parse("#D6D6D6")), 1, new DashStyle([1, 3], 0));

    public override void Render(DrawingContext context)
    {
        var step = Math.Max(4, Spacing);
        var size = Bounds.Size;
        for (var x = step; x < size.Width; x += step)
            context.DrawLine(Line, new Point(x + 0.5, 0), new Point(x + 0.5, size.Height));
        for (var y = step; y < size.Height; y += step)
            context.DrawLine(Line, new Point(0, y + 0.5), new Point(size.Width, y + 0.5));
    }
}
