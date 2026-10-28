using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tessera.App;
using Tessera.App.Services;
using Tessera.App.ViewModels;
using Tessera.App.Views;
using Tessera.App.Tests;
using Tessera.Core;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Tessera.App.Tests;

public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// Drives the real window offscreen. Screenshots go to $JUICECI_SCREENSHOTS (or a temp folder)
/// so layout changes can be eyeballed.
/// </summary>
public class UiTests
{
    static readonly string ShotDir = Environment.GetEnvironmentVariable("JUICECI_SCREENSHOTS")
        ?? Path.Combine(Path.GetTempPath(), "juiceci-screenshots");

    static void Shot(MainWindow window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Directory.CreateDirectory(ShotDir);
        window.CaptureRenderedFrame()?.Save(Path.Combine(ShotDir, name), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    /// <summary>A view model whose settings and recent list live in a throwaway folder.</summary>
    static MainViewModel NewVm(DirectoryInfo dir) => new(
        new AppSettings(Path.Combine(dir.FullName, "settings.json")),
        new RecentFiles(Path.Combine(dir.FullName, "recent.json")));

    static FieldInfo Field(ReportViewModel report, string name) =>
        report.Tables.SelectMany(t => t.Fields).First(f => f.Name == name).Info;

    [AvaloniaFact]
    public async Task BuildSaveAndReopenSampleReport()
    {
        var dir = Directory.CreateTempSubdirectory("juiceci-ui-");
        var vm = NewVm(dir);
        var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
        window.Show();
        Shot(window, "01-home.png");

        await vm.LoadSampleCommand.ExecuteAsync(null);
        var report = vm.Report;
        Assert.False(vm.IsHome);
        Assert.Single(report.Tables);

        // Card: total revenue.
        report.AddField(Field(report, "Revenue"));
        report.Selected!.Type = VisualType.Card;
        report.Selected.SetGeometry(16, 16, 240, 140);

        // Column chart: revenue by region.
        report.Selected = null;
        report.PickVisualTypeCommand.Execute(VisualType.ColumnChart);
        report.AddField(Field(report, "Region"), report.Selected);
        report.AddField(Field(report, "Revenue"), report.Selected);
        report.Selected!.SetGeometry(272, 16, 480, 320);

        // Line chart: revenue over time.
        report.Selected = null;
        report.PickVisualTypeCommand.Execute(VisualType.LineChart);
        report.AddField(Field(report, "Date"), report.Selected);
        report.AddField(Field(report, "Revenue"), report.Selected);
        report.Selected!.SetGeometry(768, 16, 496, 320);

        // Table: units and revenue by category.
        report.Selected = null;
        report.AddField(Field(report, "Category"));
        report.AddField(Field(report, "Units"), report.Selected);
        report.AddField(Field(report, "Revenue"), report.Selected);
        report.Selected!.SetGeometry(16, 352, 400, 352);

        // Bar + pie.
        report.Selected = null;
        report.PickVisualTypeCommand.Execute(VisualType.BarChart);
        report.AddField(Field(report, "Product"), report.Selected);
        report.AddField(Field(report, "Units"), report.Selected);
        report.Selected!.SetGeometry(432, 352, 432, 352);
        report.Selected = null;
        report.PickVisualTypeCommand.Execute(VisualType.PieChart);
        report.AddField(Field(report, "Channel"), report.Selected);
        report.AddField(Field(report, "Revenue"), report.Selected);
        report.Selected!.SetGeometry(880, 352, 384, 352);

        Assert.All(report.Visuals, v => Assert.Null(v.Error));
        Assert.All(report.Visuals, v => Assert.NotNull(v.Result));
        Shot(window, "02-report.png");

        report.Selected = report.Visuals[1];
        Shot(window, "03-selected.png");

        report.ShowDataViewCommand.Execute(null);
        Assert.NotNull(report.PreviewResult);
        Shot(window, "04-data-view.png");
        report.ShowReportViewCommand.Execute(null);

        var path = Path.Combine(Path.GetTempPath(), $"juiceci-ui-{Guid.NewGuid():N}{ProjectFile.Extension}");
        try
        {
            ProjectFile.Save(path, vm.Engine, report.ToDocument());
            var reopened = NewVm(dir);
            await reopened.OpenPathAsync(path);
            Assert.Equal(6, reopened.Report.Visuals.Count);
            Assert.All(reopened.Report.Visuals, v => Assert.NotNull(v.Result));
        }
        finally
        {
            File.Delete(path);
            dir.Delete(recursive: true);
        }
        window.Close();
    }

    [AvaloniaFact]
    public async Task OpensPowerBiReportWithItsPages()
    {
        var dir = Directory.CreateTempSubdirectory("juiceci-ui-");
        try
        {
            var vm = NewVm(dir);
            var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
            window.Show();

            // The start screen's Power BI card goes through Get data; into an empty report, that opens the whole report.
            await vm.ImportFilesAsync([Path.Combine(AppContext.BaseDirectory, "Data", "ai-sample.pbix")]);
            var report = vm.Report;

            // The Data pane follows the model: hidden tables and keys stay out, columns sit in display folders.
            var accounts = report.Tables.Single(t => t.Name == "Accounts");
            accounts.IsExpanded = true;
            report.Tables.Single(t => t.Name == "Campaigns").IsExpanded = true;
            Assert.False(report.Tables.Single(t => t.Name == "Industries").IsVisible);
            Assert.DoesNotContain(accounts.Items, i => i is FieldNodeViewModel { Name: "AccountSeq" });
            var address = accounts.Items.OfType<FolderNodeViewModel>().Single(f => f.Name == "Address");
            Assert.Contains(address.Children, c => c is FieldNodeViewModel { Name: "City" });
            Assert.Equal(5, accounts.Items.OfType<FolderNodeViewModel>().Single(f => f.IsHierarchy).Children.Count);
            report.ShowHiddenFields = true;
            Assert.True(report.Tables.Single(t => t.Name == "Industries").IsVisible);
            report.ShowHiddenFields = false;
            report.Tables.Single(t => t.Name == "Accounts").IsExpanded = true;
            report.Tables.Single(t => t.Name == "Campaigns").IsExpanded = true;
            Assert.False(vm.IsHome);
            Assert.Equal("ai-sample - JuiceCI", vm.Title);
            Assert.Equal(["Key Influencers", "Decomposition Tree", "Anomaly Detection"], report.Pages.Select(p => p.Name));
            Assert.Contains("can't show yet", vm.Status);
            Assert.All(report.Visuals.Where(v => v.ShowsData), v => Assert.Null(v.Error));
            var close = report.Visuals.Single(v => v.Title == "Close %" && v.Subtitle == "by Product Category");
            Assert.Equal(4, close.Result!.Rows.Count);

            // Key influencers: what makes an opportunity more likely to be won, as Power BI finds it.
            var influencers = report.Visuals.Single(v => v.Type == VisualType.KeyInfluencers);
            var analysis = Assert.IsType<KeyInfluencerAnalysis>(influencers.Analysis);
            Assert.Equal("Won", analysis.TargetValue);
            Assert.Equal("Discount goes up 0.02", analysis.Influencers[0].Condition);
            Assert.Contains(analysis.Influencers.Take(3), i => i.Condition == "Sales Owner is Molly Clark" && Math.Round(i.Ratio, 2) == 1.52);
            Shot(window, "10-pbix-page1.png");
            influencers.TargetValue = "Lost";
            Assert.Equal("Lost", Assert.IsType<KeyInfluencerAnalysis>(influencers.Analysis).TargetValue);
            report.UndoCommand.Execute(null);

            // The page navigator inside the report switches pages, like the tabs do.
            report.SelectPage("Anomaly Detection");
            Assert.True(report.Pages[2].IsCurrent);
            Assert.All(report.Visuals.Where(v => v.ShowsData), v => Assert.Null(v.Error));
            Shot(window, "11-pbix-page3.png");

            // The decomposition tree opens along the path saved in the file, with the visual's renamed fields and filters.
            report.SelectPageCommand.Execute(report.Pages[1]);
            Assert.All(report.Visuals.Where(v => v.ShowsData), v => Assert.Null(v.Error));
            var tree = report.Visuals.Single(v => v.Type == VisualType.DecompositionTree);
            var levels = Assert.IsType<Decomposition>(tree.Analysis).Levels;
            Assert.Equal(["North America", "Software", "Design app", "US-SOUTH", "Molly Clark", "Major Pharmaceuticals"],
                levels.Select(l => l.Nodes[l.Selected].Label));
            Assert.Equal(29_621_000, ((Decomposition)tree.Analysis!).Total);
            var campaigns = report.Visuals.Single(v => v.Title == "Sales Opportunity");
            Assert.Equal("by Campaign Type", campaigns.Subtitle);
            Assert.Equal(["Email", "Phone", "Tradeshow", "Online"], campaigns.Result!.Rows.Select(r => (string?)r[0]));
            Shot(window, "12-pbix-page2.png");
            tree.Expand(1, "Computers");
            Assert.Equal(3, ((Decomposition)tree.Analysis!).Levels.Count);
            report.UndoCommand.Execute(null);
            tree = report.Visuals.Single(v => v.Type == VisualType.DecompositionTree);
            Assert.Equal(6, Assert.IsType<Decomposition>(tree.Analysis).Levels.Count);

            // Edits on one page survive switching away and back; pages can be added and undone.
            var count = report.Visuals.Count;
            report.Selected = report.Visuals.First(v => v.ShowsData);
            report.DeleteSelectedCommand.Execute(null);
            report.SelectPageCommand.Execute(report.Pages[0]);
            report.SelectPageCommand.Execute(report.Pages[1]);
            Assert.Equal(count - 1, report.Visuals.Count);
            report.AddPageCommand.Execute(null);
            Assert.Equal(4, report.Pages.Count);
            Assert.Empty(report.Visuals);
            report.UndoCommand.Execute(null);
            Assert.Equal(3, report.Pages.Count);

            // The page's filters come across: Status is limited to Lost and Won, the rest are empty cards.
            report.SelectPageCommand.Execute(report.Pages[0]);
            Assert.Equal(8, report.PageFilters.Count);   // the file lists Purchase Process twice
            var status = report.PageFilters.Single(f => f.Name == "Status");
            Assert.Equal("is Lost or Won", status.Summary);
            Assert.Equal(1, report.ActiveFilterCount);

            // Filtering a page re-runs its visuals: only the Furniture category is left.
            var category = report.PageFilters.Single(f => f.Name == "Product Category");
            category.IsExpanded = true;
            category.Values.Single(v => v.Value == "Furniture").IsChecked = true;
            var closeChart = report.Visuals.Single(v => v.Title == "Close %");
            Assert.Equal("Furniture", Assert.Single(closeChart.Result!.Rows)[0]);
            report.IsFiltersExpanded = true;
            Shot(window, "13-pbix-filters.png");
            category.ClearCommand.Execute(null);
            Assert.Equal(4, closeChart.Result!.Rows.Count);

            // A visual-level filter, dropped from the Data pane.
            report.Selected = closeChart;
            report.AddFilter(Field(report, "Product Category"), FilterScope.Visual);
            report.VisualFilters[0].Values.Single(v => v.Value == "Software").IsChecked = true;
            Assert.Single(closeChart.Result!.Rows);
            report.VisualFilters[0].RemoveCommand.Execute(null);
            Assert.Equal(4, closeChart.Result!.Rows.Count);

            // Format pane changes the look and can be undone.
            report.IsFormatTab = true;
            closeChart.AccentColor = "#E66C37";
            closeChart.ShowTitle = false;
            Shot(window, "14-format-pane.png");
            report.UndoCommand.Execute(null);
            report.UndoCommand.Execute(null);
            Assert.True(report.Visuals.Single(v => v.Title == "Close %").ShowTitle);

            // A new measure, written in DAX, joins the model and can be charted.
            var editor = new MeasureViewModel(vm.Engine, vm.Engine.Tables.Select(t => t.Name).ToList(), "Opportunities", null)
            {
                Name = "Lost Revenue",
                Expression = "CALCULATE(SUM(Opportunities[Value]), Opportunities[Status] = \"Lost\")",
                Format = MeasureViewModel.Formats.Single(f => f.Label.StartsWith("Currency")),
            };
            Assert.True(editor.Validate(), editor.Error);
            Assert.StartsWith("$", editor.Result);
            vm.Engine.Model = editor.ApplyTo(vm.Engine.Model);
            Dispatcher.UIThread.RunJobs();
            report.NewVisualCommand.Execute(VisualType.Card);
            report.AddField(vm.Engine.FindTable("Opportunities")!.Fields.Single(f => f.Name == "Lost Revenue"), report.Selected);
            Assert.Null(report.Selected!.Error);
            Assert.False(new MeasureViewModel(vm.Engine, ["Opportunities"], null, null) { Expression = "ALL(Opportunities)" }.Validate());

            report.ShowModelViewCommand.Execute(null);
            Shot(window, "15-model-view.png");
            report.ShowReportViewCommand.Execute(null);

            // Saved as a JuiceCI report, the measures and pages come back.
            var saved = Path.Combine(dir.FullName, $"ai{ProjectFile.Extension}");
            ProjectFile.Save(saved, vm.Engine, report.ToDocument());
            var reopened = NewVm(dir);
            await reopened.OpenPathAsync(saved);
            Assert.Equal(3, reopened.Report.Pages.Count);
            Assert.Equal(4, reopened.Report.Visuals.Single(v => v.Title == "Close %" && v.Subtitle == "by Product Category").Result!.Rows.Count);
            window.Close();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task GettingStartedThemesAndUndo()
    {
        var dir = Directory.CreateTempSubdirectory("juiceci-ui-");
        try
        {
            var vm = NewVm(dir);
            Assert.Equal("Untitled - JuiceCI", vm.Title);
            var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
            window.Show();

            vm.Theme = AppTheme.Dark;
            Assert.Equal(Avalonia.Styling.ThemeVariant.Dark, Application.Current!.ActualThemeVariant);
            Shot(window, "05-home-dark.png");
            Assert.Equal(AppTheme.Dark, new AppSettings(Path.Combine(dir.FullName, "settings.json")).Values.Theme);

            await vm.GettingStartedCommand.ExecuteAsync(null);
            var report = vm.Report;
            Assert.Equal(6, report.Visuals.Count);
            Assert.All(report.Visuals, v => Assert.Null(v.Error));
            Assert.False(report.UndoCommand.CanExecute(null));
            Shot(window, "06-report-dark.png");

            // A drag is many small moves; it should undo as one step.
            report.History.MergeWindow = TimeSpan.FromSeconds(5);
            var card = report.Visuals[0];
            for (var x = 24; x <= 96; x += 8) card.SetGeometry(x, card.Y, card.Width, card.Height);
            report.History.MergeWindow = TimeSpan.Zero;
            report.Selected = report.Visuals[5];
            report.DeleteSelectedCommand.Execute(null);
            Assert.Equal(5, report.Visuals.Count);

            report.UndoCommand.Execute(null);
            Assert.Equal(6, report.Visuals.Count);
            Assert.Equal(96, report.Visuals[0].X);
            report.UndoCommand.Execute(null);
            Assert.Equal(16, report.Visuals[0].X);
            Assert.False(report.UndoCommand.CanExecute(null));
            report.RedoCommand.Execute(null);
            report.RedoCommand.Execute(null);
            Assert.Equal(5, report.Visuals.Count);
            Assert.True(vm.IsDirty);

            // Search finds commands, tables and fields; picking a field adds it to the report.
            var items = vm.BuildSearchItems();
            Assert.Contains(items, i => i.Label == "Theme: Light");
            var field = items.Single(i => i.Group == "Field" && i.Label.EndsWith("[Segment]"));
            report.Selected = null;
            field.Run();
            Assert.Equal(6, report.Visuals.Count);
            Assert.True(report.Selected!.Uses(Field(report, "Segment")));

            vm.Theme = AppTheme.Light;
            var saved = Path.Combine(dir.FullName, $"Quarterly sales{ProjectFile.Extension}");
            ProjectFile.Save(saved, vm.Engine, report.ToDocument());
            vm.Recent.Add(saved);
            vm.Recent.Add(Path.Combine(dir.FullName, "missing.tsr"));
            vm.RecentQuery = "quarterly";
            Assert.Equal("Quarterly sales", Assert.Single(vm.FilteredRecent).Name);
            vm.RecentQuery = "nothing like it";
            Assert.True(vm.NoRecentMatches);
            vm.RecentQuery = "";
            vm.RecentAgeFilters[0].IsChecked = true; // Today
            Assert.Equal("Quarterly sales", Assert.Single(vm.FilteredRecent).Name);
            Assert.Equal("Filter (1)", vm.RecentFilterLabel);
            vm.RecentAgeFilters[0].IsChecked = false;
            vm.RecentAgeFilters[3].IsChecked = true; // Older: only the missing file
            Assert.Equal("missing", Assert.Single(vm.FilteredRecent).Name);
            vm.ClearRecentFiltersCommand.Execute(null);
            Assert.Equal(2, vm.FilteredRecent.Count);
            Assert.Equal(dir.FullName, Assert.Single(vm.RecentFolderFilters).Label);
            vm.ShowHomeCommand.Execute(null);
            Shot(window, "07-home-recent.png");
            var filter = window.GetVisualDescendants().OfType<Avalonia.Controls.Button>().Single(b => b.Flyout is Avalonia.Controls.Flyout);
            filter.Flyout!.ShowAt(filter);
            Shot(window, "08-recent-filter.png");
            filter.Flyout.Hide();

            // Options: global choices persist and apply; the page size is per report.
            var options = new OptionsViewModel(vm.Settings.Values, report.PageName, report.PageWidth, report.PageHeight);
            var dialog = new OptionsWindow { DataContext = options, Width = 1040, Height = 680 };
            dialog.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            dialog.CaptureRenderedFrame()?.Save(Path.Combine(ShotDir, "09-options.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            options.SelectedFilePage = OptionsViewModel.FilePages[0];
            Assert.Equal("page", options.PageKey);
            Assert.Null(options.SelectedGlobalPage);
            dialog.Close();

            options.GridSize = 16;
            options.DefaultGrain = OptionsViewModel.Grains.First(g => g.Grain == DateGrain.Quarter);
            options.Culture = OptionsViewModel.Cultures.First(c => c.Name == "de-DE");
            options.RecentLimit = 1;
            options.PageName = "Overview";
            options.PageSize = OptionsViewModel.PageSizes.First(p => p.Label.StartsWith("4:3"));
            vm.ApplyOptions(options);
            try
            {
                Assert.Equal(16, report.GridSize);
                Assert.Equal(32, report.Snap(26));
                Assert.Equal("1.234,5", Sql.FormatNumber(1234.5).Substring(0, 7));
                Assert.Single(vm.Recent.Items);
                Assert.Equal((960, 720), (report.PageWidth, report.PageHeight));
                Assert.Equal("Overview", report.PageName);
                Assert.All(report.Visuals, v => Assert.True(v.X + v.Width <= 960));
                report.UndoCommand.Execute(null);
                Assert.Equal(1280, report.PageWidth);
                report.RedoCommand.Execute(null);
                Assert.Equal(960, report.PageWidth);
                report.Selected = null;
                report.PickVisualTypeCommand.Execute(VisualType.LineChart);
                report.AddField(Field(report, "Date"), report.Selected);
                Assert.Equal(DateGrain.Quarter, report.Selected!.Spec.Category[0].Grain);

                var reloaded = new AppSettings(Path.Combine(dir.FullName, "settings.json")).Values;
                Assert.Equal(("de-DE", 16, 1), (reloaded.Culture, reloaded.GridSize, reloaded.RecentLimit));
            }
            finally
            {
                vm.ApplyOptions(new OptionsViewModel(new Preferences(), report.PageName, report.PageWidth, report.PageHeight));
            }
            window.Close();
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
            dir.Delete(recursive: true);
        }
    }
}
