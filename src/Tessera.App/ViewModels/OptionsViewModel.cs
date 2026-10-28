using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Tessera.App.Services;
using Tessera.Core;

namespace Tessera.App.ViewModels;

public sealed record OptionsPage(string Key, string Label, bool IsCurrentFile);

public sealed record GrainChoice(DateGrain Grain, string Label);

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

    public static IReadOnlyList<OptionsPage> GlobalPages { get; } =
    [
        new("data", "Data Load", false), new("report", "Report settings", false),
        new("regional", "Regional Settings", false), new("appearance", "Appearance", false),
        new("recent", "Recent files", false),
    ];

    public static IReadOnlyList<OptionsPage> FilePages { get; } = [new("page", "Page", true)];

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

    public OptionsViewModel(Preferences prefs, string pageName, double pageWidth, double pageHeight)
    {
        SelectedPage = GlobalPages[0];
        SelectedGlobalPage = GlobalPages[0];
        Theme = prefs.Theme;
        DetectTypes = prefs.DetectTypes;
        AllExcelSheets = prefs.AllExcelSheets;
        EngineThreads = prefs.EngineThreads;
        EngineMemoryMb = prefs.EngineMemoryMb;
        DefaultGrain = Grains.FirstOrDefault(g => g.Grain == prefs.DefaultDateGrain) ?? Grains[2];
        SnapToGrid = prefs.SnapToGrid;
        GridSize = prefs.GridSize;
        Culture = Cultures.FirstOrDefault(c => c.Name == prefs.Culture) ?? Cultures[0];
        RecentLimit = prefs.RecentLimit;
        PageName = pageName;
        PageWidth = (decimal)pageWidth;
        PageHeight = (decimal)pageHeight;
        PageSize = PageSizes.FirstOrDefault(p => p.Width == pageWidth && p.Height == pageHeight) ?? PageSizes[^1];
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageKey))]
    public partial OptionsPage SelectedPage { get; set; }

    public string PageKey => SelectedPage.Key;

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
    [NotifyPropertyChangedFor(nameof(IsLightTheme), nameof(IsDarkTheme), nameof(IsSystemTheme))]
    public partial AppTheme Theme { get; set; }

    public bool IsLightTheme { get => Theme == AppTheme.Light; set { if (value) Theme = AppTheme.Light; } }
    public bool IsDarkTheme { get => Theme == AppTheme.Dark; set { if (value) Theme = AppTheme.Dark; } }
    public bool IsSystemTheme { get => Theme == AppTheme.System; set { if (value) Theme = AppTheme.System; } }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TextOnly))]
    public partial bool DetectTypes { get; set; }

    public bool TextOnly { get => !DetectTypes; set => DetectTypes = !value; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FirstSheetOnly))]
    public partial bool AllExcelSheets { get; set; }

    public bool FirstSheetOnly { get => !AllExcelSheets; set => AllExcelSheets = !value; }

    [ObservableProperty]
    public partial decimal? EngineThreads { get; set; }

    [ObservableProperty]
    public partial decimal? EngineMemoryMb { get; set; }

    public string EngineHint { get; } =
        $"0 means automatic. This machine has {Environment.ProcessorCount} logical processors.";

    [ObservableProperty]
    public partial GrainChoice DefaultGrain { get; set; }

    [ObservableProperty]
    public partial bool SnapToGrid { get; set; }

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
        DetectTypes = DetectTypes,
        AllExcelSheets = AllExcelSheets,
        EngineThreads = (int)(EngineThreads ?? 0),
        EngineMemoryMb = (int)(EngineMemoryMb ?? 0),
        DefaultDateGrain = DefaultGrain.Grain,
        SnapToGrid = SnapToGrid,
        GridSize = (int)(GridSize ?? 8),
        Culture = Culture.Name,
        RecentLimit = (int)(RecentLimit ?? 10),
    };
}
