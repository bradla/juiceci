using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tessera.App.Services;
using Tessera.Core;

namespace Tessera.App.ViewModels;

public sealed record OptionsPage(string Key, string Label, bool IsCurrentFile);

public sealed record GrainChoice(DateGrain Grain, string Label);

public sealed record ThemeChoice(AppTheme Theme, string Label);

public sealed record CultureChoice(string Name, string Label);

public sealed record PageSizeChoice(string Label, double Width, double Height)
{
    public bool IsCustom => Width == 0;
}

/// <summary>
/// The Options dialog's working copy. Nothing changes until OK, when the owner reads
/// <see cref="ToPreferences"/> and the page fields back.
/// </summary>
public partial class OptionsViewModel : ObservableObject
{
    /// <summary>The culture the process started with, before any override. Declared first: <see cref="Cultures"/> reads it.</summary>
    public static CultureInfo SystemCulture { get; } = CultureInfo.CurrentCulture;

    /// <summary>Power BI Desktop's Global pages, in its order, then JuiceCI's own.</summary>
    public static IReadOnlyList<OptionsPage> GlobalPages { get; } =
    [
        new("data", "Data Load", false), new("pq", "Power Query Editor", false), new("directquery", "DirectQuery", false),
        new("r", "R scripting", false), new("python", "Python scripting", false), new("security", "Security", false),
        new("privacy", "Privacy", false), new("regional", "Regional Settings", false),
        new("diagnostics", "Diagnostics", false), new("preview", "Preview features", false),
        new("save", "Save and Recover", false), new("report", "Report settings", false),
        new("recent", "Recent files", false),
    ];

    public static IReadOnlyList<OptionsPage> FilePages { get; } =
    [
        new("fdata", "Data Load", true), new("fregional", "Regional Settings", true), new("fprivacy", "Privacy", true),
        new("frecovery", "Auto recovery", true), new("fpublished", "Published semantic model settings", true),
        new("fquery", "Query reduction", true), new("freport", "Report settings", true), new("page", "Page", true),
    ];

    public static IReadOnlyList<GrainChoice> Grains { get; } =
    [
        new(DateGrain.Year, "Year"), new(DateGrain.Quarter, "Quarter"), new(DateGrain.Month, "Month"),
        new(DateGrain.Day, "Day"), new(DateGrain.None, "Don't group (exact value)"),
    ];

    public static IReadOnlyList<CultureChoice> Cultures { get; } =
        new[] { "en-US", "en-GB", "en-IN", "de-DE", "fr-FR", "es-ES", "it-IT", "nl-NL", "pt-BR", "sv-SE", "pl-PL", "ja-JP", "zh-CN" }
            .Select(n => new CultureChoice(n, CultureInfo.GetCultureInfo(n).DisplayName))
            .Prepend(new CultureChoice("", $"Use system setting ({SystemCulture.DisplayName})"))
            .ToList();

    public static IReadOnlyList<PageSizeChoice> PageSizes { get; } =
    [
        new("16:9 (1280 × 720)", 1280, 720), new("4:3 (960 × 720)", 960, 720),
        new("Letter (816 × 1056)", 816, 1056), new("Custom", 0, 0),
    ];

