using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ScottPlot.Avalonia;
using Tessera.App.ViewModels;
using Tessera.Core;
using SP = ScottPlot;

namespace Tessera.App.Views;

/// <summary>Controls for Power BI's AI visuals: the decomposition tree and key influencers.</summary>
public static class AnalysisViews
{
    internal static readonly Color DefaultAccent = Color.Parse("#118DFF");
    internal static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#252423"));
    internal static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#605E5C"));

    public static Control Build(object analysis, VisualViewModel vm) => analysis switch
    {
        Decomposition d => new DecompositionTreeView(d, vm),
        KeyInfluencerAnalysis k => KeyInfluencersView.Create(k, vm),
        _ => new TextBlock { Text = "Nothing to show" },
    };

    internal static Color Accent(VisualViewModel vm) => vm.Style?.Accent is { } a ? Color.Parse(a) : DefaultAccent;

    /// <summary>Blends toward white: Power BI's lighter shade of a theme colour.</summary>
    internal static Color Lighter(Color c, double amount) => Color.FromRgb(
        (byte)(c.R + (255 - c.R) * amount), (byte)(c.G + (255 - c.G) * amount), (byte)(c.B + (255 - c.B) * amount));
}

/// <summary>
/// Power BI's decomposition tree: the total on the left, then one column per level of the expanded path,
/// each a list of bars sized against the node it breaks down. Clicking a bar expands or collapses it.
/// </summary>
public sealed class DecompositionTreeView : Control
{
    const double HeaderHeight = 38, Gap = 34;

    static readonly Typeface Regular = new(FontFamily.Default);
    static readonly Typeface Bold = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    static readonly IBrush Track = new SolidColorBrush(Color.Parse("#E9E9E9"));
    static readonly IPen HeaderRule = new Pen(new SolidColorBrush(Color.Parse("#8A8886")), 1);
    static readonly IPen Connector = new Pen(new SolidColorBrush(Color.Parse("#C8C6C4")), 1);
    static readonly IPen OpenConnector = new Pen(new SolidColorBrush(Color.Parse("#8A8886")), 1.2, new DashStyle([2, 2], 0));

    readonly Decomposition _tree;
    readonly VisualViewModel _vm;
    readonly IBrush _bar;
    readonly List<(Rect Rect, int Level, string? Key)> _targets = [];

    public DecompositionTreeView(Decomposition tree, VisualViewModel vm)
    {
        _tree = tree;
        _vm = vm;
        _bar = new SolidColorBrush(AnalysisViews.Accent(vm));
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    string Amount(double v) => Sql.FormatNumber(v, _tree.Format, compact: false);

    static FormattedText Text(string text, double size, IBrush brush, double maxWidth, bool bold = false) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold ? Bold : Regular, size, brush)
        {
            MaxTextWidth = Math.Max(1, maxWidth), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis,
        };

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        _targets.Clear();
        var (width, height) = (Bounds.Width, Bounds.Height);
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var columns = _tree.Levels.Count + 1;
        var columnWidth = Math.Min(200, (width - 8) / columns);
        var barWidth = Math.Max(30, columnWidth - Gap);
        var most = _tree.Levels.Count == 0 ? 1 : _tree.Levels.Max(l => l.Nodes.Count + (l.Hidden > 0 ? 1 : 0));
        var nodeHeight = Math.Clamp((height - HeaderHeight - 8) / most, 26, 42);

        // The root: the value's name and total.
        var x = 4.0;
        var y = HeaderHeight + (height - HeaderHeight) / 2 - nodeHeight / 2;
        DrawNode(context, x, y, barWidth, nodeHeight, _tree.ValueName, _tree.Total, _tree.Total, bold: true);
        var parentPoint = new Point(x + barWidth, y + nodeHeight - 6);

