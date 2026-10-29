using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Tessera.App.Services;
using Tessera.App.ViewModels;
using Tessera.Core;

namespace Tessera.App.Views;

public partial class MainWindow : Window, IDialogs
{
    bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        OptionsCatalog.OpenFolder = folder => _ = Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder));
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnFileDragOver);
        AddHandler(DragDrop.DropEvent, OnFileDrop);
    }

    MainViewModel Vm => (MainViewModel)DataContext!;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainViewModel vm) vm.Dialogs = this;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        if (e.Key == Key.F)
        {
            SearchBox.Focus();
            e.Handled = true;
            return;
        }
        var command = e.Key switch
        {
            Key.S when e.KeyModifiers.HasFlag(KeyModifiers.Shift) => Vm.SaveAsCommand,
            Key.S => Vm.SaveCommand,
            Key.O => Vm.OpenCommand,
            Key.N => Vm.NewReportCommand,
            Key.Z when e.KeyModifiers.HasFlag(KeyModifiers.Shift) => Vm.Report.RedoCommand,
            Key.Z => Vm.Report.UndoCommand,
            Key.Y => Vm.Report.RedoCommand,
            _ => null,
        };
        if (command is null) return;
        command.Execute(null);
        e.Handled = true;
    }

    // ---- Top-bar search ----

    void OnSearchGotFocus(object? sender, FocusChangedEventArgs e) => SearchBox.ItemsSource = Vm.BuildSearchItems();

    void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SearchBox.Text = "";
            Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && SearchBox.SelectedItem is null)
        {
            // Enter with no highlighted suggestion runs the first match.
            var first = SearchBox.ItemsSource?.OfType<SearchItem>()
                .FirstOrDefault(i => i.Label.Contains(SearchBox.Text ?? "", StringComparison.CurrentCultureIgnoreCase));
            if (first is not null) RunSearchItem(first);
            e.Handled = true;
        }
    }

    // Arrowing through suggestions moves SelectedItem too, so only act once the list closes
    // on a pick (a cancelled list puts the typed text back).
    void OnSearchDropDownClosed(object? sender, EventArgs e)
    {
        if (SearchBox.SelectedItem is SearchItem item && SearchBox.Text == item.Label) RunSearchItem(item);
    }

    void RunSearchItem(SearchItem item)
    {
        SearchBox.SelectedItem = null;
        SearchBox.Text = "";
        Focus();
        item.Run();
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed || !Vm.IsDirty) return;
        e.Cancel = true;
        if (!await Vm.ConfirmDiscardAsync()) return;
        _closeConfirmed = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Vm.Engine.Dispose();
    }

    // Files dragged in from the file manager. Field drags inside the report are handled
    // (and marked handled) by ReportView before they bubble up here.
    void OnFileDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;

    async void OnFileDrop(object? sender, DragEventArgs e)
    {
        var paths = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList() ?? [];
        // A JuiceCI report or a Power BI file opens as a report; other files add their data.
        var project = paths.FirstOrDefault(p => p.EndsWith(ProjectFile.Extension, StringComparison.OrdinalIgnoreCase)
            || p.EndsWith(".pbix", StringComparison.OrdinalIgnoreCase));
        if (project is not null) await Vm.OpenPathAsync(project);
        else if (paths.Count > 0) await Vm.ImportFilesAsync(paths);
    }

    // ---- IDialogs ----

    static readonly FilePickerFileType ProjectType = new("JuiceCI report") { Patterns = ["*" + ProjectFile.Extension] };
    static readonly FilePickerFileType ReportTypes = new("Reports (JuiceCI, Power BI)") { Patterns = ["*" + ProjectFile.Extension, "*.pbix"] };

    public async Task<IReadOnlyList<string>> PickDataFilesAsync(string kind)
    {
        FilePickerFileType type = kind switch
        {
            "excel" => new("Excel workbook") { Patterns = ["*.xlsx", "*.xls"] },
            "text" => new("Text / CSV") { Patterns = ["*.csv", "*.tsv", "*.txt"] },
            "parquet" => new("Parquet") { Patterns = ["*.parquet"] },
            "json" => new("JSON") { Patterns = ["*.json"] },
            "powerbi" => new("Power BI file") { Patterns = ["*.pbix", "*.PBIX"] },
            _ => new("All supported data") { Patterns = DataEngine.SupportedExtensions.Select(x => "*" + x).ToList() },
        };
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = kind == "powerbi" ? "Open Power BI file" : "Get data",
            AllowMultiple = kind != "powerbi",
            FileTypeFilter = [type],
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    public async Task<string?> PickProjectAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open report",
            FileTypeFilter = [ReportTypes, ProjectType, new("Power BI report") { Patterns = ["*.pbix"] }],
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> PickSavePathAsync(string suggestedName, string? folder = null)
    {
        var start = folder is not null && Directory.Exists(folder) ? await StorageProvider.TryGetFolderFromPathAsync(new Uri(Path.GetFullPath(folder))) : null;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save report",
            SuggestedStartLocation = start,
            SuggestedFileName = suggestedName,
            DefaultExtension = ProjectFile.Extension.TrimStart('.'),
            FileTypeChoices = [ProjectType],
        });
        return file?.TryGetLocalPath();
    }

    public async Task<SaveChoice> AskSaveChangesAsync(string documentName)
    {
        var dialog = MessageDialog("Unsaved changes", $"Save changes to {documentName}?",
            ("Save", SaveChoice.Save, true), ("Don't save", SaveChoice.Discard, false), ("Cancel", SaveChoice.Cancel, false));
        return await dialog.ShowDialog<SaveChoice?>(this) ?? SaveChoice.Cancel;
    }

    public Task ShowErrorAsync(string title, string message) =>
        MessageDialog(title, message, ("OK", (object?)null, true)).ShowDialog(this);

    public Task ShowMessageAsync(string title, string message) => ShowErrorAsync(title, message);

    public async Task<bool> ShowOptionsAsync(OptionsViewModel options) =>
        await new OptionsWindow { DataContext = options }.ShowDialog<bool?>(this) == true;

    public async Task<bool> ShowMeasureAsync(MeasureViewModel measure) =>
        await new MeasureWindow { DataContext = measure }.ShowDialog<bool?>(this) == true;

    public async Task<bool> ShowEnterDataAsync(EnterDataViewModel data) =>
        await new EnterDataWindow { DataContext = data }.ShowDialog<bool?>(this) == true;

    public async Task<bool> ShowRelationshipsAsync(RelationshipsViewModel relationships) =>
        await new RelationshipsWindow { DataContext = relationships }.ShowDialog<bool?>(this) == true;

    public async Task<bool> ShowCalculationAsync(CalculationViewModel calculation) =>
        await new CalculationWindow { DataContext = calculation }.ShowDialog<bool?>(this) == true;

    public async Task<bool> ShowRolesAsync(RolesViewModel roles) =>
        await new RolesWindow { DataContext = roles }.ShowDialog<bool?>(this) == true;

    public async Task<bool> ShowCustomizeThemeAsync(CustomizeThemeViewModel theme) =>
        await new CustomizeThemeWindow { DataContext = theme }.ShowDialog<bool?>(this) == true;

    static readonly FilePickerFileType ThemeType = new("Power BI theme") { Patterns = ["*.json"] };

    public async Task<string?> PickThemeFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import theme", AllowMultiple = false, FileTypeFilter = [ThemeType],
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> PickThemeSavePathAsync(string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Download theme", SuggestedFileName = suggestedName, DefaultExtension = "json", FileTypeChoices = [ThemeType],
        });
        return file?.TryGetLocalPath();
    }

    public void OpenUrl(string url) => _ = Launcher.LaunchUriAsync(new Uri(url));

    public async Task<string?> PickImageAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Insert image",
            FileTypeFilter = [new("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg"] }],
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    static Window MessageDialog<T>(string title, string message, params (string Label, T Result, bool IsDefault)[] buttons)
    {
        var window = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var (label, result, isDefault) in buttons)
        {
            var b = new Button { Content = label, IsDefault = isDefault, MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center };
            if (isDefault) b.Classes.Add("accent");
            b.Click += (_, _) => window.Close(result);
            row.Children.Add(b);
        }
        window.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 20,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                row,
            },
        };
        return window;
    }
}

/// <summary>The app mark: four tiles of a mosaic.</summary>
public sealed class LogoMark : Control
{
    static readonly IBrush[] Tiles =
    [
        new SolidColorBrush(Color.Parse("#0E7C66")), new SolidColorBrush(Color.Parse("#E0A526")),
        new SolidColorBrush(Color.Parse("#3B6FB6")), new SolidColorBrush(Color.Parse("#D2553F")),
    ];

    public override void Render(DrawingContext context)
    {
        var s = Math.Min(Bounds.Width, Bounds.Height);
        var gap = s * 0.1;
        var tile = (s - gap) / 2;
        for (var i = 0; i < 4; i++)
        {
            var rect = new Rect(i % 2 * (tile + gap), i / 2 * (tile + gap), tile, tile);
            context.DrawRectangle(Tiles[i], null, rect, tile * 0.2, tile * 0.2);
        }
    }
}
