using CommunityToolkit.Mvvm.ComponentModel;

namespace Tessera.App.ViewModels;

/// <summary>Home › Enter data: a small table typed or pasted (from a spreadsheet, say), header row first.</summary>
public partial class EnterDataViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Name { get; set; } = "Table";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial string Text { get; set; } = "";

    [ObservableProperty]
    public partial string? Error { get; set; }

    string[] Lines => Text.ReplaceLineEndings("\n").Split('\n').Where(l => l.Trim().Length > 0).ToArray();

    static char Delimiter(string header) => header.Contains('\t') ? '\t' : ',';

    /// <summary>"3 columns, 12 rows", so the shape can be checked before loading.</summary>
    public string Summary
    {
        get
        {
            var lines = Lines;
            if (lines.Length == 0) return "";
            var columns = lines[0].Split(Delimiter(lines[0])).Length;
            return $"{columns} column{(columns == 1 ? "" : "s")}, {lines.Length - 1} row{(lines.Length == 2 ? "" : "s")}";
        }
    }

    public bool Validate()
    {
        var lines = Lines;
        Error = string.IsNullOrWhiteSpace(Name) ? "Give the table a name."
            : lines.Length == 0 ? "Type or paste at least a row of column names."
            : lines[0].Split(Delimiter(lines[0])).Any(h => h.Trim().Length == 0) ? "Every column needs a name in the first row."
            : null;
        return Error is null;
    }
}
