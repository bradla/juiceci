using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Tessera.Core;

namespace Tessera.App.ViewModels;

public sealed partial class ThemeColorViewModel(string label, string hex) : ObservableObject
{
    public string Label { get; } = label;

    [ObservableProperty]
    public partial string Hex { get; set; } = hex;
}

/// <summary>Design › Customize theme: the theme's name and colours, as #RRGGBB.</summary>
public sealed partial class CustomizeThemeViewModel : ObservableObject
{
    public CustomizeThemeViewModel(ReportTheme theme)
    {
        Name = theme.Name;
        for (var i = 0; i < 8; i++)
            DataColors.Add(new ThemeColorViewModel($"Color {i + 1}", i < theme.DataColors.Count ? theme.DataColors[i] : "#808080"));
        Others.Add(new ThemeColorViewModel("Visual background", theme.Background));
        Others.Add(new ThemeColorViewModel("Text", theme.Foreground));
        Others.Add(new ThemeColorViewModel("Page background", theme.PageBackground ?? "#FFFFFF"));
    }

    [ObservableProperty]
    public partial string Name { get; set; }

    public ObservableCollection<ThemeColorViewModel> DataColors { get; } = [];
    public ObservableCollection<ThemeColorViewModel> Others { get; } = [];

    [ObservableProperty]
    public partial string? Error { get; set; }

    static bool IsHex(string s) => s.Length == 7 && s[0] == '#' && s[1..].All(Uri.IsHexDigit);

    public bool Validate()
    {
        var bad = DataColors.Concat(Others).FirstOrDefault(c => !IsHex(c.Hex.Trim()));
        Error = string.IsNullOrWhiteSpace(Name) ? "Give the theme a name."
            : bad is not null ? $"{bad.Label} should be a colour like #118DFF." : null;
        return Error is null;
    }

    public ReportTheme ToTheme() => new()
    {
        Name = Name.Trim(),
        DataColors = DataColors.Select(c => c.Hex.Trim().ToUpperInvariant()).ToList(),
        Background = Others[0].Hex.Trim().ToUpperInvariant(),
        Foreground = Others[1].Hex.Trim().ToUpperInvariant(),
        PageBackground = Others[2].Hex.Trim().ToUpperInvariant() is "#FFFFFF" ? null : Others[2].Hex.Trim().ToUpperInvariant(),
    };
}
