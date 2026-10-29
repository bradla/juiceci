using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Tessera.App.ViewModels;
using Tessera.Core;

namespace Tessera.App.Views;

public partial class ReportView : UserControl
{
    static readonly DataFormat<FieldInfo> FieldFormat = DataFormat.CreateInProcessFormat<FieldInfo>("juiceci.field");

    ReportViewModel? _vm;
    PointerPressedEventArgs? _fieldPress;
    FieldInfo? _pressedField;
    Border? _hotWell;

    public ReportView()
    {
        InitializeComponent();
        PageCanvas.PointerPressed += (_, e) =>
        {
            if (e.Source == PageCanvas && _vm is not null) _vm.Selected = null;
            Focus();
        };

        AddHandler(PointerPressedEvent, OnFieldPointerPressed);
        AddHandler(PointerMovedEvent, OnFieldPointerMoved);
        AddHandler(PointerReleasedEvent, (_, _) => _fieldPress = null);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => SetHotWell(null));
        AddHandler(DragDrop.DropEvent, OnDrop);
        CanvasScroller.SizeChanged += (_, _) => ApplyZoom();
    }

    /// <summary>Scales the page: fitted to the visible area, or at the chosen percentage.</summary>
    void ApplyZoom()
    {
        if (_vm is null) return;
        var area = CanvasScroller.Bounds.Size;
        const double margin = 40;
        double scale;
        if (_vm.IsMobileLayout && area.Width > margin)
        {
            // A phone layout is tall and scrolls, so it's fitted to the width, never past actual size.
            scale = Math.Min(1, (area.Width - margin - 18) / _vm.PageWidth);
            _vm.ReportFittedZoom(scale * 100);
        }
        else switch (_vm.Zoom)
        {
            case PageZoom.FitPage when area.Width > margin && area.Height > margin:
                scale = Math.Min((area.Width - margin) / _vm.PageWidth, (area.Height - margin) / _vm.PageHeight);
                _vm.ReportFittedZoom(scale * 100);
                break;
            case PageZoom.FitWidth when area.Width > margin:
                // Leave room for the vertical scroll bar the taller page will need.
                scale = (area.Width - margin - 18) / _vm.PageWidth;
                _vm.ReportFittedZoom(scale * 100);
                break;
            default:
                scale = _vm.ZoomPercent / 100;
                break;
        }
        scale = Math.Max(0.05, scale);
        PageZoomer.VerticalAlignment = _vm.AlignPageTop ? Avalonia.Layout.VerticalAlignment.Top : Avalonia.Layout.VerticalAlignment.Center;
        PageZoomer.LayoutTransform = new Avalonia.Media.ScaleTransform(scale, scale);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.Visuals.CollectionChanged -= OnVisualsChanged;
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }
        _vm = DataContext as ReportViewModel;
        if (_vm is null) return;
        _vm.Visuals.CollectionChanged += OnVisualsChanged;
        _vm.PropertyChanged += OnVmPropertyChanged;
        RebuildCanvas();
        FillPreview();
        ApplyZoom();
        ApplyWallpaper();
    }

    void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ReportViewModel.Zoom) or nameof(ReportViewModel.ZoomPercent)
            or nameof(ReportViewModel.PageWidth) or nameof(ReportViewModel.PageHeight) or nameof(ReportViewModel.AlignPageTop)
            or nameof(ReportViewModel.IsMobileLayout))
            ApplyZoom();
        if (e.PropertyName == nameof(ReportViewModel.PageWallpaper)) ApplyWallpaper();
        if (e.PropertyName is nameof(ReportViewModel.PreviewResult) or nameof(ReportViewModel.ShowWhitespace)
            or nameof(ReportViewModel.MonospacedPreview))
            FillPreview();
    }

    /// <summary>Design › Wallpaper colours the canvas around the page; none shows the app's canvas colour.</summary>
    void ApplyWallpaper() =>
        CanvasScroller.Background = _vm?.PageWallpaper is { } color ? Brush.Parse(color) : null;

    void FillPreview()
    {
        PreviewGrid.FontFamily = _vm!.MonospacedPreview ? new FontFamily("DejaVu Sans Mono, Consolas, Menlo, monospace") : FontFamily.Default;
        GridBuilder.Fill(PreviewGrid, _vm.PreviewResult, _vm.ShowWhitespace);
    }

    void OnVisualsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null)
            foreach (VisualViewModel v in e.NewItems) PageCanvas.Children.Add(new VisualHost(v, _vm!));
        else
            RebuildCanvas();
    }

    void RebuildCanvas()
    {
        PageCanvas.Children.Clear();
        if (_vm is null) return;
        foreach (var v in _vm.Visuals) PageCanvas.Children.Add(new VisualHost(v, _vm));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _vm is null || TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox)
            return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key is Key.C or Key.X or Key.V)
        {
            var command = e.Key switch { Key.C => _vm.CopyCommand, Key.X => _vm.CutCommand, _ => _vm.PasteCommand };
            if (command.CanExecute(null)) command.Execute(null);
            e.Handled = true;
            return;
        }
        if (_vm.Selected is null) return;
        if (e.Key is Key.Delete or Key.Back)
        {
            _vm.DeleteSelectedCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _vm.Selected = null;
            e.Handled = true;
        }
    }

    // ---- Dragging fields out of the Data pane ----

    void OnFieldPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _fieldPress = null;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var row = (e.Source as Visual)?.FindAncestorOfType<Border>(includeSelf: true);
        while (row is not null && !row.Classes.Contains("fieldRow")) row = row.FindAncestorOfType<Border>();
        if (row?.DataContext is not FieldNodeViewModel field) return;
        _fieldPress = e;
        _pressedField = field.Info;
    }

    async void OnFieldPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_fieldPress is not { } press || _pressedField is not { } field) return;
        var delta = e.GetPosition(this) - press.GetPosition(this);
        if (Math.Abs(delta.X) + Math.Abs(delta.Y) < 6) return;
        _fieldPress = null;

        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(FieldFormat, field));
        await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Copy);
        SetHotWell(null);
    }

    void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetValue(FieldFormat) is not { } field) return;
        var target = DropTargetAt(e, field);
        e.DragEffects = target is null ? DragDropEffects.None : DragDropEffects.Copy;
        SetHotWell(target?.Well);
        e.Handled = true;
    }

    void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetValue(FieldFormat) is not { } field) return;
        e.Handled = true;
        SetHotWell(null);
        if (_vm is null || DropTargetAt(e, field) is not { } target) return;

        if (target.Well is { Tag: FilterScope scope })
            _vm.AddFilter(field, scope);
        else if (target.Well is { Tag: PageWell.DrillThrough })
            _vm.AddDrillThroughField(field);
        else if (target.Well is { Tag: Well well })
            _vm.AddField(field, _vm.Selected, well);
        else
            _vm.AddField(field, target.Visual, at: target.Visual is null ? e.GetPosition(PageCanvas) : null);
    }

    sealed record DropTarget(VisualViewModel? Visual, Border? Well);

    /// <summary>A field well, a visual on the page, or empty page space; null if none.</summary>
    DropTarget? DropTargetAt(DragEventArgs e, FieldInfo field)
    {
        for (var v = e.Source as Visual; v is not null; v = v.GetVisualParent())
        {
            if (v is Border { Tag: Well } well && _vm?.Selected is not null) return new DropTarget(null, well);
            if (v is Border { Tag: FilterScope or PageWell } filters && !field.IsMeasure) return new DropTarget(null, filters);
            if (v is VisualHost host) return new DropTarget(host.Vm, null);
            if (v == PageCanvas) return new DropTarget(null, null);
        }
        return null;
    }

    void SetHotWell(Border? well)
    {
        if (_hotWell == well) return;
        _hotWell?.Classes.Remove("dropTarget");
        _hotWell = well;
        _hotWell?.Classes.Add("dropTarget");
    }
}
