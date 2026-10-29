using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ScottPlot.Avalonia;
using Tessera.Core;
using SP = ScottPlot;

namespace Tessera.App.Views;

/// <summary>Builds the control that draws a visual's query result.</summary>
public static class ChartRenderer
{
    static readonly SP.Color[] Palette =
        new[] { "#0E7C66", "#E0A526", "#3B6FB6", "#D2553F", "#7A5BA6", "#4FA3A5", "#B5651D", "#8C8C3A", "#C2577F", "#5E7D2E", "#6E7F99", "#A0522D" }
            .Select(SP.Color.FromHex).ToArray();

    // The theme's text and background for the chart being built; charts are built one at a time on the UI thread.
    static SP.Color Ink = SP.Color.FromHex("#55554F");
    static SP.Color GridLine = SP.Color.FromHex("#EDEDE9");
    static SP.Color Paper = SP.Colors.White;
    static IBrush Text = new SolidColorBrush(Color.Parse("#252423"));

    /// <param name="style">Imported formatting; its accent colours the first series.</param>
    /// <param name="theme">The report theme: series colours, text and the visual's background.</param>
    public static Control Build(VisualType type, QueryResult r, VisualStyle? style = null, ReportTheme? theme = null)
    {
        var background = style?.Background ?? theme?.Background ?? "#FFFFFF";
        Paper = SP.Color.FromHex(background);
        var fore = SP.Color.FromHex(theme?.Foreground ?? "#252423");
        Ink = fore.WithAlpha(0.8);
        GridLine = fore.WithAlpha(0.1);
        Text = new SolidColorBrush(Color.Parse(theme?.Foreground ?? "#252423"));
        if (r.Rows.Count == 0) return Message("No data");
        var themed = theme?.DataColors is { Count: > 0 } colors ? colors.Select(SP.Color.FromHex).ToArray() : Palette;
        var palette = style?.Accent is { } accent ? [SP.Color.FromHex(accent), .. themed] : themed;
        return type switch
        {
            VisualType.Table => GridBuilder.Create(r),
            VisualType.Card => Card(r),
            VisualType.PieChart => Pie(r, palette, donut: false),
            VisualType.DonutChart => Pie(r, palette, donut: true),
            VisualType.LineChart => Line(r, palette, area: false),
            VisualType.AreaChart => Line(r, palette, area: true),
            VisualType.BarChart => Bars(r, palette, horizontal: true, style?.HideValueAxis == true),
            _ => Bars(r, palette, horizontal: false, style?.HideValueAxis == true),
        };
    }

    /// <summary>Formats a value column the way its measure asks, shortened for labels and ticks.</summary>
    static Func<double, string> Formatter(QueryResult r, int column) => v => Sql.FormatNumber(v, r.FormatOf(column), compact: true);

