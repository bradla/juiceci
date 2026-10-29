using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Controls.Shapes;
using Path = Avalonia.Controls.Shapes.Path;
using Tessera.App.ViewModels;
using Tessera.Core;

namespace Tessera.App.Views;

/// <summary>
/// One visual on the report page: a header strip that moves it, a body that renders
/// the query result, and a corner grip that resizes it while selected. Layout elements
/// (text boxes, shapes, images) have no header and are moved by their body.
/// </summary>
public sealed class VisualHost : Border
{
    const double MinWidth_ = 120, MinHeight_ = 80;

    static readonly IBrush SelectedBorder = new SolidColorBrush(Color.Parse("#0E7C66"));
    static readonly IBrush IdleBorder = new SolidColorBrush(Color.Parse("#E6E6E6"));

    readonly ReportViewModel _report;
    readonly TextBlock _title = new()
    {
        FontWeight = FontWeight.Normal,
        FontSize = 14,
        Margin = new Thickness(10, 6, 4, 2),
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };
    readonly TextBlock _subtitle = new()
    {
        FontSize = 11,
        Margin = new Thickness(10, 0, 4, 2),
        TextTrimming = TextTrimming.CharacterEllipsis,
        Foreground = new SolidColorBrush(Color.Parse("#605E5C")),
    };
    readonly ContentControl _body = new() { Margin = new Thickness(4, 0, 4, 4) };
    readonly Border _grip;
    readonly Button _close;
    readonly Grid _header;

    enum Drag { None, Move, Resize }
    Drag _drag;
    Point _dragStart;
    Rect _startRect;

