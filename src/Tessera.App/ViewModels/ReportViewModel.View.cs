using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tessera.Core;

namespace Tessera.App.ViewModels;

/// <summary>A row in the Performance analyzer: one visual's query and drawing time for one refresh.</summary>
public sealed partial class PerformanceEntryViewModel(VisualViewModel visual, double queryMs) : ObservableObject
{
    public VisualViewModel Visual { get; } = visual;
    public string Name { get; } = visual.LayerName;
    public double QueryMs { get; } = queryMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalMs), nameof(Detail))]
    public partial double? DisplayMs { get; set; }

    public double TotalMs => QueryMs + (DisplayMs ?? 0);

    public string Detail => $"Query  {QueryMs:0}    Visual display  {(DisplayMs is { } d ? d.ToString("0") : "…")}";
}

public sealed partial class BookmarkViewModel(Bookmark bookmark, Action<BookmarkViewModel> apply, Action<BookmarkViewModel> remove,
    Action renamed) : ObservableObject
{
    public Bookmark Bookmark { get; } = bookmark;

    public string Name
    {
        get => Bookmark.Name;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == Bookmark.Name) return;
            Bookmark.Name = value.Trim();
            OnPropertyChanged();
            renamed();
        }
    }

    public string PageName => Bookmark.Page;

    [RelayCommand]
    void Apply() => apply(this);

    [RelayCommand]
    void Remove() => remove(this);
}

/// <summary>The View tab: page view, mobile layout, page options and the extra panes.</summary>
public partial class ReportViewModel
{
    VisualViewModel CreateVisual(VisualSpec spec)
    {
        // Filters on all pages apply alongside the page's own.
        var vm = new VisualViewModel(spec, _engine, OnVisualChanged, () => [.. _doc.Filters, .. _page.Filters])
        {
            UseMobile = IsMobileLayout, IsPaused = () => IsVisualsPaused,
        };
        vm.Queried += OnVisualQueried;
        return vm;
    }

    // ---- Optimize ----

    /// <summary>Optimize › Pause visuals: edits stop re-running queries until Refresh visuals or un-pausing.</summary>
    [ObservableProperty]
    public partial bool IsVisualsPaused { get; set; }

    partial void OnIsVisualsPausedChanged(bool value)
    {
        if (!value) foreach (var v in Visuals) v.Refresh();
    }

    /// <summary>Optimize › Refresh visuals: runs every visual's query now, even while paused.</summary>
    [RelayCommand]
    void RefreshPausedVisuals()
    {
        var paused = IsVisualsPaused;
        IsVisualsPaused = false;
        foreach (var v in Visuals) v.Refresh();
        IsVisualsPaused = paused;
    }

    /// <summary>
    /// Optimize › Optimization presets: Query reduction turns off cross highlighting and adds Apply buttons;
    /// Interactivity turns them back on. They set the Current file › Query reduction options.
    /// </summary>
    public void ApplyOptimizationPreset(bool reduceQueries)
    {
        var settings = new Dictionary<string, string>(_doc.Settings ?? [])
        {
            [Core.FileSettings.DisableCrossHighlight] = reduceQueries ? "true" : "false",
            [Core.FileSettings.SlicerApply] = reduceQueries ? "1" : "0",
            [Core.FileSettings.FilterApply] = reduceQueries ? "2" : "0",
        };
        SetFileSettings(settings);
    }

    // ---- Format painter ----

    VisualStyle? _painterStyle;

    /// <summary>Home › Format painter: on, the next visual selected takes the copied formatting.</summary>
    [ObservableProperty]
    public partial bool IsFormatPainting { get; set; }

    [RelayCommand]
    void FormatPainter()
    {
        if (IsFormatPainting || Selected is null)
        {
            IsFormatPainting = false;
            _painterStyle = null;
            return;
        }
        _painterStyle = Selected.Spec.Style ?? new VisualStyle();
        IsFormatPainting = true;
    }

    void PaintFormat(VisualViewModel target)
    {
        if (!IsFormatPainting || _painterStyle is null) return;
        target.ApplyFormat(_painterStyle);
        IsFormatPainting = false;
        _painterStyle = null;
    }

    // ---- Page options ----

    /// <summary>View › Gridlines: a dotted grid on the page to line visuals up against.</summary>
    [ObservableProperty]
    public partial bool ShowGridlines { get; set; }

    /// <summary>Gridline spacing: the snap grid when there is one, else Power BI's 16px.</summary>
    public double GridlineSpacing => GridSize > 1 ? GridSize * 2 : 16;

    /// <summary>View › Lock objects: visuals can't be moved or resized.</summary>
    [ObservableProperty]
    public partial bool LockObjects { get; set; }

