using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Tessera.Core;

namespace Tessera.App.Views;

/// <summary>A theme's preview in the Design tab's gallery: "Aa" and a row of bars in its colours, on its background.</summary>
public sealed class ThemeSwatch : Control
{
    public static readonly StyledProperty<ReportTheme?> ThemeProperty =
        AvaloniaProperty.Register<ThemeSwatch, ReportTheme?>(nameof(Theme));

    static ThemeSwatch() => AffectsRender<ThemeSwatch>(ThemeProperty);

    public ReportTheme? Theme
    {
        get => GetValue(ThemeProperty);
        set => SetValue(ThemeProperty, value);
    }

    static readonly double[] Heights = [0.55, 0.8, 0.4, 0.65, 0.5, 0.9];

    public override void Render(DrawingContext context)
    {
        if (Theme is not { } theme) return;
        var size = Bounds.Size;
        var background = theme.PageBackground ?? theme.Background;
        context.FillRectangle(Brush.Parse(background), new Rect(size), 3);
        var text = new FormattedText("Aa", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            Typeface.Default, 10, Brush.Parse(theme.Foreground));
        context.DrawText(text, new Point(5, 3));
        var bars = Math.Min(Heights.Length, theme.DataColors.Count);
        var width = (size.Width - 10) / Math.Max(1, bars) - 2;
        var floor = size.Height - 5;
        for (var i = 0; i < bars; i++)
        {
            var h = (size.Height - 20) * Heights[i];
            context.FillRectangle(Brush.Parse(theme.DataColors[i]), new Rect(5 + i * (width + 2), floor - h, width, h));
        }
    }
}
