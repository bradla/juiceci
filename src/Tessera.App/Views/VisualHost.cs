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
    static readonly IBrush TitleInk = new SolidColorBrush(Color.Parse("#252423"));

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
        if (!vm.ShowsData && vm.Type != VisualType.PageNavigator) Attach(_body, Drag.Move);

        vm.PropertyChanged += OnVmChanged;
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

    void UpdateBackground() =>
        Background = Vm.Style?.Background is { } bg ? Brush.Parse(bg) : Vm.ShowsData ? Brushes.White : Brushes.Transparent;

    void UpdateTitle()
    {
        var hidden = Vm.Style?.HideTitle == true;
        _title.Text = hidden ? "" : Vm.Title;
        _title.Foreground = Vm.Style?.TitleColor is { } c ? Brush.Parse(c) : TitleInk;
        _subtitle.Text = hidden ? null : Vm.Subtitle;
        _subtitle.IsVisible = _subtitle.Text is not null;
    }

    void Rebuild()
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
            _body.Content = ChartRenderer.Build(Vm.Type, result, Vm.Style);
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
            if (kind == Drag.Move)
            {
                var x = Math.Clamp(Snap(_startRect.X + d.X), 0, Math.Max(0, page.Width - _startRect.Width));
                var y = Math.Clamp(Snap(_startRect.Y + d.Y), 0, Math.Max(0, page.Height - _startRect.Height));
                Vm.SetGeometry(x, y, _startRect.Width, _startRect.Height);
            }
            else
            {
                // Layout elements such as thin header bars can be much smaller than a chart.
                var (minW, minH) = Vm.ShowsData ? (MinWidth_, MinHeight_) : (16.0, 8.0);
                var w = Math.Clamp(Snap(_startRect.Width + d.X), minW, Math.Max(minW, page.Width - _startRect.X));
                var h = Math.Clamp(Snap(_startRect.Height + d.Y), minH, Math.Max(minH, page.Height - _startRect.Y));
                Vm.SetGeometry(_startRect.X, _startRect.Y, w, h);
            }
        };
        handle.PointerReleased += (_, e) =>
        {
            if (_drag != kind) return;
            _drag = Drag.None;
            e.Pointer.Capture(null);
        };
        handle.PointerCaptureLost += (_, _) => _drag = Drag.None;
    }

    double Snap(double v) => _report.Snap(v);
}