    // ---- Mobile layout ----

    const double PhoneWidth = 320, PhoneHeight = 568;

    /// <summary>View › Mobile layout: the page as a phone shows it, from each visual's mobile position.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageWidth), nameof(PageHeight))]
    public partial bool IsMobileLayout { get; set; }

    partial void OnIsMobileLayoutChanged(bool value)
    {
        Selected = null;
        foreach (var v in Visuals) v.UseMobile = value;
        OnPropertyChanged(nameof(OffMobileVisuals));
    }

    double MobileWidth => Math.Max(PhoneWidth, Visuals.Where(v => v.Spec.Mobile is not null).Select(v => v.Spec.Mobile!.X + v.Spec.Mobile.Width).DefaultIfEmpty().Max());

    double MobileHeight => Math.Max(PhoneHeight,
        Visuals.Where(v => v.Spec.Mobile is not null).Select(v => v.Spec.Mobile!.Y + v.Spec.Mobile.Height + 40).DefaultIfEmpty().Max());

    /// <summary>The Page visuals list beside the phone: visuals not on the mobile layout yet.</summary>
    public IReadOnlyList<VisualViewModel> OffMobileVisuals => Visuals.Where(v => v.Spec.Mobile is null && !v.IsHidden).ToList();

    /// <summary>Adds a visual to the bottom of the mobile layout, full width, keeping its proportions.</summary>
    [RelayCommand]
    void AddToMobile(VisualViewModel visual)
    {
        var bottom = Visuals.Where(v => v.Spec.Mobile is not null).Select(v => v.Spec.Mobile!.Y + v.Spec.Mobile.Height).DefaultIfEmpty().Max();
        var height = Math.Round(visual.Spec.Height * PhoneWidth / Math.Max(1, visual.Spec.Width));
        visual.PlaceOnMobile(new MobilePosition(0, bottom, PhoneWidth, Math.Clamp(height, 40, 400)));
        OnPropertyChanged(nameof(OffMobileVisuals));
        OnPropertyChanged(nameof(PageHeight));
    }

    [RelayCommand]
    void RemoveFromMobile(VisualViewModel visual)
    {
        visual.Spec.Mobile = null;
        visual.UseMobile = !IsMobileLayout;   // re-raise geometry for the view
        visual.UseMobile = IsMobileLayout;
        if (Selected == visual) Selected = null;
        OnPropertyChanged(nameof(OffMobileVisuals));
        RaiseChanged();
    }

    // ---- Panes ----

    [ObservableProperty]
    public partial bool ShowBookmarksPane { get; set; }

    [ObservableProperty]
    public partial bool ShowSelectionPane { get; set; }

    [ObservableProperty]
    public partial bool ShowPerformancePane { get; set; }

    [ObservableProperty]
    public partial bool ShowSyncSlicersPane { get; set; }

    /// <summary>View › Filters: Power BI's button collapses or expands the Filters pane.</summary>
    public bool IsFiltersPaneOpen
    {
        get => ShowFiltersPane && IsFiltersExpanded;
        set
        {
            ShowFiltersPane = true;
            IsFiltersExpanded = value;
        }
    }

    partial void OnIsFiltersExpandedChanged(bool value) => OnPropertyChanged(nameof(IsFiltersPaneOpen));
    partial void OnShowFiltersPaneChanged(bool value) => OnPropertyChanged(nameof(IsFiltersPaneOpen));

    [RelayCommand]
    void CloseBookmarksPane() => ShowBookmarksPane = false;

    [RelayCommand]
    void CloseSelectionPane() => ShowSelectionPane = false;

    [RelayCommand]
    void ClosePerformancePane() => ShowPerformancePane = false;

    [RelayCommand]
    void CloseSyncSlicersPane() => ShowSyncSlicersPane = false;

    // ---- Selection pane ----

    /// <summary>The Selection pane lists the page's visuals top layer first, as Power BI does.</summary>
    public IReadOnlyList<VisualViewModel> Layers => Visuals.Reverse().ToList();

    [RelayCommand]
    void SelectLayer(VisualViewModel visual)
    {
        if (visual.IsOnCanvas) Selected = visual;
    }

    [RelayCommand]
    void ToggleLayerVisibility(VisualViewModel visual)
    {
        visual.IsHidden = !visual.IsHidden;
        if (visual.IsHidden && Selected == visual) Selected = null;
        OnPropertyChanged(nameof(OffMobileVisuals));
    }

    [RelayCommand]
    void ShowAllLayers()
    {
        foreach (var v in Visuals) v.IsHidden = false;
        OnPropertyChanged(nameof(OffMobileVisuals));
    }