        for (var level = 0; level < _tree.Levels.Count; level++)
        {
            var l = _tree.Levels[level];
            x = 4 + (level + 1) * columnWidth;

            // Level header: the field, with the open value underneath.
            context.DrawText(Text(l.Field.DisplayName, 11.5, AnalysisViews.Ink, barWidth, bold: true), new Point(x, 4));
            context.DrawLine(HeaderRule, new Point(x, 21), new Point(x + barWidth, 21));
            if (l.Selected >= 0)
                context.DrawText(Text(l.Nodes[l.Selected].Label, 10, AnalysisViews.Muted, barWidth), new Point(x, 23));

            // The level's bars, centred on the node they break down while they fit.
            var rows = l.Nodes.Count + (l.Hidden > 0 ? 1 : 0);
            var block = rows * nodeHeight;
            var top = Math.Clamp(parentPoint.Y - block / 2, HeaderHeight, Math.Max(HeaderHeight, height - block - 4));
            var scale = l.Parent > 0 ? l.Parent : l.Nodes.Count > 0 ? l.Nodes.Max(n => n.Amount) : 1;
            Point? next = null;
            for (var i = 0; i < l.Nodes.Count; i++)
            {
                var n = l.Nodes[i];
                var ny = top + i * nodeHeight;
                if (ny + nodeHeight > height + 2) break;
                var open = i == l.Selected;
                var barY = ny + nodeHeight - 6;
                context.DrawGeometry(null, open ? OpenConnector : Connector, Curve(parentPoint, new Point(x, barY)));
                DrawNode(context, x, ny, barWidth, nodeHeight, n.Label, n.Amount, scale, bold: open);
                _targets.Add((new Rect(x, ny, barWidth, nodeHeight), level, n.Key));
                if (open) next = new Point(x + barWidth, barY);
            }
            if (l.Hidden > 0)
            {
                var more = top + l.Nodes.Count * nodeHeight;
                if (more + 14 < height)
                    context.DrawText(Text($"+{l.Hidden} more", 10, AnalysisViews.Muted, barWidth), new Point(x, more + 2));
            }
            if (next is not { } p) break;
            parentPoint = p;
        }
    }

    void DrawNode(DrawingContext context, double x, double y, double width, double height, string label, double amount, double scale, bool bold)
    {
        var small = height < 32;
        context.DrawText(Text(label, small ? 10 : 11, AnalysisViews.Ink, width, bold), new Point(x, y + (small ? 0 : 2)));
        if (!small) context.DrawText(Text(Amount(amount), 10, AnalysisViews.Muted, width), new Point(x, y + 16));
        var barY = y + height - 9;
        context.FillRectangle(Track, new Rect(x, barY, width, 6));
        var fraction = scale > 0 ? Math.Clamp(amount / scale, 0, 1) : 0;
        context.FillRectangle(_bar, new Rect(x, barY, width * fraction, 6));
    }

    static Geometry Curve(Point from, Point to)
    {
        var mid = (from.X + to.X) / 2;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(from, false);
            c.CubicBezierTo(new Point(mid, from.Y), new Point(mid, to.Y), to);
            c.EndFigure(false);
        }
        return g;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var point = e.GetPosition(this);
        foreach (var (rect, level, key) in _targets)
        {
            if (!rect.Contains(point)) continue;
            _vm.Expand(level, key);
            e.Handled = true;
            return;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        Cursor = _targets.Any(t => t.Rect.Contains(point)) ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
    }
}

/// <summary>
/// Power BI's key influencers: the factors that make the target value more likely, as bubbles sized by
/// how much more likely, and a chart of the selected factor's field.
/// </summary>
public static class KeyInfluencersView
{
    public static Control Create(KeyInfluencerAnalysis a, VisualViewModel vm)
    {
        var accent = AnalysisViews.Accent(vm);
        var light = AnalysisViews.Lighter(accent, 0.6);
        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("5*,6*"), Margin = new Thickness(6, 0, 6, 6) };

