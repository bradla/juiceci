using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tessera.App.Services;
using Tessera.Core;

namespace Tessera.App.ViewModels;

/// <summary>Non-overlapping date-modified buckets, so several can be ticked at once.</summary>
public enum RecentAge { Today, PastWeek, PastMonth, Older }

/// <summary>A checkbox in the recent-reports Filter menu.</summary>
public sealed partial class RecentFilterOption(string label, Func<RecentFile, bool> matches, Action changed) : ObservableObject
{
    public string Label { get; } = label;
    public Func<RecentFile, bool> Matches { get; } = matches;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    partial void OnIsCheckedChanged(bool value) => changed();
}

/// <summary>An entry in the top-bar search: a command, report, table or field.</summary>
public sealed record SearchItem(string Label, string Group, Action Run)
{
    public override string ToString() => Label;
}

public partial class MainViewModel : ObservableObject
{
    public const string AppName = "JuiceCI";

    public DataEngine Engine { get; } = new();
    public ReportViewModel Report { get; }
    public RecentFiles Recent { get; }
    public AppSettings Settings { get; }

    /// <summary>Set by the main window once it exists.</summary>
    public IDialogs? Dialogs { get; set; }

    public MainViewModel(AppSettings? settings = null, RecentFiles? recent = null, RecentFiles? recentSources = null)
    {
        Settings = settings ?? new AppSettings();
        Recent = recent ?? new RecentFiles();
        RecentSources = recentSources ?? new RecentFiles(Path.Combine(Settings.Folder, "recent-sources.json"));
        Report = new ReportViewModel(Engine);
        _recoveryTimer.Tick += (_, _) => SaveRecoveryCopy();
        IsHome = Settings.Values.ShowHomeOnStart;
        ApplyPreferences();
        Report.Changed += (_, _) => IsDirty = true;
        Report.MeasureEditor = EditMeasureAsync;
        Report.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ReportViewModel.IsReportView)) OnPropertyChanged(nameof(IsReportPage));
            if (e.PropertyName == nameof(ReportViewModel.FileSettings)) UpdateImportOptions();
        };
        Engine.TablesChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            Report.RefreshTables();
            OnPropertyChanged(nameof(RoleNames));
        });
        RecentAgeFilters = BuildAgeFilters();
        RebuildFolderFilters();
        Recent.Items.CollectionChanged += (_, _) =>
        {
            RebuildFolderFilters();
            OnRecentChanged();
        };
        if (RecoveredCount() is > 0 and var n)
            Status = $"{n} auto-recovered report{(n == 1 ? " is" : "s are")} waiting: Options › Save and Recover › Open auto recovery folder";
    }

    // ---- Theme ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLightTheme), nameof(IsDarkTheme), nameof(IsSystemTheme))]
    public partial AppTheme Theme { get; set; }

    public bool IsLightTheme => Theme == AppTheme.Light;
    public bool IsDarkTheme => Theme == AppTheme.Dark;
    public bool IsSystemTheme => Theme == AppTheme.System;

    partial void OnThemeChanged(AppTheme value)
    {
        ApplyTheme();
        if (Settings.Values.Theme == value) return;
        Settings.Values = Settings.Values with { Theme = value };
        Settings.Save();
    }

    /// <summary>Pushes the saved preferences into the engine, the report and the process culture.</summary>
    void ApplyPreferences()
    {
        var p = Settings.Values;
        Theme = p.Theme;
        ApplyTheme();
        UpdateImportOptions();
        var threads = p.EngineThreads > 0 ? p.EngineThreads : OptionsViewModel.DefaultEvaluations;
        Engine.SetResourceLimits(p.EngineThreads, p.EngineMemoryMb > 0 ? p.EngineMemoryMb * threads : null);
        Engine.SetTempDirectory(Path.Combine(Settings.Folder, "Cache"), p.DataCacheMb);
        Report.GridSize = p.SnapToGrid ? p.GridSize : 0;
        Report.SmartGuides = p.SmartGuides;
        Report.AlignPageTop = p.AlignPageTop;
        Report.CopyObjectNames = p.CopyObjectNames;
        Report.DefaultDateGrain = p.AutoDateTime ? p.DefaultDateGrain : DateGrain.None;
        Recent.Max = p.RecentLimit;

        var o = p.Options;
        Report.SetPreviewVisuals(OptionsCatalog.Flag(o, OptionsCatalog.PreviewDecompositionTree),
            OptionsCatalog.Flag(o, OptionsCatalog.PreviewKeyInfluencers));
        Report.ShowWhitespace = OptionsCatalog.Flag(o, OptionsCatalog.ShowWhitespace);
        Report.MonospacedPreview = OptionsCatalog.Flag(o, OptionsCatalog.MonospacedPreview);
        CrashLog.Install();
        CrashLog.Folder = OptionsCatalog.Flag(o, OptionsCatalog.CrashDumps) ? Path.Combine(Settings.Folder, "Traces") : null;
        Engine.Trace = OptionsCatalog.Flag(o, OptionsCatalog.Tracing) ? TraceTo(_traceFile ??= NewTraceFile()) : null;
        _recoveryTimer.Interval = TimeSpan.FromMinutes(Math.Max(1, OptionsCatalog.Number(o, OptionsCatalog.AutoRecoveryMinutes)));
        _recoveryTimer.IsEnabled = OptionsCatalog.Flag(o, OptionsCatalog.AutoRecovery);

        var culture = p.Culture.Length > 0 ? CultureInfo.GetCultureInfo(p.Culture) : OptionsViewModel.SystemCulture;
        if (!Equals(CultureInfo.CurrentCulture, culture))
        {
            // Background imports and queries format with the same culture as the UI.
            CultureInfo.CurrentCulture = CultureInfo.DefaultThreadCurrentCulture = culture;
            Report.RefreshTables();
            OnRecentChanged();
        }
    }

    /// <summary>Type Detection: always, never, or as the report's Current file setting says.</summary>
    void UpdateImportOptions()
    {
        var p = Settings.Values;
        var detect = p.TypeDetection switch
        {
            FileDefault.Always => true,
            FileDefault.Never => false,
            _ => OptionsCatalog.Flag(Report.FileSettings, FileSettings.TypeDetection),
        };
        Engine.Import = new ImportOptions(detect, p.AllExcelSheets);
    }

    [RelayCommand]
    Task OptionsAsync() => ShowOptionsAsync(pageSettings: false);

    /// <summary>Options, opened at the current file's Page settings.</summary>
    [RelayCommand]
    Task PageSettingsAsync() => ShowOptionsAsync(pageSettings: true);

    /// <summary>Design › Page size › Custom: the Page settings in Options.</summary>
    [RelayCommand]
    Task CustomPageSizeAsync() => ShowOptionsAsync(pageSettings: true);

    async Task ShowOptionsAsync(bool pageSettings)
    {
        if (Dialogs is null) return;
        var options = new OptionsViewModel(Settings.Values, Report.PageName, Report.PageWidth, Report.PageHeight, Report.FileSettings)
        {
            SettingsFolder = Settings.Folder,
        };
        if (pageSettings) options.SelectedFilePage = OptionsViewModel.FilePages.First(p => p.Key == "page");
        if (!await Dialogs.ShowOptionsAsync(options)) return;
        ApplyOptions(options);
    }

    // ---- Ribbon ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHomeTab), nameof(IsInsertTab), nameof(IsModelingTab), nameof(IsDesignTab), nameof(IsViewTab),
        nameof(IsOptimizeTab), nameof(IsHelpTab))]
    public partial string RibbonTab { get; set; } = "Home";

    public bool IsHomeTab => RibbonTab == "Home";
    public bool IsInsertTab => RibbonTab == "Insert";
    public bool IsModelingTab => RibbonTab == "Modeling";
    public bool IsDesignTab => RibbonTab == "Design";
    public bool IsViewTab => RibbonTab == "View";
    public bool IsOptimizeTab => RibbonTab == "Optimize";
    public bool IsHelpTab => RibbonTab == "Help";

    [ObservableProperty]
    public partial bool IsRibbonExpanded { get; set; } = true;

    [RelayCommand]
    void SelectRibbonTab(string tab)
    {
        // Clicking the current tab folds the ribbon away, as in Office.
        if (tab == RibbonTab && IsRibbonExpanded) IsRibbonExpanded = false;
        else (RibbonTab, IsRibbonExpanded) = (tab, true);
    }

    [RelayCommand]
    void ToggleRibbon() => IsRibbonExpanded = !IsRibbonExpanded;

    /// <summary>The report canvas is showing, so page position and zoom apply.</summary>
    public bool IsReportPage => !IsHome && Report.IsReportView;

    public bool SnapToGrid
    {
        get => Settings.Values.SnapToGrid;
        set
        {
            if (value == SnapToGrid) return;
            Settings.Values = Settings.Values with { SnapToGrid = value };
            Settings.Save();
            Report.GridSize = value ? Settings.Values.GridSize : 0;
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    async Task InsertImageAsync()
    {
        if (Dialogs is null || await Dialogs.PickImageAsync() is not { } path) return;
        try
        {
            Report.ShowReportViewCommand.Execute(null);
            Report.InsertImage(await File.ReadAllBytesAsync(path));
        }
        catch (IOException e)
        {
            await Dialogs.ShowErrorAsync("Couldn't add the image", e.Message);
        }
    }

    [RelayCommand]
    Task ShortcutsAsync() => Dialogs?.ShowMessageAsync("Keyboard shortcuts",
        "Ctrl+N  New report\nCtrl+O  Open\nCtrl+S  Save    Ctrl+Shift+S  Save as\nCtrl+Z  Undo    Ctrl+Y / Ctrl+Shift+Z  Redo\n" +
        "Ctrl+C / Ctrl+X / Ctrl+V  Copy, cut and paste visuals\nDelete  Remove the selected visual\nEsc  Deselect\nCtrl+F  Search")
        ?? Task.CompletedTask;

    // ---- Measures ----

    async Task EditMeasureAsync(string? table, Measure? existing)
    {
        if (Dialogs is null) return;
        var tables = Engine.Tables.Select(t => t.Name).ToList();
        if (tables.Count == 0)
        {
            await Dialogs.ShowMessageAsync("New measure", "Load some data first: a measure summarizes the rows of a table.");
            return;
        }
        var editor = new MeasureViewModel(Engine, tables, table, existing);
        if (!await Dialogs.ShowMeasureAsync(editor)) return;
        Engine.Model = editor.ApplyTo(Engine.Model);
        IsDirty = true;
        Status = existing is null ? $"Added measure [{editor.Name}]" : $"Updated measure [{editor.Name}]";
    }

    /// <summary>Saves the dialog's Global choices and applies its Current file ones to the report.</summary>
    public void ApplyOptions(OptionsViewModel options)
    {
        Settings.Values = options.ToPreferences(Settings.Values);
        Settings.Save();
        ApplyPreferences();
        Report.SetFileSettings(options.FileValues);
        Report.SetPage(options.PageName, (double)(options.PageWidth ?? (decimal)Report.PageWidth),
            (double)(options.PageHeight ?? (decimal)Report.PageHeight));
        Status = "Options saved";
    }

    // ---- Diagnostics: tracing ----

    string? _traceFile;

    string NewTraceFile() => Path.Combine(Settings.Folder, "Traces", $"juiceci-{DateTime.Now:yyyyMMdd-HHmmss}.log");

    /// <summary>Appends each engine statement to the session's trace log. The engine runs one statement at a time.</summary>
    static Action<string> TraceTo(string file) => sql =>
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}\t{sql.ReplaceLineEndings(" ")}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    };

    // ---- Save and Recover ----

    readonly DispatcherTimer _recoveryTimer = new();
    string? _recoveryFile;

    /// <summary>Where unsaved changes are copied: the chosen location, else AutoRecovery beside the settings.</summary>
    public string RecoveryFolder =>
        OptionsCatalog.Folder(Settings.Values.Options, OptionsCatalog.RecoveryLocation, Path.Combine(Settings.Folder, "AutoRecovery"));

    /// <summary>Where a report's previous version goes when a save replaces it.</summary>
    public string BackupFolder => Path.Combine(Settings.Folder, "Backup");

    int RecoveredCount()
    {
        try
        {
            return Directory.Exists(RecoveryFolder)
                ? Directory.EnumerateFiles(RecoveryFolder, "*" + ProjectFile.Extension).Count() : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Copies a report with unsaved changes to the auto recovery folder, unless its Current file settings
    /// opt out. Returns the copy's path, or null when nothing was saved.
    /// </summary>
    public string? SaveRecoveryCopy()
    {
        if (!IsDirty || IsBusy || OptionsCatalog.Flag(Report.FileSettings, FileSettings.DisableAutoRecovery)) return null;
        var path = Path.Combine(RecoveryFolder, DocumentName + ProjectFile.Extension);
        try
        {
            Directory.CreateDirectory(RecoveryFolder);
            ProjectFile.Save(path, Engine, Report.ToDocument());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
        if (_recoveryFile != path) DeleteRecoveryCopy();
        _recoveryFile = path;
        return path;
    }

    /// <summary>
    /// The user chose not to save. The recovery copy goes, unless Save and Recover says to keep
    /// the last version; then it's brought up to date and left for good.
    /// </summary>
    public void DiscardChanges()
    {
        if (OptionsCatalog.Flag(Settings.Values.Options, OptionsCatalog.KeepRecoveryOnClose))
        {
            SaveRecoveryCopy();
            _recoveryFile = null;
        }
        else
        {
            DeleteRecoveryCopy();
        }
    }

    /// <summary>Copies the report a save is about to replace into the backup folder, one version per name.</summary>
    void BackUp(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            Directory.CreateDirectory(BackupFolder);
            File.Copy(path, Path.Combine(BackupFolder, Path.GetFileName(path)), overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A failed backup mustn't stop the save itself.
        }
    }

    /// <summary>The changes were saved or deliberately thrown away, so the copy isn't needed.</summary>
    void DeleteRecoveryCopy()
    {
        if (_recoveryFile is null) return;
        try
        {
            File.Delete(_recoveryFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        _recoveryFile = null;
    }

    partial void OnIsDirtyChanged(bool value)
    {
        if (!value) DeleteRecoveryCopy();
    }

    void ApplyTheme()
    {
        if (Application.Current is not { } app) return;
        app.RequestedThemeVariant = Theme switch
        {
            AppTheme.Dark => ThemeVariant.Dark,
            AppTheme.System => ThemeVariant.Default,
            _ => ThemeVariant.Light,
        };
    }

    [RelayCommand]
    void SetTheme(AppTheme theme) => Theme = theme;

    // ---- Recent reports on the start screen ----

    [ObservableProperty]
    public partial string RecentQuery { get; set; } = "";

    /// <summary>Date-modified checkboxes; ticking none means any date.</summary>
    public IReadOnlyList<RecentFilterOption> RecentAgeFilters { get; }

    /// <summary>One checkbox per folder holding a recent report; ticking none means any folder.</summary>
    public ObservableCollection<RecentFilterOption> RecentFolderFilters { get; } = [];

    public static RecentAge AgeOf(DateTime modified) =>
        modified >= DateTime.Today ? RecentAge.Today
        : modified >= DateTime.Today.AddDays(-7) ? RecentAge.PastWeek
        : modified >= DateTime.Today.AddDays(-30) ? RecentAge.PastMonth
        : RecentAge.Older;

    IReadOnlyList<RecentFilterOption> BuildAgeFilters() =>
    [
        new("Today", r => AgeOf(r.LastWrite) == RecentAge.Today, OnRecentChanged),
        new("Earlier this week", r => AgeOf(r.LastWrite) == RecentAge.PastWeek, OnRecentChanged),
        new("Earlier this month", r => AgeOf(r.LastWrite) == RecentAge.PastMonth, OnRecentChanged),
        new("Older", r => AgeOf(r.LastWrite) == RecentAge.Older, OnRecentChanged),
    ];

    void RebuildFolderFilters()
    {
        var ticked = RecentFolderFilters.Where(f => f.IsChecked).Select(f => f.Label).ToHashSet();
        RecentFolderFilters.Clear();
        foreach (var folder in Recent.Items.Select(r => r.Folder).Distinct())
            RecentFolderFilters.Add(new(folder, r => r.Folder == folder, OnRecentChanged) { IsChecked = ticked.Contains(folder) });
    }

    public int ActiveRecentFilterCount =>
        RecentAgeFilters.Count(f => f.IsChecked) + RecentFolderFilters.Count(f => f.IsChecked);

    public string RecentFilterLabel => ActiveRecentFilterCount == 0 ? "Filter" : $"Filter ({ActiveRecentFilterCount})";

    [RelayCommand]
    void ClearRecentFilters()
    {
        foreach (var f in RecentAgeFilters.Concat(RecentFolderFilters)) f.IsChecked = false;
    }

    public IReadOnlyList<RecentFile> FilteredRecent
    {
        get
        {
            static bool Passes(IEnumerable<RecentFilterOption> group, RecentFile r)
            {
                var ticked = group.Where(f => f.IsChecked).ToList();
                return ticked.Count == 0 || ticked.Any(f => f.Matches(r));
            }
            var q = RecentQuery.Trim();
            return Recent.Items
                .Where(r => Passes(RecentAgeFilters, r) && Passes(RecentFolderFilters, r))
                .Where(r => q.Length == 0 || r.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                    || r.Folder.Contains(q, StringComparison.CurrentCultureIgnoreCase))
                .ToList();
        }
    }

    public bool HasRecent => Recent.Items.Count > 0;
    public bool NoRecentMatches => HasRecent && FilteredRecent.Count == 0;
    public RecentFile? LastReport => Recent.Items.FirstOrDefault(r => File.Exists(r.Path));

    partial void OnRecentQueryChanged(string value) => OnRecentChanged();

    void OnRecentChanged()
    {
        OnPropertyChanged(nameof(FilteredRecent));
        OnPropertyChanged(nameof(ActiveRecentFilterCount));
        OnPropertyChanged(nameof(RecentFilterLabel));
        OnPropertyChanged(nameof(HasRecent));
        OnPropertyChanged(nameof(NoRecentMatches));
        OnPropertyChanged(nameof(LastReport));
    }

    [RelayCommand]
    void ClearRecent()
    {
        Recent.Clear();
        Status = "Cleared the recent reports list";
    }

    [RelayCommand]
    Task AboutAsync() => Dialogs?.ShowMessageAsync($"About {AppName}",
        $"{AppName} {typeof(MainViewModel).Assembly.GetName().Version?.ToString(3)}\n\n" +
        "A desktop BI report builder: load data, drag fields onto a page, and build charts, tables and cards.")
        ?? Task.CompletedTask;

    // ---- Search ----

    /// <summary>Everything the top-bar search can jump to, built fresh each time it opens.</summary>
    public IReadOnlyList<SearchItem> BuildSearchItems()
    {
        var items = new List<SearchItem>
        {
            new("Blank report", "Command", () => NewReportCommand.Execute(null)),
            new("Open report…", "Command", () => OpenCommand.Execute(null)),
            new("Save", "Command", () => SaveCommand.Execute(null)),
            new("Save as…", "Command", () => SaveAsCommand.Execute(null)),
            new("Undo", "Command", () => Report.UndoCommand.Execute(null)),
            new("Redo", "Command", () => Report.RedoCommand.Execute(null)),
            new("Get data: Excel workbook", "Command", () => GetDataCommand.Execute("excel")),
            new("Get data: Text / CSV", "Command", () => GetDataCommand.Execute("text")),
            new("Get data: Parquet", "Command", () => GetDataCommand.Execute("parquet")),
            new("Get data: JSON", "Command", () => GetDataCommand.Execute("json")),
            new("Get data: Power BI file", "Command", () => GetDataCommand.Execute("powerbi")),
            new("Load sample data", "Command", () => LoadSampleCommand.Execute(null)),
            new("Getting started: build a sample report", "Command", () => GettingStartedCommand.Execute(null)),
            new("Home", "View", () => IsHome = true),
            new("Report view", "View", () => { IsHome = false; Report.ShowReportViewCommand.Execute(null); }),
            new("Table view", "View", () => { IsHome = false; Report.ShowDataViewCommand.Execute(null); }),
            new("Theme: Light", "Setting", () => Theme = AppTheme.Light),
            new("Theme: Dark", "Setting", () => Theme = AppTheme.Dark),
            new("Theme: System default", "Setting", () => Theme = AppTheme.System),
            new("Options and settings…", "Setting", () => OptionsCommand.Execute(null)),
        };
        foreach (var r in Recent.Items)
            items.Add(new(r.Name, "Recent report", () => OpenRecentCommand.Execute(r)));
        foreach (var t in Report.Tables)
        {
            items.Add(new(t.Name, "Table", () =>
            {
                IsHome = false;
                Report.ShowDataViewCommand.Execute(null);
                Report.PreviewTable = t;
            }));
            foreach (var f in t.Fields)
                items.Add(new($"{t.Name}[{f.Name}]", "Field", () =>
                {
                    IsHome = false;
                    Report.ShowReportViewCommand.Execute(null);
                    Report.AddField(f.Info, Report.Selected);
                }));
        }
        return items;
    }

    [RelayCommand]
    async Task GettingStartedAsync()
    {
        if (!await ConfirmDiscardAsync()) return;
        Engine.Clear();
        Report.Load(new ReportDocument());
        FilePath = null;
        ImportedName = null;
        LastSaved = null;
        var table = await RunBusyAsync("Building a sample report…", () => Engine.LoadSampleData());
        if (table is null) return;
        Report.RefreshTables();
        Report.BuildStarterReport(table);
        Report.ResetHistory();
        IsDirty = true;
        IsHome = false;
        Status = "Sample report ready: select a visual to see how it's built, or drag more fields onto the page";
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(DocumentName))]
    public partial string? FilePath { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(SavedText))]
    public partial bool IsDirty { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReportPage))]
    public partial bool IsHome { get; set; } = true;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "Ready";

    /// <summary>Name of an opened Power BI file, until the report is saved as a JuiceCI report.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(DocumentName))]
    public partial string? ImportedName { get; set; }

    /// <summary>When the report was last saved or opened from disk.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SavedText))]
    public partial DateTime? LastSaved { get; set; }

    public string SavedText => LastSaved is not { } t || IsDirty ? (IsDirty ? "•  Unsaved changes" : "")
        : $"•  Last saved: {(t.Date == DateTime.Today ? "Today" : t.ToString("d"))} at {t:t}";

    public string DocumentName => FilePath is not null ? Path.GetFileNameWithoutExtension(FilePath) : ImportedName ?? "Untitled";

    public string Title => $"{(IsDirty ? "● " : "")}{DocumentName} - {AppName}";

    [RelayCommand]
    void ShowHome() => IsHome = true;

    [RelayCommand]
    void ShowReport() => IsHome = false;

    [RelayCommand]
    async Task NewReportAsync()
    {
        if (!await ConfirmDiscardAsync()) return;
        Engine.Clear();
        Report.Load(new ReportDocument());
        FilePath = null;
        ImportedName = null;
        LastSaved = null;
        IsDirty = false;
        IsHome = false;
        Status = "New report";
    }

    [RelayCommand]
    async Task GetDataAsync(string? kind)
    {
        if (Dialogs is null) return;
        var files = await Dialogs.PickDataFilesAsync(kind ?? "any");
        if (files.Count > 0) await ImportFilesAsync(files);
    }

    /// <summary>The start screen's Power BI file card: opens a .pbix as the report, pages and all, like Power BI's Open.</summary>
    [RelayCommand]
    async Task OpenPowerBiFileAsync()
    {
        if (Dialogs is null) return;
        if ((await Dialogs.PickDataFilesAsync("powerbi")).FirstOrDefault() is { } path) await OpenPbixAsync(path);
    }

    [RelayCommand]
    async Task LoadSampleAsync()
    {
        var name = await RunBusyAsync("Loading sample data…", () => Engine.LoadSampleData());
        if (name is null) return;
        AfterImport([name]);
    }

    public async Task ImportFilesAsync(IEnumerable<string> paths)
    {
        // A Power BI file picked into an empty report opens as the report itself, pages and all.
        if (paths.ToList() is [var single] && single.EndsWith(".pbix", StringComparison.OrdinalIgnoreCase)
            && Report.ToDocument().Pages.All(p => p.Visuals.Count == 0))
        {
            await OpenPbixAsync(single);
            return;
        }
        var created = new List<string>();
        foreach (var path in paths)
        {
            var names = await RunBusyAsync($"Importing {Path.GetFileName(path)}…", () => Engine.ImportFile(path));
            if (names is null) return;
            created.AddRange(names);
            RecentSources.Add(Path.GetFullPath(path));
        }
        AfterImport(created);
    }

    // ---- Home ribbon: data ----

    /// <summary>Data files imported lately, for Home › Recent sources.</summary>
    public RecentFiles RecentSources { get; }

    [RelayCommand]
    Task ImportRecentSourceAsync(RecentFile source) => ImportFilesAsync([source.Path]);

    /// <summary>Home › Enter data: a small table typed or pasted in.</summary>
    [RelayCommand]
    async Task EnterDataAsync()
    {
        if (Dialogs is null) return;
        var data = new EnterDataViewModel();
        if (!await Dialogs.ShowEnterDataAsync(data)) return;
        var name = await RunBusyAsync($"Loading {data.Name}…", () => Engine.ImportText(data.Name, data.Text));
        if (name is not null) AfterImport([name]);
    }

    /// <summary>Home › Refresh: reads every table again from the file it came from.</summary>
    [RelayCommand]
    async Task RefreshAsync()
    {
        var refreshed = await RunBusyAsync("Refreshing data…", () => Engine.RefreshSources());
        if (refreshed is null) return;
        if (refreshed.Count == 0)
        {
            Status = "Nothing to refresh: only tables loaded from CSV, text, Parquet or JSON files can be read again";
            return;
        }
        Report.RefreshTables();
        IsDirty = true;
        Status = refreshed.Count == 1 ? $"Refreshed {refreshed[0]}" : $"Refreshed {refreshed.Count} tables";
    }

    // ---- Design ribbon ----

    [RelayCommand]
    async Task CustomizeThemeAsync()
    {
        if (Dialogs is null) return;
        var editor = new CustomizeThemeViewModel(Report.Theme);
        if (!await Dialogs.ShowCustomizeThemeAsync(editor)) return;
        Report.ApplyTheme(editor.ToTheme());
        Status = $"Applied theme {editor.Name}";
    }

    /// <summary>Saves the report's theme as a Power BI theme file.</summary>
    [RelayCommand]
    async Task DownloadThemeAsync()
    {
        if (Dialogs is null || await Dialogs.PickThemeSavePathAsync(Report.Theme.Name + ".json") is not { } path) return;
        var json = Report.Theme.ToJson();
        if (await RunBusyAsync("Saving theme…", () => { File.WriteAllText(path, json); return true; })) Status = $"Saved theme to {path}";
    }

    [RelayCommand]
    async Task ImportThemeAsync()
    {
        if (Dialogs is null || await Dialogs.PickThemeFileAsync() is not { } path) return;
        var theme = await RunBusyAsync($"Reading {Path.GetFileName(path)}…", () => ReportTheme.FromJson(File.ReadAllText(path)));
        if (theme is null) return;
        Report.ApplyTheme(theme);
        Status = $"Applied theme {theme.Name}";
    }

    [RelayCommand]
    void HowToCreateTheme() =>
        Dialogs?.OpenUrl("https://learn.microsoft.com/power-bi/create-reports/desktop-report-themes");

    // ---- Optimize ribbon ----

    /// <summary>Optimize › Optimization presets: query reduction, interactivity, or the options to customize.</summary>
    [RelayCommand]
    async Task OptimizationPresetAsync(string preset)
    {
        switch (preset)
        {
            case "QueryReduction":
                Report.ApplyOptimizationPreset(reduceQueries: true);
                Status = "Query reduction preset: cross highlighting off, Apply buttons for slicers and filters";
                break;
            case "Interactivity":
                Report.ApplyOptimizationPreset(reduceQueries: false);
                Status = "Interactivity preset: cross highlighting on, filters apply instantly";
                break;
            default:
                if (Dialogs is null) return;
                var options = new OptionsViewModel(Settings.Values, Report.PageName, Report.PageWidth, Report.PageHeight, Report.FileSettings)
                {
                    SettingsFolder = Settings.Folder,
                };
                options.SelectedFilePage = OptionsViewModel.FilePages.First(p => p.Key == "fquery");
                if (await Dialogs.ShowOptionsAsync(options)) ApplyOptions(options);
                break;
        }
    }

    // ---- Modeling ribbon ----

    [RelayCommand]
    async Task ManageRelationshipsAsync()
    {
        if (Dialogs is null) return;
        var relationships = new RelationshipsViewModel(Engine);
        if (!await Dialogs.ShowRelationshipsAsync(relationships)) return;
        Engine.Model = relationships.ApplyTo(Engine.Model);
        Report.RefreshTables();
        IsDirty = true;
        Status = $"{Engine.Model.Relationships.Count} relationship{(Engine.Model.Relationships.Count == 1 ? "" : "s")}";
    }

    [RelayCommand]
    Task NewColumnAsync() => NewCalculationAsync(isTable: false);

    [RelayCommand]
    Task NewTableAsync() => NewCalculationAsync(isTable: true);

    async Task NewCalculationAsync(bool isTable)
    {
        if (Dialogs is null) return;
        if (!isTable && Engine.Tables.All(t => t.Fields.All(f => f.IsMeasure)))
        {
            await Dialogs.ShowMessageAsync("New column", "Load some data first: a column is worked out for each row of a table.");
            return;
        }
        var calc = new CalculationViewModel(Engine, isTable, Report.Selected?.Spec.Category.FirstOrDefault()?.Table);
        if (!await Dialogs.ShowCalculationAsync(calc)) return;
        var name = calc.Name.Trim();
        var done = await RunBusyAsync($"Adding {name}…", () =>
        {
            if (isTable) return Engine.AddCalculatedTable(name, calc.Expression);
            Engine.AddCalculatedColumn(calc.Table, name, calc.Expression);
            return calc.Table;
        });
        if (done is null) return;
        Report.RefreshTables();
        Report.ExpandTables([done]);
        IsDirty = true;
        Status = isTable ? $"Added table {done}" : $"Added column {calc.Table}[{name}]";
    }

    [RelayCommand]
    async Task ManageRolesAsync()
    {
        if (Dialogs is null) return;
        var roles = new RolesViewModel(Engine);
        if (!await Dialogs.ShowRolesAsync(roles)) return;
        Engine.Model = roles.ApplyTo(Engine.Model);
        if (ViewingAs is not null && Engine.Model.Roles.All(r => r.Name != ViewingAs)) ViewAs(null);
        else Report.RefreshTables();
        IsDirty = true;
        OnPropertyChanged(nameof(RoleNames));
    }

    /// <summary>The roles View as offers.</summary>
    public IReadOnlyList<string> RoleNames => Engine.Model.Roles.Select(r => r.Name).ToList();

    /// <summary>Modeling › View as: every visual shows only the rows the role may see.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsViewingAs), nameof(ViewingAsText))]
    public partial string? ViewingAs { get; private set; }

    public bool IsViewingAs => ViewingAs is not null;
    public string ViewingAsText => $"Now viewing report as: {ViewingAs}";

    [RelayCommand]
    void ViewAs(string? role)
    {
        Engine.ViewAs = role;
        ViewingAs = role;
        Report.RefreshTables();
        Status = role is null ? "Stopped viewing as a role" : $"Viewing as {role}";
    }

    /// <summary>Home › Quick measure: opens the measure editor with the chosen calculation written out.</summary>
    [RelayCommand]
    async Task QuickMeasureAsync(string key)
    {
        if (Dialogs is null) return;
        var preferred = Report.Selected?.Spec.Values.FirstOrDefault(v => !v.IsMeasure);
        if (QuickMeasures.Build(key, Engine, preferred) is not { } draft)
        {
            await Dialogs.ShowMessageAsync("Quick measure",
                "Load a table with a number column (and a text column, for the filtered calculations) first.");
            return;
        }
        var editor = new MeasureViewModel(Engine, Engine.Tables.Select(t => t.Name).ToList(), draft.Table, null)
        {
            Name = draft.Name, Expression = draft.Expression,
        };
        if (MeasureViewModel.Formats.FirstOrDefault(f => f.FormatString == draft.FormatString) is { } format) editor.Format = format;
        if (!await Dialogs.ShowMeasureAsync(editor)) return;
        Engine.Model = editor.ApplyTo(Engine.Model);
        IsDirty = true;
        Status = $"Added measure [{editor.Name}]";
    }

    void AfterImport(IReadOnlyList<string> tables)
    {
        Report.RefreshTables();
        Report.ExpandTables(tables);
        IsDirty = true;
        IsHome = false;
        Status = tables.Count switch
        {
            0 => "No tables found in that file",
            1 => $"Loaded table {tables[0]}",
            _ => $"Loaded {tables.Count} tables: {string.Join(", ", tables)}",
        };
    }

    [RelayCommand]
    async Task OpenAsync()
    {
        if (Dialogs is null) return;
        var path = await Dialogs.PickProjectAsync();
        if (path is not null) await OpenPathAsync(path);
    }

    [RelayCommand]
    Task OpenRecentAsync(RecentFile file) => OpenPathAsync(file.Path);

    /// <summary>Opens a project or Power BI report, or imports a data file into the current report.</summary>
    public async Task OpenPathAsync(string path)
    {
        if (Path.GetExtension(path).Equals(".pbix", StringComparison.OrdinalIgnoreCase))
        {
            await OpenPbixAsync(path);
            return;
        }
        if (DataEngine.SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
        {
            await ImportFilesAsync([path]);
            return;
        }
        if (!await ConfirmDiscardAsync()) return;
        var doc = await RunBusyAsync($"Opening {Path.GetFileName(path)}…", () => ProjectFile.Load(path, Engine));
        if (doc is null) return;
        Report.Load(doc);
        FilePath = path;
        LastSaved = File.GetLastWriteTime(path);
        ImportedName = null;
        IsDirty = false;
        IsHome = false;
        Recent.Add(path);
        Status = $"Opened {path}";
    }

    /// <summary>Opens a Power BI Desktop file as a new report: its model, measures and pages.</summary>
    async Task OpenPbixAsync(string path)
    {
        if (!await ConfirmDiscardAsync()) return;
        var file = Path.GetFileName(path);
        var doc = await RunBusyAsync($"Opening {file}…", () =>
        {
            Engine.Clear();
            Engine.ImportPbix(path);
            return PbixReportImporter.Read(path, Engine.Tables) ?? new ReportDocument();
        });
        Report.Load(doc ?? new ReportDocument());
        FilePath = null;
        ImportedName = doc is null ? null : Path.GetFileNameWithoutExtension(path);
        LastSaved = doc is null ? null : File.GetLastWriteTime(path);
        IsDirty = false;
        if (doc is null) return;
        IsHome = false;
        Recent.Add(path);
        var pages = doc.Pages.Count;
        var unsupported = doc.Pages.Sum(p => p.Visuals.Count(v => v.Type == VisualType.Unsupported));
        Status = $"Opened {file}: {pages} page{(pages == 1 ? "" : "s")}"
            + (unsupported > 0 ? $", {unsupported} visual{(unsupported == 1 ? "" : "s")} JuiceCI can't show yet" : "")
            + ". Save as a JuiceCI report to keep changes.";
    }

    [RelayCommand]
    async Task SaveAsync()
    {
        if (FilePath is null) await SaveAsAsync();
        else await SaveToAsync(FilePath);
    }

    [RelayCommand]
    async Task SaveAsAsync()
    {
        if (Dialogs is null) return;
        var folder = FilePath is not null ? Path.GetDirectoryName(FilePath)
            : OptionsCatalog.Folder(Settings.Values.Options, OptionsCatalog.DefaultSaveLocation, OptionsCatalog.DocumentsFolder);
        var path = await Dialogs.PickSavePathAsync(DocumentName + ProjectFile.Extension, folder);
        if (path is null) return;
        if (!path.EndsWith(ProjectFile.Extension, StringComparison.OrdinalIgnoreCase)) path += ProjectFile.Extension;
        await SaveToAsync(path);
    }

    async Task<bool> SaveToAsync(string path)
    {
        var doc = Report.ToDocument();
        var ok = await RunBusyAsync($"Saving {Path.GetFileName(path)}…", () =>
        {
            BackUp(path);
            ProjectFile.Save(path, Engine, doc);
            return true;
        });
        if (!ok) return false;
        FilePath = path;
        LastSaved = File.GetLastWriteTime(path);
        ImportedName = null;
        IsDirty = false;
        Recent.Add(path);
        Status = $"Saved {path}";
        return true;
    }

    /// <summary>Asks to save unsaved work. Returns false if the user cancelled.</summary>
    public async Task<bool> ConfirmDiscardAsync()
    {
        if (!IsDirty || Dialogs is null) return true;
        switch (await Dialogs.AskSaveChangesAsync(DocumentName))
        {
            case SaveChoice.Discard:
                DiscardChanges();
                return true;
            case SaveChoice.Cancel: return false;
        }
        await SaveAsync();
        return !IsDirty;
    }

    /// <summary>Runs work off the UI thread; on failure shows the error and returns default.</summary>
    async Task<T?> RunBusyAsync<T>(string message, Func<T> work)
    {
        IsBusy = true;
        Status = message;
        try
        {
            return await Task.Run(work);
        }
        catch (Exception e)
        {
            Status = e.Message;
            if (Dialogs is not null) await Dialogs.ShowErrorAsync("Something went wrong", e.Message);
            return default;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