    public VisualHost(VisualViewModel vm, ReportViewModel report)
    {
        Vm = vm;
        _report = report;
        UpdateBackground();
        // Power BI's Fluent 2 look: rounded white cards with a light border.
        CornerRadius = new CornerRadius(vm.ShowsData || vm.Type == VisualType.Unsupported ? 8 : 0);
        BorderThickness = new Thickness(1);
        ClipToBounds = true;

        var header = _header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.SizeAll), MinHeight = 28 };
        _close = new Button
        {
            Classes = { "flat" },
            Padding = new Thickness(6),
            Margin = new Thickness(0, 2, 2, 0),
            Content = new Path { Classes = { "icon" }, Data = (Geometry)Application.Current!.FindResource("Icon.Close")!, Width = 10, Height = 10 },
        };
        ToolTip.SetTip(_close, "Delete visual");
        _close.Click += (_, _) => report.DeleteSelectedCommand.Execute(null);
        Avalonia.Controls.Grid.SetColumn(_close, 1);
        header.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _title, _subtitle } });
        header.Children.Add(_close);

        _grip = new Border
        {
            Width = 14,
            Height = 14,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = SelectedBorder,
            CornerRadius = new CornerRadius(3, 0, 3, 0),
            Cursor = new Cursor(StandardCursorType.BottomRightCorner),
        };
        ToolTip.SetTip(_grip, "Resize");

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Avalonia.Controls.Grid.SetRow(_body, 1);
        Avalonia.Controls.Grid.SetRowSpan(_grip, 2);
        root.Children.Add(header);
        root.Children.Add(_body);
        root.Children.Add(_grip);
        Child = root;

        // Selecting on the tunnel pass lets the click still reach the chart or grid inside.
        AddHandler(PointerPressedEvent, (_, _) => _report.Selected = Vm, RoutingStrategies.Tunnel);
        Attach(header, Drag.Move);
        Attach(_grip, Drag.Resize);
        if (!vm.ShowsData && vm.Type is not (VisualType.PageNavigator or VisualType.BookmarkNavigator)) Attach(_body, Drag.Move);
        if (vm.Type == VisualType.BookmarkNavigator)
        {
            // Bookmarks are added and renamed in their pane; the buttons follow.
            void Changed(object? s, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => Rebuild();
            report.Bookmarks.CollectionChanged += Changed;
            DetachedFromVisualTree += (_, _) => report.Bookmarks.CollectionChanged -= Changed;
        }

        // Report settings › Copy object names when right clicking on report objects.
        var copyName = new MenuItem { Header = "Copy object name" };
        copyName.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, Vm.Title);
        };
        var offMobile = new MenuItem { Header = "Remove from mobile layout" };
        offMobile.Click += (_, _) => _report.RemoveFromMobileCommand.Execute(Vm);
        // Drill through › page › value, for pages whose drill-through field this visual groups by.
        var drill = new MenuItem { Header = "Drill through" };
        ContextMenu = new ContextMenu { Items = { drill, copyName, offMobile } };
        ContextMenu.Opening += (_, e) =>
        {
            drill.Items.Clear();
            foreach (var target in _report.DrillTargets(Vm))
            {
                var page = new MenuItem { Header = target.Page };
                foreach (var value in target.Values)
                {
                    var item = new MenuItem { Header = value ?? "(Blank)" };
                    item.Click += (_, _) => _report.DrillThrough(target, value);
                    page.Items.Add(item);
                }
                drill.Items.Add(page);
            }
            drill.IsVisible = drill.Items.Count > 0;
            copyName.IsVisible = _report.CopyObjectNames;
            offMobile.IsVisible = _report.IsMobileLayout;
            e.Cancel = !drill.IsVisible && !copyName.IsVisible && !offMobile.IsVisible;
        };

        vm.PropertyChanged += OnVmChanged;
        // A new theme recolours every visual that doesn't set its own colours.
        void ThemeChanged(object? s, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ReportViewModel.Theme)) return;
            UpdateBackground();
            Rebuild();
        }
        report.PropertyChanged += ThemeChanged;
        DetachedFromVisualTree += (_, _) => report.PropertyChanged -= ThemeChanged;
        IsVisible = vm.IsOnCanvas;
        UpdateGeometry();
        UpdateSelection();
        Rebuild();
    }

    public VisualViewModel Vm { get; }

    void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(VisualViewModel.X) or nameof(VisualViewModel.Y)
                or nameof(VisualViewModel.Width) or nameof(VisualViewModel.Height):
                UpdateGeometry();
                break;
            case nameof(VisualViewModel.IsSelected):
                UpdateSelection();
                break;
            case nameof(VisualViewModel.IsOnCanvas):
                IsVisible = Vm.IsOnCanvas;
                break;
            case nameof(VisualViewModel.Title) or nameof(VisualViewModel.Subtitle):
                UpdateTitle();
                break;
            case nameof(VisualViewModel.Style):
                UpdateBackground();
                Rebuild();
                break;
            case nameof(VisualViewModel.Result) or nameof(VisualViewModel.Error) or nameof(VisualViewModel.Type)
                or nameof(VisualViewModel.Analysis):
                Rebuild();
                break;
        }
    }

    void UpdateGeometry()
    {
        Canvas.SetLeft(this, Vm.X);
        Canvas.SetTop(this, Vm.Y);
        Width = Vm.Width;
        Height = Vm.Height;
    }

    bool HasHeader => Vm.ShowsData || Vm.Type == VisualType.Unsupported;

    void UpdateSelection()
    {
        BorderBrush = Vm.IsSelected ? SelectedBorder : HasHeader ? IdleBorder : Brushes.Transparent;
        BorderThickness = new Thickness(Vm.IsSelected ? 2 : 1);
        _grip.IsVisible = Vm.IsSelected;
        _close.IsVisible = Vm.IsSelected;
        ZIndex = Vm.IsSelected ? 1 : 0;
    }

    // Shapes other than rectangles draw their own fill, so the frame stays clear.
    void UpdateBackground() =>
        Background = Vm.Type == VisualType.Shape && Vm.Style?.Shape is not null ? Brushes.Transparent
            : Vm.Style?.Background is { } bg ? Brush.Parse(bg) : Vm.ShowsData ? Brush.Parse(_report.Theme.Background) : Brushes.Transparent;

    void UpdateTitle()
    {
        var hidden = Vm.Style?.HideTitle == true;
        _title.Text = hidden ? "" : Vm.Title;
        _title.Foreground = Vm.Style?.TitleColor is { } c ? Brush.Parse(c) : Brush.Parse(_report.Theme.Foreground);
        _subtitle.Text = hidden ? null : Vm.Subtitle;
        _subtitle.IsVisible = _subtitle.Text is not null;
    }

    /// <summary>Redraws the visual and tells the Performance analyzer how long that took.</summary>
    void Rebuild()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        RebuildContent();
        if (Vm.ShowsData) _report.ReportDisplayTime(Vm, clock.Elapsed);
    }

    void RebuildContent()
    {
        UpdateTitle();
        _header.IsVisible = HasHeader;
        _body.Margin = HasHeader ? new Thickness(4, 0, 4, 4) : default;
        if (!Vm.ShowsData)
        {
            _body.Content = LayoutElement();
            return;
        }
        if (Vm.Error is { } error)
        {
            _body.Content = new TextBlock
            {
                Text = "Can't display this visual.\n" + error,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.Parse("#B23B2A")),
                Margin = new Thickness(8),
            };
        }
        else if (Vm.Result is { } result)
        {
            _body.Content = ChartRenderer.Build(Vm.Type, result, Vm.Style, _report.Theme);
        }
        else if (Vm.Analysis is { } analysis)
        {
            _body.Content = AnalysisViews.Build(analysis, Vm);
        }
        else
        {
            var key = ReportViewModel.VisualTypes.First(t => t.Type == Vm.Type).IconKey;
            _body.Content = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 8,
                Children =
                {
                    new Path { Classes = { "icon" }, Data = (Geometry)Application.Current!.FindResource(key)!, Width = 40, Height = 40, Opacity = 0.35 },
                    new TextBlock { Text = "Add fields to build this visual", Classes = { "muted" }, FontSize = 12 },
                },
            };
        }
    }

    Control? LayoutElement()
    {
        var style = Vm.Style ?? new VisualStyle();
        switch (Vm.Type)
        {
            case VisualType.Text:
            {
                var text = style.Text ?? "";
                var block = new TextBlock
                {
                    Text = text,
                    FontSize = style.FontSize ?? 14,
                    FontWeight = style.Bold ? FontWeight.Bold : FontWeight.Normal,
                    Foreground = style.Foreground is { } fg ? Brush.Parse(fg) : Brushes.Black,
                    Margin = new Thickness(4, 2),
                };
                // Fonts differ from Power BI's, so a one-line label shrinks to fit rather than being cut off.
                if (text.Contains('\n') || text.Length > 60)
                {
                    block.TextWrapping = TextWrapping.Wrap;
                    return block;
                }
                return new Viewbox
                {
                    Stretch = Stretch.Uniform,
                    StretchDirection = StretchDirection.DownOnly,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Child = block,
                };
            }
            case VisualType.Image when style.Image is { } data:
                try
                {
                    return new Image { Source = new Bitmap(new MemoryStream(Convert.FromBase64String(data))), Stretch = Stretch.Uniform };
                }
                catch (Exception e) when (e is FormatException or ArgumentException or InvalidOperationException)
                {
                    return null;
                }
            case VisualType.PageNavigator:
                return PageNavigator();
            case VisualType.BookmarkNavigator:
                return BookmarkNavigator();
            case VisualType.Shape when style.Shape is { } kind:
            {
                var fill = Brush.Parse(style.Background ?? "#D6E4F0");
                return kind switch
                {
                    "Oval" => new Ellipse { Fill = fill },
                    "RoundedRectangle" => new Border { Background = fill, CornerRadius = new CornerRadius(Math.Min(Vm.Width, Vm.Height) / 5) },
                    "Triangle" => new Path { Data = Geometry.Parse("M0,1 L0.5,0 L1,1 Z"), Stretch = Stretch.Fill, Fill = fill },
                    "Arrow" => new Path { Data = Geometry.Parse("M0,0.3 H0.6 V0 L1,0.5 L0.6,1 V0.7 H0 Z"), Stretch = Stretch.Fill, Fill = fill },
                    _ => null,
                };
            }
            case VisualType.Unsupported:
                return new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Spacing = 6,
                    Margin = new Thickness(8),
                    Children =
                    {
                        new Path { Classes = { "icon" }, Data = (Geometry)Application.Current!.FindResource("Icon.Info")!, Width = 28, Height = 28, Opacity = 0.4 },
                        new TextBlock { Text = style.Text ?? "Visual", FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                        new TextBlock
                        {
                            Text = "JuiceCI can't show this kind of visual yet.",
                            Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                        },
                    },
                };
            default:
                return null;
        }
    }

    /// <summary>A row of buttons, one per bookmark, like Power BI's bookmark navigator.</summary>
    Control BookmarkNavigator()
    {
        if (_report.Bookmarks.Count == 0)
            return new TextBlock
            {
                Text = "Add bookmarks (View › Bookmarks) to fill this navigator",
                Classes = { "muted" }, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
        var row = new Avalonia.Controls.Primitives.UniformGrid { Rows = 1 };
        foreach (var bookmark in _report.Bookmarks)
        {
            var button = new Button
            {
                Content = bookmark.Name,
                Padding = new Thickness(4, 0),
                FontSize = Math.Clamp(Vm.Height * 0.45, 8, 14),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                CornerRadius = default,
                Margin = new Thickness(1, 0),
                Background = Brushes.White,
                Foreground = Brushes.Black,
            };
            ToolTip.SetTip(button, $"Show {bookmark.Name}");
            button.Click += (_, _) => bookmark.ApplyCommand.Execute(null);
            row.Children.Add(button);
        }
        return row;
    }

    /// <summary>A row of buttons, one per page, like Power BI's page navigator.</summary>
    Control PageNavigator()
    {
        var row = new Avalonia.Controls.Primitives.UniformGrid { Rows = 1 };
        foreach (var page in _report.Pages)
        {
            var current = page.IsCurrent;
            var button = new Button
            {
                Content = page.Name,
                Padding = new Thickness(4, 0),
                FontSize = Math.Clamp(Vm.Height * 0.45, 8, 14),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                CornerRadius = default,
                Margin = new Thickness(1, 0),
                Background = current ? new SolidColorBrush(Color.Parse("#E6E6E6")) : Brushes.White,
                BorderBrush = current ? new SolidColorBrush(Color.Parse("#252423")) : Brushes.Transparent,
                BorderThickness = new Thickness(0, 0, 0, 2),
                Foreground = Brushes.Black,
            };
            ToolTip.SetTip(button, $"Go to {page.Name}");
            button.Click += (_, _) => _report.SelectPage(page.Name);
            row.Children.Add(button);
        }
        return row;
    }

    void Attach(Control handle, Drag kind)
    {
        handle.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed || Parent is not Canvas canvas) return;
            if (_report.LockObjects)
            {
                // View › Lock objects: a click still selects, but nothing moves.
                return;
            }
            _drag = kind;
            _dragStart = e.GetPosition(canvas);
            _startRect = new Rect(Vm.X, Vm.Y, Vm.Width, Vm.Height);
            e.Pointer.Capture(handle);
            e.Handled = true;
        };
        handle.PointerMoved += (_, e) =>
        {
            if (_drag != kind || Parent is not Canvas canvas) return;
            var d = e.GetPosition(canvas) - _dragStart;
            var page = canvas.Bounds.Size;
            var others = _report.Visuals.Where(v => v != Vm).Select(v => new Rect(v.X, v.Y, v.Width, v.Height)).ToList();
            var guides = new List<(bool Vertical, double At)>();
            if (kind == Drag.Move)
            {
                var x = Math.Clamp(Snap(_startRect.X + d.X), 0, Math.Max(0, page.Width - _startRect.Width));
                var y = Math.Clamp(Snap(_startRect.Y + d.Y), 0, Math.Max(0, page.Height - _startRect.Height));
                if (_report.SmartGuides)
                {
                    x = Align(x, _startRect.Width, others.Select(o => (o.X, o.Width)), [0, 0.5, 1], true, guides);
                    y = Align(y, _startRect.Height, others.Select(o => (o.Y, o.Height)), [0, 0.5, 1], false, guides);
                }
                Vm.SetGeometry(x, y, _startRect.Width, _startRect.Height);
            }
            else
            {
                // Layout elements such as thin header bars can be much smaller than a chart.
                var (minW, minH) = Vm.ShowsData ? (MinWidth_, MinHeight_) : (16.0, 8.0);
                var w = Math.Clamp(Snap(_startRect.Width + d.X), minW, Math.Max(minW, page.Width - _startRect.X));
                var h = Math.Clamp(Snap(_startRect.Height + d.Y), minH, Math.Max(minH, page.Height - _startRect.Y));
                if (_report.SmartGuides)
                {
                    // Only the far edges move when resizing, so only they line up.
                    w = Math.Max(minW, Align(_startRect.X + w, 0, others.Select(o => (o.X, o.Width)), [0], true, guides) - _startRect.X);
                    h = Math.Max(minH, Align(_startRect.Y + h, 0, others.Select(o => (o.Y, o.Height)), [0], false, guides) - _startRect.Y);
                }
                Vm.SetGeometry(_startRect.X, _startRect.Y, w, h);
            }
            ShowGuides(canvas, guides);
        };
        handle.PointerReleased += (_, e) =>
        {
            if (_drag != kind) return;
            _drag = Drag.None;
            e.Pointer.Capture(null);
            if (Parent is Canvas canvas) ShowGuides(canvas, []);
        };
        handle.PointerCaptureLost += (_, _) =>
        {
            _drag = Drag.None;
            if (Parent is Canvas canvas) ShowGuides(canvas, []);
        };
    }

    /// <summary>Within this many page pixels, an edge or centre snaps to another visual's.</summary>
    const double GuideReach = 6;

    /// <summary>
    /// Power BI's smart guides: moves <paramref name="start"/> so the nearest of its edges or centre
    /// (<paramref name="anchors"/> as fractions of <paramref name="size"/>) meets one of another visual's,
    /// and records a guide for each line that now matches.
    /// </summary>
    public static double Align(double start, double size, IEnumerable<(double Start, double Size)> others, double[] anchors,
        bool vertical, List<(bool Vertical, double At)> guides)
    {
        var targets = others.SelectMany(o => new[] { o.Start, o.Start + o.Size / 2, o.Start + o.Size }).ToList();
        var best = (Gap: GuideReach + 1, Shift: 0.0);
        foreach (var a in anchors)
            foreach (var t in targets)
            {
                var gap = t - (start + a * size);
                if (Math.Abs(gap) < Math.Abs(best.Gap)) best = (gap, gap);
            }
        if (Math.Abs(best.Gap) > GuideReach) return start;
        start += best.Shift;
        foreach (var a in anchors)
            if (targets.Any(t => Math.Abs(t - (start + a * size)) < 0.5)) guides.Add((vertical, start + a * size));
        return start;
    }

    /// <summary>Replaces the dashed guide lines on the page.</summary>
    static void ShowGuides(Canvas canvas, List<(bool Vertical, double At)> guides)
    {
        foreach (var old in canvas.Children.OfType<Avalonia.Controls.Shapes.Line>().Where(l => l.Tag as string == "guide").ToList())
            canvas.Children.Remove(old);
        var size = canvas.Bounds.Size;
        foreach (var (vertical, at) in guides.Distinct())
            canvas.Children.Add(new Avalonia.Controls.Shapes.Line
            {
                Tag = "guide",
                StartPoint = vertical ? new Point(at, 0) : new Point(0, at),
                EndPoint = vertical ? new Point(at, size.Height) : new Point(size.Width, at),
                Stroke = new SolidColorBrush(Color.Parse("#D13438")),
                StrokeThickness = 1,
                StrokeDashArray = [4, 3],
                IsHitTestVisible = false,
                ZIndex = int.MaxValue,
            });
    }

    double Snap(double v) => _report.Snap(v);
}