    /// <param name="fileSettings">The report's Current file choices; null when it has none yet.</param>
    public OptionsViewModel(Preferences prefs, string pageName, double pageWidth, double pageHeight,
        IReadOnlyDictionary<string, string>? fileSettings = null)
    {
        GlobalValues = new Dictionary<string, string>(prefs.Options);
        FileValues = fileSettings is null ? [] : new Dictionary<string, string>(fileSettings);
        SelectedPage = GlobalPages[0];
        SelectedGlobalPage = GlobalPages[0];
        Theme = prefs.Theme;
        TypeDetection = prefs.TypeDetection;
        BackgroundData = prefs.BackgroundData;
        AllExcelSheets = prefs.AllExcelSheets;
        EngineThreads = prefs.EngineThreads > 0 ? prefs.EngineThreads : DefaultEvaluations;
        EngineMemoryMb = prefs.EngineMemoryMb > 0 ? prefs.EngineMemoryMb : DefaultMemoryPerEvaluation;
        AutoDateTime = prefs.AutoDateTime;
        DataCache.MaxMb = prefs.DataCacheMb;
        QueryCache.MaxMb = prefs.QueryCacheMb;
        ValueFilterIndependent = prefs.ValueFilterIndependent;
        DefaultGrain = Grains.FirstOrDefault(g => g.Grain == prefs.DefaultDateGrain) ?? Grains[2];
        SnapToGrid = prefs.SnapToGrid;
        SmartGuides = prefs.SmartGuides;
        AlignPageTop = prefs.AlignPageTop;
        ExpandFormatSubcategories = prefs.ExpandFormatSubcategories;
        ShowHomeOnStart = prefs.ShowHomeOnStart;
        CopyObjectNames = prefs.CopyObjectNames;
        GridSize = prefs.GridSize;
        Culture = Cultures.FirstOrDefault(c => c.Name == prefs.Culture) ?? Cultures[0];
        RecentLimit = prefs.RecentLimit;
        PageName = pageName;
        PageWidth = (decimal)pageWidth;
        PageHeight = (decimal)pageHeight;
        PageSize = PageSizes.FirstOrDefault(p => p.Width == pageWidth && p.Height == pageHeight) ?? PageSizes[^1];
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageKey), nameof(Sections))]
    public partial OptionsPage SelectedPage { get; set; }

    public string PageKey => SelectedPage.Key;

    /// <summary>Working copies of the catalog settings; written back only on OK.</summary>
    public Dictionary<string, string> GlobalValues { get; }
    public Dictionary<string, string> FileValues { get; }

    readonly Dictionary<string, IReadOnlyList<OptionSectionViewModel>> _sections = [];

    /// <summary>The current page's catalog settings, built once per page so drop-downs detect once.</summary>
    public IReadOnlyList<OptionSectionViewModel> Sections
    {
        get
        {
            var key = SelectedPage.Key;
            if (_sections.TryGetValue(key, out var built)) return built;
            if (!OptionsCatalog.Pages.TryGetValue(key, out var defs)) return [];
            var store = SelectedPage.IsCurrentFile ? FileValues : GlobalValues;
            var sections = defs.Select(s => new OptionSectionViewModel(s.Heading, s.Items.SelectMany(d => Item(store, key, d)).ToList(), s.Tip)).ToList();
            var checks = sections.SelectMany(s => s.Items).OfType<CheckItemViewModel>().ToList();
            foreach (var (def, child) in defs.SelectMany(s => s.Items).OfType<CheckDef>().Zip(checks))
                if (def.Parent is { } parent && checks.FirstOrDefault(c => c.Key == parent) is { } owner) child.Follow(owner);
            return _sections[key] = sections;
        }
    }

    static IEnumerable<OptionItemViewModel> Item(Dictionary<string, string> store, string page, OptionDef def) => def switch
    {
        CheckDef c => [new CheckItemViewModel(store, c)],
        RadioDef r => r.Labels.Select((label, i) => new RadioItemViewModel(store, r.Key, i, label, page + "." + r.Key, r.Tips?.ElementAtOrDefault(i))),
        ListDef l => [new ListItemViewModel(store, l)],
        NumberDef n => [new NumberItemViewModel(store, n)],
        TextDef t => [new TextItemViewModel(store, t)],
        NoteDef n => [new NoteItemViewModel(n.Text)],
        ActionDef a => [new ActionItemViewModel(store, a)],
        CheckNumberDef cn => [new CheckNumberItemViewModel(store, cn)],
        FolderDef f => [new FolderItemViewModel(store, f)],
        ToggleDef t => [new ToggleItemViewModel(store, t)],
        SubheadDef h => [new SubheadItemViewModel(h.Text)],
        _ => [],
    };

    // The page lists are two ListBoxes; selecting in one clears the other.
    [ObservableProperty]
    public partial OptionsPage? SelectedGlobalPage { get; set; }

    [ObservableProperty]
    public partial OptionsPage? SelectedFilePage { get; set; }

    partial void OnSelectedGlobalPageChanged(OptionsPage? value)
    {
        if (value is null) return;
        SelectedFilePage = null;
        SelectedPage = value;
    }

    partial void OnSelectedFilePageChanged(OptionsPage? value)
    {
        if (value is null) return;
        SelectedGlobalPage = null;
        SelectedPage = value;
    }

    // ---- Global ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLightTheme), nameof(IsDarkTheme), nameof(IsSystemTheme), nameof(ThemeOption))]
    public partial AppTheme Theme { get; set; }

    public bool IsLightTheme { get => Theme == AppTheme.Light; set { if (value) Theme = AppTheme.Light; } }
    public bool IsDarkTheme { get => Theme == AppTheme.Dark; set { if (value) Theme = AppTheme.Dark; } }
    public bool IsSystemTheme { get => Theme == AppTheme.System; set { if (value) Theme = AppTheme.System; } }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AlwaysDetect), nameof(DetectPerFile), nameof(NeverDetect))]
    public partial FileDefault TypeDetection { get; set; }

    public bool AlwaysDetect { get => TypeDetection == FileDefault.Always; set { if (value) TypeDetection = FileDefault.Always; } }
    public bool DetectPerFile { get => TypeDetection == FileDefault.PerFile; set { if (value) TypeDetection = FileDefault.PerFile; } }
    public bool NeverDetect { get => TypeDetection == FileDefault.Never; set { if (value) TypeDetection = FileDefault.Never; } }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AlwaysBackground), nameof(BackgroundPerFile), nameof(NeverBackground))]
    public partial FileDefault BackgroundData { get; set; }

    public bool AlwaysBackground { get => BackgroundData == FileDefault.Always; set { if (value) BackgroundData = FileDefault.Always; } }
    public bool BackgroundPerFile { get => BackgroundData == FileDefault.PerFile; set { if (value) BackgroundData = FileDefault.PerFile; } }
    public bool NeverBackground { get => BackgroundData == FileDefault.Never; set { if (value) BackgroundData = FileDefault.Never; } }

    [ObservableProperty]
    public partial bool AutoDateTime { get; set; }

    [ObservableProperty]
    public partial bool ValueFilterIndependent { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FirstSheetOnly))]
    public partial bool AllExcelSheets { get; set; }

    public bool FirstSheetOnly { get => !AllExcelSheets; set => AllExcelSheets = !value; }

    [ObservableProperty]
    public partial decimal? EngineThreads { get; set; }

    [ObservableProperty]
    public partial decimal? EngineMemoryMb { get; set; }

    /// <summary>What DuckDB uses when left to decide: a thread per logical processor and 80% of memory between them.</summary>
    public static int DefaultEvaluations { get; } = Environment.ProcessorCount;

    public static int DefaultMemoryPerEvaluation { get; } =
        (int)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes * 0.8 / (1024 * 1024) / DefaultEvaluations);

    public string EngineHint { get; } =
        $"Queries the engine runs on separate threads at once. This machine has {Environment.ProcessorCount} logical processors.";

    // ---- Caches on the Data Load page ----

    /// <summary>Holds the Cache folders; set by the owner, defaults to the real settings folder.</summary>
    public string SettingsFolder
    {
        get;
        set
        {
            field = value;
            DataCache.Folder = Path.Combine(value, "Cache");
            QueryCache.Folder = Path.Combine(value, "QueryCache");
            FoldedCache.Folder = Path.Combine(value, "FoldedArtifacts");
        }
    } = AppSettings.DefaultFolder;

    /// <summary>Where the engine spills data that doesn't fit in memory, capped at its maximum size.</summary>
    public CacheViewModel DataCache { get; } = new(Path.Combine(AppSettings.DefaultFolder, "Cache"), Preferences.DefaultCacheMb);

    /// <summary>Power BI's Q&amp;A cache; JuiceCI has no natural language queries, so it stays empty.</summary>
    public CacheViewModel QueryCache { get; } = new(Path.Combine(AppSettings.DefaultFolder, "QueryCache"), Preferences.DefaultCacheMb);

    public CacheViewModel FoldedCache { get; } = new(Path.Combine(AppSettings.DefaultFolder, "FoldedArtifacts"), null);

    [ObservableProperty]
    public partial GrainChoice DefaultGrain { get; set; }

    [ObservableProperty]
    public partial bool SnapToGrid { get; set; }

    [ObservableProperty]
    public partial bool SmartGuides { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AlignPageCenter))]
    public partial bool AlignPageTop { get; set; }

    public bool AlignPageCenter { get => !AlignPageTop; set => AlignPageTop = !value; }

    [ObservableProperty]
    public partial bool ExpandFormatSubcategories { get; set; }

    [ObservableProperty]
    public partial bool ShowHomeOnStart { get; set; }

    [ObservableProperty]
    public partial bool CopyObjectNames { get; set; }

    public static IReadOnlyList<ThemeChoice> Themes { get; } =
        [new(AppTheme.Light, "Light"), new(AppTheme.Dark, "Dark"), new(AppTheme.System, "Use system setting")];

    /// <summary>The Customize appearance drop-down.</summary>
    public ThemeChoice ThemeOption
    {
        get => Themes.First(t => t.Theme == Theme);
        set
        {
            if (value is not null) Theme = value.Theme;
        }
    }

    [ObservableProperty]
    public partial decimal? GridSize { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CulturePreview))]
    public partial CultureChoice Culture { get; set; }

    public string CulturePreview
    {
        get
        {
            var c = Culture.Name.Length == 0 ? SystemCulture : CultureInfo.GetCultureInfo(Culture.Name);
            return $"{1234567.89.ToString("N2", c)}   ·   {new DateTime(2026, 3, 14, 15, 30, 0).ToString("g", c)}";
        }
    }

    [ObservableProperty]
    public partial decimal? RecentLimit { get; set; }

    // ---- Current file ----

    [ObservableProperty]
    public partial string PageName { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomSize))]
    public partial PageSizeChoice PageSize { get; set; }

    public bool IsCustomSize => PageSize.IsCustom;

    partial void OnPageSizeChanged(PageSizeChoice value)
    {
        if (value.IsCustom) return;
        PageWidth = (decimal)value.Width;
        PageHeight = (decimal)value.Height;
    }

    [ObservableProperty]
    public partial decimal? PageWidth { get; set; }

    [ObservableProperty]
    public partial decimal? PageHeight { get; set; }

    public Preferences ToPreferences(Preferences current) => current with
    {
        Theme = Theme,
        TypeDetection = TypeDetection,
        BackgroundData = BackgroundData,
        AllExcelSheets = AllExcelSheets,
        // Leaving the automatic values alone keeps them automatic, so they follow the machine.
        EngineThreads = EngineThreads is { } t && (int)t != DefaultEvaluations ? (int)t : 0,
        EngineMemoryMb = EngineMemoryMb is { } m && (int)m != DefaultMemoryPerEvaluation ? (int)m : 0,
        AutoDateTime = AutoDateTime,
        DataCacheMb = (int)(DataCache.MaxMb ?? Preferences.DefaultCacheMb),
        QueryCacheMb = (int)(QueryCache.MaxMb ?? Preferences.DefaultCacheMb),
        DefaultDateGrain = DefaultGrain.Grain,
        ValueFilterIndependent = ValueFilterIndependent,
        SnapToGrid = SnapToGrid,
        SmartGuides = SmartGuides,
        AlignPageTop = AlignPageTop,
        ExpandFormatSubcategories = ExpandFormatSubcategories,
        ShowHomeOnStart = ShowHomeOnStart,
        CopyObjectNames = CopyObjectNames,
        GridSize = (int)(GridSize ?? 8),
        Culture = Culture.Name,
        RecentLimit = (int)(RecentLimit ?? 10),
        Options = new Dictionary<string, string>(GlobalValues),
    };
}

/// <summary>A cache folder on the Data Load page: its size, Clear Cache, and an optional maximum.</summary>
public sealed partial class CacheViewModel(string folder, int? defaultMb) : ObservableObject
{
    public string Folder
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged(nameof(Used));
        }
    } = folder;

    public bool HasMaximum => defaultMb is not null;

    [ObservableProperty]
    public partial decimal? MaxMb { get; set; } = defaultMb;

    public string Used
    {
        get
        {
            long bytes = 0;
            try
            {
                if (Directory.Exists(Folder))
                    bytes = new DirectoryInfo(Folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
            return "Currently used: " + bytes switch
            {
                < 1024 => $"{bytes} bytes",
                < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
                < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
                _ => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB",
            };
        }
    }

    /// <summary>Deletes the cached files; any still in use stay.</summary>
    [RelayCommand]
    void Clear()
    {
        if (Directory.Exists(Folder))
            foreach (var file in Directory.EnumerateFiles(Folder, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        OnPropertyChanged(nameof(Used));
    }

    [RelayCommand]
    void RestoreDefaults() => MaxMb = defaultMb;
}