        var left = new DockPanel { Margin = new Thickness(0, 0, 10, 0) };
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(0, 0, 0, 8) };
        tabs.Children.Add(new Border
        {
            BorderBrush = AnalysisViews.Ink, BorderThickness = new Thickness(0, 0, 0, 2),
            Child = new TextBlock { Text = "Key influencers", FontSize = 11.5, FontWeight = FontWeight.SemiBold, Foreground = AnalysisViews.Ink },
        });
        var segments = new TextBlock { Text = "Top segments", FontSize = 11.5, Foreground = AnalysisViews.Muted };
        ToolTip.SetTip(segments, "JuiceCI doesn't find top segments yet.");
        tabs.Children.Add(segments);
        DockPanel.SetDock(tabs, Dock.Top);
        left.Children.Add(tabs);

        // "What influences Status to be [Won]?"
        var question = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 0, 8) };
        question.Children.Add(new TextBlock
        {
            Text = $"What influences {a.TargetName} to be", FontSize = 11, Foreground = AnalysisViews.Muted, VerticalAlignment = VerticalAlignment.Center,
        });
        var target = new ComboBox { ItemsSource = a.TargetValues, SelectedItem = a.TargetValue, FontSize = 11, MinWidth = 110, Padding = new Thickness(6, 2) };
        target.SelectionChanged += (_, _) =>
        {
            if (target.SelectedItem is string value && value != a.TargetValue) vm.TargetValue = value;
        };
        question.Children.Add(target);
        DockPanel.SetDock(question, Dock.Top);
        left.Children.Add(question);

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Margin = new Thickness(0, 0, 0, 6) };
        header.Children.Add(new TextBlock { Text = "When...", FontSize = 10.5, Foreground = AnalysisViews.Muted, VerticalAlignment = VerticalAlignment.Bottom });
        var likelihood = new TextBlock
        {
            Text = $"...the likelihood of {a.TargetName} being {a.TargetValue} increases by",
            FontSize = 10.5, Foreground = AnalysisViews.Muted, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Right,
        };
        Grid.SetColumn(likelihood, 1);
        header.Children.Add(likelihood);
        DockPanel.SetDock(header, Dock.Top);
        left.Children.Add(header);

        var detail = new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse("#E1DFDD")), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8),
        };
        Grid.SetColumn(detail, 1);

        if (a.Influencers.Count == 0)
        {
            left.Children.Add(new TextBlock
            {
                Text = $"Nothing stands out: no value of the fields makes {a.TargetName} = {a.TargetValue} noticeably more likely.",
                TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Foreground = AnalysisViews.Muted, Margin = new Thickness(0, 8),
            });
            root.Children.Add(left);
            return root;
        }

        var list = new StackPanel();
        var rows = new List<Border>();
        var bubbles = new List<Ellipse>();
        var max = a.Influencers.Max(i => i.Ratio);
        void Select(int index)
        {
            for (var i = 0; i < rows.Count; i++)
            {
                rows[i].BorderBrush = i == index ? new SolidColorBrush(accent) : Brushes.Transparent;
                rows[i].Background = i == index ? new SolidColorBrush(Color.Parse("#F3F2F1")) : Brushes.Transparent;
                bubbles[i].Fill = new SolidColorBrush(i == index ? accent : light);
            }
            detail.Child = Detail(a, a.Influencers[index], accent, light);
        }
        for (var index = 0; index < a.Influencers.Count; index++)
        {
            var influencer = a.Influencers[index];
            var size = max > 1 ? 30 + 14 * (influencer.Ratio - 1) / (max - 1) : 34;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,3*,46"), MinHeight = 46 };
            row.Children.Add(new TextBlock
            {
                Text = influencer.Condition, FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = AnalysisViews.Ink,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 2, 6, 2),
            });
            var line = new Rectangle { Height = 1, Fill = new SolidColorBrush(light), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(line, 1);
            row.Children.Add(line);
            var bubble = new Ellipse { Width = size, Height = size, Fill = new SolidColorBrush(light) };
            bubbles.Add(bubble);
            var badge = new Panel
            {
                Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    bubble,
                    new TextBlock
                    {
                        Text = influencer.Ratio.ToString("0.00", CultureInfo.CurrentCulture) + "x", FontSize = 9, FontWeight = FontWeight.SemiBold,
                        Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            };
            Grid.SetColumn(badge, 2);
            row.Children.Add(badge);
            var border = new Border
            {
                Child = row, BorderThickness = new Thickness(3, 0, 0, 0), Cursor = new Cursor(StandardCursorType.Hand),
                Background = Brushes.Transparent,
            };
            ToolTip.SetTip(border, $"{influencer.Probability:P1} of {Sql.FormatNumber(influencer.Count)} rows have {a.TargetName} = {a.TargetValue}");
            var captured = index;
            border.PointerPressed += (_, e) =>
            {
                Select(captured);
                e.Handled = true;
            };
            rows.Add(border);
            list.Children.Add(border);
        }
        left.Children.Add(new ScrollViewer { Content = list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        root.Children.Add(left);
        root.Children.Add(detail);
        Select(0);
        return root;
    }

    /// <summary>The selected influencer's field: the likelihood of the target at each value, or along a numeric field.</summary>
    static Control Detail(KeyInfluencerAnalysis a, Influencer influencer, Color accent, Color light)
    {
        var b = influencer.Breakdown;
        var panel = new DockPanel();
        var statement = b.IsNumeric
            ? $"On average when {b.Name} {(b.Fit?.Slope > 0 ? "increases" : "decreases")}, the likelihood of {a.TargetName} being {a.TargetValue} {(b.Fit?.Slope > 0 ? "increases" : "decreases")}."
            : $"{influencer.Probability:P1} of rows where {influencer.Condition[(b.Name.Length + 1)..]} have {a.TargetName} = {a.TargetValue}, against {a.Probability:P1} overall.";
        var text = new TextBlock { Text = statement, FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Foreground = AnalysisViews.Muted, Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(text, Dock.Top);
        panel.Children.Add(text);

        var ap = new AvaPlot();
        ap.UserInputProcessor.Disable();
        var plt = ap.Plot;
        plt.FigureBackground.Color = SP.Colors.White;
        plt.DataBackground.Color = SP.Colors.White;
        var ink = SP.Color.FromHex("#55554F");
        plt.Axes.Color(ink);
        plt.Grid.MajorLineColor = SP.Color.FromHex("#EDEDE9");
        plt.Axes.Top.FrameLineStyle.Width = 0;
        plt.Axes.Right.FrameLineStyle.Width = 0;
        plt.Axes.Left.Label.Text = $"%{a.TargetName} is {a.TargetValue}";
        plt.Axes.Left.Label.FontSize = 11;
        plt.Axes.Bottom.Label.Text = b.Name;
        plt.Axes.Bottom.Label.FontSize = 11;
        plt.Axes.Left.TickLabelStyle.FontSize = 10;
        plt.Axes.Bottom.TickLabelStyle.FontSize = 10;
        plt.Axes.Left.TickGenerator = new SP.TickGenerators.NumericAutomatic { LabelFormatter = v => $"{v:0}%" };
        var primary = SP.Color.FromHex(accent.ToString()[3..]);
        var secondary = SP.Color.FromHex(light.ToString()[3..]);

        if (b.IsNumeric)
        {
            var points = b.Points;
            var scatter = plt.Add.ScatterPoints(points.Select(p => p.X).ToArray(), points.Select(p => p.Probability * 100).ToArray());
            scatter.Color = primary;
            scatter.MarkerSize = 5;
            if (b.Fit is { } fit && points.Count > 1)
            {
                var (min, max) = (points.Min(p => p.X), points.Max(p => p.X));
                var xs = Enumerable.Range(0, 50).Select(i => min + (max - min) * i / 49).ToArray();
                var curve = plt.Add.ScatterLine(xs, xs.Select(x => 100 / (1 + Math.Exp(-(fit.Intercept + fit.Slope * x)))).ToArray());
                curve.Color = SP.Color.FromHex("#8A8886");
                curve.LinePattern = SP.LinePattern.Dotted;
                curve.LineWidth = 1.5f;
            }
            plt.Axes.Bottom.TickGenerator = new SP.TickGenerators.NumericAutomatic
            {
                LabelFormatter = v => Sql.FormatNumber(v, b.Field.FormatString, compact: true),
            };
            plt.Grid.XAxisStyle.MajorLineStyle.Width = 0;
        }
        else
        {
            // The values most likely to have the target, the selected one among them.
            var points = b.Points.Where(p => p.Count >= 10).Take(12).ToList();
            if (!points.Any(p => influencer.Condition.EndsWith(" is " + p.Label)))
                points = [.. points.Take(11), .. b.Points.Where(p => influencer.Condition.EndsWith(" is " + p.Label)).Take(1)];
            var bars = points.Select((p, i) => new SP.Bar
            {
                Position = i, Value = p.Probability * 100, Size = 0.7, LineWidth = 0,
                FillColor = influencer.Condition.EndsWith(" is " + p.Label) ? primary : secondary,
            }).ToList();
            plt.Add.Bars(bars);
            plt.Axes.Bottom.SetTicks(Enumerable.Range(0, points.Count).Select(i => (double)i).ToArray(), points.Select(p => p.Label).ToArray());
            if (points.Count > 4 || points.Any(p => p.Label.Length > 10))
            {
                plt.Axes.Bottom.TickLabelStyle.Rotation = -35;
                plt.Axes.Bottom.TickLabelStyle.Alignment = SP.Alignment.MiddleRight;
            }
            var average = plt.Add.HorizontalLine(a.Probability * 100);
            average.Color = SP.Color.FromHex("#8A8886");
            average.LinePattern = SP.LinePattern.Dashed;
            average.LineWidth = 1;
            average.LabelText = $"Average {a.Probability:P0}";
            average.LabelFontSize = 9;
            plt.Axes.Margins(bottom: 0, top: 0.1);
            plt.Grid.XAxisStyle.MajorLineStyle.Width = 0;
        }
        ap.Refresh();
        panel.Children.Add(ap);
        return panel;
    }
}
