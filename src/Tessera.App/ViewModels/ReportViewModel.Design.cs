using CommunityToolkit.Mvvm.Input;
using Tessera.Core;

namespace Tessera.App.ViewModels;

public sealed record PageSizeOption(string Label, double Width, double Height);

/// <summary>The Design tab: report themes and the page's size, background and wallpaper.</summary>
public partial class ReportViewModel
{
    /// <summary>The report's theme; reports without one use JuiceCI's colours.</summary>
    public ReportTheme Theme => _doc.Theme ?? ReportTheme.BuiltIn.First(t => t.Name == "JuiceCI");

    public static IReadOnlyList<ReportTheme> Themes => ReportTheme.BuiltIn;

    /// <summary>Applies a theme to every visual and page that doesn't set its own colours. Undoable.</summary>
    [RelayCommand]
    public void ApplyTheme(ReportTheme theme)
    {
        _doc.Theme = theme.Clone();
        OnPropertyChanged(nameof(Theme));
        OnPropertyChanged(nameof(PageBackground));
        RaiseChanged();
    }

    // ---- Customize page ----

    public static IReadOnlyList<PageSizeOption> PageSizes { get; } =
    [
        new("16:9", 1280, 720), new("4:3", 960, 720), new("Letter", 816, 1056), new("Tooltip", 320, 240),
    ];

    public static PageSizeOption Widescreen => PageSizes[0];
    public static PageSizeOption Standard => PageSizes[1];
    public static PageSizeOption Letter => PageSizes[2];
    public static PageSizeOption TooltipSize => PageSizes[3];

    [RelayCommand]
    void SetPageSize(PageSizeOption size) => SetPage(_page.Name, size.Width, size.Height);

    /// <summary>Design › Wallpaper: the colour around the page; null is the app's own.</summary>
    public string? PageWallpaper => _page.Wallpaper;

    [RelayCommand]
    void SetWallpaper(string? color)
    {
        if (_page.Wallpaper == color) return;
        _page.Wallpaper = color;
        OnPropertyChanged(nameof(PageWallpaper));
        RaiseChanged();
    }
}