    static Control Message(string text) => new TextBlock
    {
        Text = text,
        Classes = { "muted" },
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    static Control Card(QueryResult r)
    {
        var value = r.Rows[0][r.CategoryCount];
        var text = Sql.ToDouble(value) is { } d && value is not string ? Formatter(r, r.CategoryCount)(d) : Sql.Format(value);
        var big = new TextBlock
        {
            Text = text,
            Foreground = Text,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var label = new TextBlock { Text = r.Columns[r.CategoryCount], Classes = { "muted" }, HorizontalAlignment = HorizontalAlignment.Center };
        var host = new Panel
        {
            Children = { new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2, Children = { big, label } } },
        };
        // Scale the number with the card.
        host.SizeChanged += (_, e) => big.FontSize = Math.Clamp(Math.Min(e.NewSize.Height * 0.4, e.NewSize.Width / 6), 18, 72);
        return host;
    }

    static string[] Labels(QueryResult r) =>
        r.Rows.Select(row => r.CategoryCount == 0 ? ""
            : string.Join(" · ", row.Take(r.CategoryCount).Select(v => v is null ? "(Blank)" : Sql.Format(v)))).ToArray();

    static (int Index, string Name)[] Series(QueryResult r) =>
        Enumerable.Range(r.CategoryCount, r.Columns.Count - r.CategoryCount).Select(i => (i, r.Columns[i])).ToArray();

    static AvaPlot NewPlot()
    {
        var ap = new AvaPlot();
        ap.UserInputProcessor.Disable();
        var plt = ap.Plot;
        plt.FigureBackground.Color = Paper;
        plt.DataBackground.Color = Paper;
        plt.Axes.Color(Ink);
        plt.Grid.MajorLineColor = GridLine;
        plt.Axes.Top.FrameLineStyle.Width = 0;
        plt.Axes.Right.FrameLineStyle.Width = 0;
        foreach (var axis in new SP.IAxis[] { plt.Axes.Left, plt.Axes.Bottom })
            axis.TickLabelStyle.FontSize = 11;
        return ap;
    }

    static void Legend(SP.Plot plt, IEnumerable<(string Label, SP.Color Color)> items)
    {
        plt.Legend.ManualItems.Clear();
        foreach (var (label, color) in items)
            plt.Legend.ManualItems.Add(new SP.LegendItem { LabelText = label, FillColor = color });
        plt.Legend.FontSize = 11;
        plt.Legend.FontColor = Ink;
        plt.Legend.BackgroundColor = Paper;
        plt.ShowLegend(SP.Alignment.UpperRight);
    }

    /// <param name="labelsOnly">Data labels without a value axis or gridlines, like Power BI's "Data labels" style.</param>
    static Control Bars(QueryResult r, SP.Color[] palette, bool horizontal, bool labelsOnly = false)
    {
        var ap = NewPlot();
        var plt = ap.Plot;
        var labels = Labels(r);
        var series = Series(r);
        var n = labels.Length;
        var slot = 0.8 / series.Length;
        // Label each bar when there's room, as Power BI's data labels do.
        var labelled = series.Length == 1 && (labelsOnly || n <= (horizontal ? 20 : 12));
        var format = Formatter(r, series[0].Index);
        var labelFormat = Sql.UnitFormatter(r.Rows.Select(row => Sql.ToDouble(row[series[0].Index]) ?? 0), r.FormatOf(series[0].Index));

        var bars = new List<SP.Bar>();
        for (var i = 0; i < n; i++)
        {
            // Bar charts list the largest value at the top.
            var pos = horizontal ? n - 1 - i : i;
            for (var s = 0; s < series.Length; s++)
            {
                var value = Sql.ToDouble(r.Rows[i][series[s].Index]) ?? 0;
                bars.Add(new SP.Bar
                {
                    Position = pos - 0.4 + slot * (s + 0.5),
                    Value = value,
                    Size = slot * 0.9,
                    FillColor = palette[s % palette.Length],
                    LineWidth = 0,
                    Label = labelled ? labelFormat(value) : "",
                });
            }
        }
        var plot = plt.Add.Bars(bars);
        plot.Horizontal = horizontal;
        plot.ValueLabelStyle.FontSize = 10;
        plot.ValueLabelStyle.ForeColor = Ink;

        var positions = Enumerable.Range(0, n).Select(i => (double)(horizontal ? n - 1 - i : i)).ToArray();
        var compact = new SP.TickGenerators.NumericAutomatic { LabelFormatter = format };
        if (horizontal)
        {
            plt.Axes.Left.SetTicks(positions, labels);
            plt.Axes.Bottom.TickGenerator = compact;
            plt.Axes.Margins(left: 0, right: labelled ? 0.25 : 0.05, bottom: 0.02, top: 0.02);
        }
        else
        {
            plt.Axes.Bottom.SetTicks(positions, labels);
            plt.Axes.Left.TickGenerator = compact;
            plt.Axes.Margins(left: 0.02, right: 0.02, bottom: 0, top: labelled ? 0.14 : 0.08);
            if (n > 6 || labels.Any(l => l.Length > 10))
            {
                plt.Axes.Bottom.TickLabelStyle.Rotation = -40;
                plt.Axes.Bottom.TickLabelStyle.Alignment = SP.Alignment.MiddleRight;
            }
        }
        plt.Grid.XAxisStyle.MajorLineStyle.Width = horizontal && !labelsOnly ? 1 : 0;
        plt.Grid.YAxisStyle.MajorLineStyle.Width = horizontal || labelsOnly ? 0 : 1;
        if (labelsOnly)
        {
            SP.IAxis valueAxis = horizontal ? plt.Axes.Bottom : plt.Axes.Left;
            valueAxis.IsVisible = false;
            // The category axis keeps its labels but loses the line and ticks.
            SP.IAxis categoryAxis = horizontal ? plt.Axes.Left : plt.Axes.Bottom;
            categoryAxis.FrameLineStyle.Width = 0;
            categoryAxis.MajorTickStyle.Length = 0;
        }

        if (series.Length > 1) Legend(plt, series.Select((s, i) => (s.Name, palette[i % palette.Length])));
        ap.Refresh();
        return ap;
    }

    static Control Line(QueryResult r, SP.Color[] palette, bool area)
    {
        var ap = NewPlot();
        var plt = ap.Plot;
        var series = Series(r);
        var n = r.Rows.Count;

        var isDate = r.CategoryCount == 1 && r.Rows.All(row => row[0] is null || Sql.ToDateTime(row[0]) is not null && row[0] is not string);
        double[] xs;
        if (isDate)
        {
            xs = r.Rows.Select(row => Sql.ToDateTime(row[0])?.ToOADate() ?? double.NaN).ToArray();
            plt.Axes.DateTimeTicksBottom();
        }
        else
        {
            xs = Enumerable.Range(0, n).Select(i => (double)i).ToArray();
            var labels = Labels(r);
            var step = Math.Max(1, (int)Math.Ceiling(n / 6.0));
            var shown = Enumerable.Range(0, n).Where(i => i % step == 0).ToArray();
            plt.Axes.Bottom.SetTicks(shown.Select(i => xs[i]).ToArray(), shown.Select(i => labels[i]).ToArray());
        }

        for (var s = 0; s < series.Length; s++)
        {
            var pts = Enumerable.Range(0, n)
                .Select(i => (X: xs[i], Y: Sql.ToDouble(r.Rows[i][series[s].Index])))
                .Where(p => p.Y is not null && !double.IsNaN(p.X))
                .ToArray();
            var line = plt.Add.Scatter(pts.Select(p => p.X).ToArray(), pts.Select(p => p.Y!.Value).ToArray());
            line.Color = palette[s % palette.Length];
            line.LineWidth = 2;
            if (area)
            {
                // Power BI's area chart: the line with a light fill down to zero.
                line.FillY = true;
                line.FillYColor = line.Color.WithAlpha(0.25);
            }
            line.MarkerSize = n <= 40 ? 5 : 0;
        }

        plt.Axes.Left.TickGenerator = new SP.TickGenerators.NumericAutomatic { LabelFormatter = Formatter(r, r.CategoryCount) };
        plt.Grid.XAxisStyle.MajorLineStyle.Width = 0;
        if (series.Length > 1) Legend(plt, series.Select((s, i) => (s.Name, palette[i % palette.Length])));
        ap.Refresh();
        return ap;
    }

    static Control Pie(QueryResult r, SP.Color[] palette, bool donut)
    {
        var labels = Labels(r);
        var slices = r.Rows
            .Select((row, i) => (Label: labels[i] == "" ? r.Columns[r.CategoryCount] : labels[i], Value: Sql.ToDouble(row[r.CategoryCount]) ?? 0))
            .Where(s => s.Value > 0)
            .ToList();
        if (slices.Count == 0) return Message("Nothing to show: pie charts need positive values");

        var ap = NewPlot();
        var plt = ap.Plot;
        var pie = plt.Add.Pie(slices.Select(s => s.Value).ToArray());
        pie.DonutFraction = donut ? 0.5 : 0;
        pie.LineWidth = 1.5f;
        pie.LineColor = SP.Colors.White;
        for (var i = 0; i < slices.Count; i++)
        {
            pie.Slices[i].FillColor = palette[i % palette.Length];
            pie.Slices[i].LegendText = slices[i].Label;
        }
        plt.Axes.Frameless();
        plt.HideGrid();
        plt.Legend.FontSize = 11;
        plt.Legend.FontColor = Ink;
        plt.Legend.BackgroundColor = Paper;
        plt.ShowLegend(SP.Edge.Right);
        ap.Refresh();
        return ap;
    }
}