    [RelayCommand]
    void HideAllLayers()
    {
        Selected = null;
        foreach (var v in Visuals) v.IsHidden = true;
        OnPropertyChanged(nameof(OffMobileVisuals));
    }

    // ---- Bookmarks ----

    public ObservableCollection<BookmarkViewModel> Bookmarks { get; } = [];

    void RebuildBookmarks()
    {
        Bookmarks.Clear();
        foreach (var b in _doc.Bookmarks ?? []) Bookmarks.Add(BookmarkVm(b));
    }

    BookmarkViewModel BookmarkVm(Bookmark b) => new(b, ApplyBookmark, RemoveBookmark, RaiseChanged);

    /// <summary>Captures the current page, its filters and which visuals are hidden.</summary>
    [RelayCommand]
    void AddBookmark()
    {
        _page.Visuals = Visuals.Select(v => v.Spec).ToList();
        var bookmark = new Bookmark
        {
            Name = NextBookmarkName(),
            Page = _page.Name,
            Filters = Clone(_page.Filters),
            HiddenVisuals = _page.Visuals.Where(v => v.Hidden).Select(v => v.Id).ToList(),
        };
        (_doc.Bookmarks ??= []).Add(bookmark);
        Bookmarks.Add(BookmarkVm(bookmark));
        RaiseChanged();
    }

    string NextBookmarkName()
    {
        for (var i = 1; ; i++)
            if (Bookmarks.All(b => b.Name != $"Bookmark {i}")) return $"Bookmark {i}";
    }

    void ApplyBookmark(BookmarkViewModel bookmark)
    {
        var b = bookmark.Bookmark;
        if (Pages.FirstOrDefault(p => p.Name == b.Page) is not { } tab) return;
        ShowPage(tab);
        _page.Filters = Clone(b.Filters);
        foreach (var v in Visuals) v.Spec.Hidden = b.HiddenVisuals.Contains(v.Spec.Id);
        // Rebuild so the canvas, filters and Selection pane all show the bookmarked state.
        ShowPage(tab);
        RaiseChanged();
    }

    void RemoveBookmark(BookmarkViewModel bookmark)
    {
        _doc.Bookmarks?.Remove(bookmark.Bookmark);
        if (_doc.Bookmarks is { Count: 0 }) _doc.Bookmarks = null;
        Bookmarks.Remove(bookmark);
        RaiseChanged();
    }

    static List<FieldFilter> Clone(List<FieldFilter> filters) =>
        JsonSerializer.Deserialize<List<FieldFilter>>(JsonSerializer.Serialize(filters)) ?? [];

    // ---- Landing page ----

    public string? LandingPageName => _doc.LandingPage;

    public string LandingPageText => $"Report landing page: {_doc.LandingPage ?? Pages.FirstOrDefault()?.Name}";

    [RelayCommand]
    void SetLandingPage(PageTabViewModel tab)
    {
        _doc.LandingPage = tab.Name;
        UpdateLandingPage();
        RaiseChanged();
    }

    void UpdateLandingPage()
    {
        var landing = _doc.LandingPage ?? Pages.FirstOrDefault()?.Name;
        foreach (var p in Pages) p.IsLanding = p.Name == landing;
        OnPropertyChanged(nameof(LandingPageName));
        OnPropertyChanged(nameof(LandingPageText));
    }

    // ---- Performance analyzer ----

    [ObservableProperty]
    public partial bool IsRecording { get; set; }

    public ObservableCollection<PerformanceEntryViewModel> Performance { get; } = [];

    void OnVisualQueried(VisualViewModel visual, TimeSpan elapsed)
    {
        if (IsRecording) Performance.Insert(0, new PerformanceEntryViewModel(visual, elapsed.TotalMilliseconds));
    }

    /// <summary>Called by the canvas after it draws a visual, so its row gets a display time.</summary>
    public void ReportDisplayTime(VisualViewModel visual, TimeSpan elapsed)
    {
        if (IsRecording && Performance.FirstOrDefault(e => e.Visual == visual) is { DisplayMs: null } entry)
            entry.DisplayMs = elapsed.TotalMilliseconds;
    }

    [RelayCommand]
    void StartRecording() => IsRecording = true;

    [RelayCommand]
    void StopRecording() => IsRecording = false;

    /// <summary>Re-runs every visual on the page while recording, as Power BI's "Refresh visuals" does.</summary>
    [RelayCommand]
    void RefreshVisuals()
    {
        IsRecording = true;
        foreach (var v in Visuals) v.Refresh();
    }

    [RelayCommand]
    void ClearPerformance() => Performance.Clear();
}
