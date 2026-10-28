using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Tessera.App.ViewModels;
using Tessera.Core;

namespace Tessera.App.Views;

/// <summary>
/// The model diagram: a card per table listing its fields, with a line for each relationship
/// (dashed when inactive) marked 1 at the "one" end and * at the "many" end. Cards can be dragged.
/// </summary>
public sealed class ModelView : UserControl
{
    const double CardWidth = 200, FieldHeight = 18, HeaderHeight = 30, MaxFields = 12, Gap = 60;

    readonly Canvas _canvas = new();
    readonly Dictionary<string, Point> _positions = [];
    readonly Dictionary<string, Border> _cards = [];
    ReportViewModel? _vm;

    public ModelView()
    {
        Background = new SolidColorBrush(Color.Parse("#F3F2F1"));
        Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = _canvas,
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.Tables.CollectionChanged -= OnTablesChanged;
        _vm = DataContext as ReportViewModel;
        if (_vm is not null) _vm.Tables.CollectionChanged += OnTablesChanged;
        Rebuild();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && IsVisible) Rebuild();
    }

    void OnTablesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (IsVisible) Rebuild();
    }

    static double CardHeight(TableInfo t) => HeaderHeight + Math.Min(t.Fields.Count, MaxFields + 1) * FieldHeight + 10;

    void Rebuild()
    {
        _canvas.Children.Clear();
        _cards.Clear();
        if (_vm is null) return;
        var tables = _vm.Engine.Tables;
        var model = _vm.Engine.Model;

        // New tables go into a grid; dragged ones keep their place.
        var columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(tables.Count)));
        for (var i = 0; i < tables.Count; i++)
        {
            if (_positions.ContainsKey(tables[i].Name)) continue;
            var col = i % columns;
            var rowTop = tables.Where((_, j) => j % columns == col && j < i).Sum(t => CardHeight(t) + Gap);
            _positions[tables[i].Name] = new Point(24 + col * (CardWidth + Gap * 1.5), 24 + rowTop);
        }

        foreach (var r in model.Relationships)
            if (_positions.ContainsKey(r.FromTable) && _positions.ContainsKey(r.ToTable))
                _canvas.Children.Add(new RelationshipLine(r));
        foreach (var t in tables) _canvas.Children.Add(_cards[t.Name] = Card(t));
        Layout();
    }

    /// <summary>Positions cards and lines, and sizes the canvas to fit them.</summary>
    void Layout()
    {
        if (_vm is null) return;
        var tables = _vm.Engine.Tables.ToDictionary(t => t.Name);
        foreach (var (name, card) in _cards)
        {
            Canvas.SetLeft(card, _positions[name].X);
            Canvas.SetTop(card, _positions[name].Y);
        }
        Rect Box(string table) => new(_positions[table], new Size(CardWidth, CardHeight(tables[table])));
        foreach (var line in _canvas.Children.OfType<RelationshipLine>())
            line.Update(Box(line.Relationship.FromTable), Box(line.Relationship.ToTable));
        _canvas.Width = _positions.Values.Select(p => p.X + CardWidth).DefaultIfEmpty(0).Max() + 40;
        _canvas.Height = tables.Values.Select(t => _positions[t.Name].Y + CardHeight(t)).DefaultIfEmpty(0).Max() + 40;
    }

    Border Card(TableInfo table)
    {
        var fields = new StackPanel { Margin = new Thickness(10, 2, 10, 8) };
        foreach (var f in table.Fields.Take((int)MaxFields))
        {
            fields.Children.Add(new TextBlock
            {
                Text = (f.IsMeasure ? "ƒ  " : "    ") + f.Name,
                FontSize = 12,
                Height = FieldHeight,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(Color.Parse("#323130")),
            });
        }
        if (table.Fields.Count > MaxFields)
            fields.Children.Add(new TextBlock { Text = $"    + {table.Fields.Count - (int)MaxFields} more", FontSize = 12, Height = FieldHeight, Foreground = Brushes.Gray });

        var header = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#E1DFDD")),
            Height = HeaderHeight,
            Padding = new Thickness(10, 0),
            Cursor = new Cursor(StandardCursorType.SizeAll),
            Child = new TextBlock
            {
                Text = table.Name,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = Brushes.Black,
            },
        };
        ToolTip.SetTip(header, $"{table.Name}: {table.RowCount:N0} rows. Drag to move.");
        var card = new Border
        {
            Width = CardWidth,
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.Parse("#C8C6C4")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
            BoxShadow = BoxShadows.Parse("0 1 4 0 #22000000"),
            Child = new DockPanel { Children = { header, fields } },
        };
        DockPanel.SetDock(header, Dock.Top);

        Point? start = null;
        var origin = default(Point);
        header.PointerPressed += (_, e) =>
        {
            start = e.GetPosition(_canvas);
            origin = _positions[table.Name];
            e.Pointer.Capture(header);
        };
        header.PointerMoved += (_, e) =>
        {
            if (start is not { } s) return;
            var d = e.GetPosition(_canvas) - s;
            _positions[table.Name] = new Point(Math.Max(0, origin.X + d.X), Math.Max(0, origin.Y + d.Y));
            Layout();
        };
        header.PointerReleased += (_, e) =>
        {
            start = null;
            e.Pointer.Capture(null);
        };
        return card;
    }

    /// <summary>A connector between two table cards, with cardinality marks at each end.</summary>
    sealed class RelationshipLine : Canvas
    {
        readonly Line _line = new() { StrokeThickness = 1.5, Stroke = new SolidColorBrush(Color.Parse("#605E5C")) };
        readonly TextBlock _many = Mark("*"), _one = Mark("1");

        public RelationshipLine(Relationship r)
        {
            Relationship = r;
            if (!r.IsActive) _line.StrokeDashArray = [4, 3];
            ToolTip.SetTip(this, $"{r.FromTable}[{r.FromColumn}] → {r.ToTable}[{r.ToColumn}]{(r.IsActive ? "" : " (inactive)")}");
            Children.Add(_line);
            Children.Add(_many);
            Children.Add(_one);
        }

        public Relationship Relationship { get; }

        static TextBlock Mark(string text) => new() { Text = text, FontWeight = FontWeight.Bold, FontSize = 13, Foreground = Brushes.Black };

        public void Update(Rect from, Rect to)
        {
            var a = Edge(from, to.Center);
            var b = Edge(to, from.Center);
            (_line.StartPoint, _line.EndPoint) = (a, b);
            Place(_many, a + (b - a) * 0.08);
            Place(_one, b + (a - b) * 0.08);
        }

        static void Place(Control c, Point p)
        {
            SetLeft(c, p.X + 4);
            SetTop(c, p.Y - 16);
        }

        /// <summary>Where the line from a box's centre toward a point leaves the box.</summary>
        static Point Edge(Rect box, Point toward)
        {
            var c = box.Center;
            var d = toward - c;
            if (d.X == 0 && d.Y == 0) return c;
            var tx = d.X == 0 ? double.MaxValue : box.Width / 2 / Math.Abs(d.X);
            var ty = d.Y == 0 ? double.MaxValue : box.Height / 2 / Math.Abs(d.Y);
            return c + d * Math.Min(tx, ty);
        }
    }
}
