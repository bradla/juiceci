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

            // Get data › Power BI file into an empty report opens the whole report, pages and all.
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

            // View tab: landing page, gridlines, locking, the Selection, Bookmarks and Performance panes, and mobile layout.
            Assert.Equal("Key Influencers", report.LandingPageName);
            Assert.True(report.Pages[0].IsLanding);
            Assert.Equal("Report landing page: Key Influencers", report.LandingPageText);
            report.SelectPageCommand.Execute(report.Pages[1]);
            vm.SelectRibbonTabCommand.Execute("View");
            report.ShowGridlines = true;
            report.ShowSelectionPane = true;
            report.ShowPerformancePane = true;
            report.RefreshVisualsCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(report.Performance, e => e.Name == "Decomposition Tree" && e.QueryMs >= 0);
            Assert.Equal(report.Visuals.Count, report.Layers.Count);
            Shot(window, "16-view-tab.png");

            var layer = report.Layers.First(l => l.ShowsData);
            report.ToggleLayerVisibilityCommand.Execute(layer);
            Assert.False(layer.IsOnCanvas);
            report.ShowBookmarksPane = true;
            report.AddBookmarkCommand.Execute(null);
            Assert.Equal("Bookmark 1", Assert.Single(report.Bookmarks).Name);
            report.ShowAllLayersCommand.Execute(null);
            Assert.True(report.Layers.All(l => !l.IsHidden));
            report.SelectPageCommand.Execute(report.Pages[0]);
            report.Bookmarks[0].ApplyCommand.Execute(null);
            Assert.Equal("Decomposition Tree", report.PageName);
            Assert.True(report.Visuals.Single(v => v.Spec.Id == layer.Spec.Id).IsHidden);
            report.ShowAllLayersCommand.Execute(null);

            report.SetLandingPageCommand.Execute(report.Pages[1]);
            Assert.Equal("Decomposition Tree", report.LandingPageName);
            report.UndoCommand.Execute(null);
            Assert.Equal("Key Influencers", report.LandingPageName);

            // Mobile layout uses each visual's phone position from the file; others wait in Page visuals.
            report.ShowSelectionPane = report.ShowPerformancePane = report.ShowBookmarksPane = false;
            report.IsMobileLayout = true;
            Assert.InRange(report.PageWidth, 320, 400);   // a phone, widened to the widest visual on it
            Assert.Contains(report.Visuals, v => v.IsOnCanvas);
            var waiting = report.OffMobileVisuals.Count;
            if (waiting > 0)
            {
                report.AddToMobileCommand.Execute(report.OffMobileVisuals[0]);
                Assert.Equal(waiting - 1, report.OffMobileVisuals.Count);
            }
            Shot(window, "17-mobile-layout.png");
            report.IsMobileLayout = false;
            Assert.True(report.Visuals.All(v => v.IsOnCanvas));
            report.ShowSelectionPane = report.ShowPerformancePane = report.ShowBookmarksPane = report.ShowGridlines = false;

            // Visualizations: Power BI's whole gallery, with the types JuiceCI draws enabled.
            Assert.Equal(40, report.Gallery.Count);
            Assert.Equal([VisualType.BarChart, VisualType.ColumnChart, VisualType.LineChart, VisualType.AreaChart, VisualType.PieChart,
                VisualType.DonutChart, VisualType.Card, VisualType.Table, VisualType.KeyInfluencers, VisualType.DecompositionTree],
                report.Gallery.Where(g => g.IsAvailable).Select(g => g.Type!.Value));
            Assert.Contains("not available", report.Gallery.First().Tip);

            // Filters on all pages reach every page's visuals.
            var opportunities = report.Visuals.Single(v => v.Title == "Opportunity Count");
            var before = opportunities.Result!.Rows.Count;
            report.AddFilter(Field(report, "Region"), FilterScope.Report);
            report.ReportFilters[0].Values.Single(v => v.Value == "EMEA").IsChecked = true;
            Assert.Single(opportunities.Result!.Rows);
            report.SelectPageCommand.Execute(report.Pages[0]);
            Assert.Single(report.ReportFilters);
            report.SelectPageCommand.Execute(report.Pages[1]);
            report.ReportFilters[0].RemoveCommand.Execute(null);
            Assert.Equal(before, report.Visuals.Single(v => v.Title == "Opportunity Count").Result!.Rows.Count);

            // Drill through: page 3 takes Region; the Opportunity Count chart (by Region) drills into it.
            report.SelectPageCommand.Execute(report.Pages[2]);
            report.AddDrillThroughField(Field(report, "Region"));
            Assert.Single(report.DrillThroughFields);
            report.SelectPageCommand.Execute(report.Pages[1]);
            var target = Assert.Single(report.DrillTargets(report.Visuals.Single(v => v.Title == "Opportunity Count")));
            Assert.Equal("Anomaly Detection", target.Page);
            Assert.Contains("LATAM", target.Values);
            report.DrillThrough(target, "LATAM");
            Assert.Equal("Anomaly Detection", report.PageName);
            Assert.Equal(["LATAM"], report.PageFilters.Single(f => f.Name == "Region").Filter.Values);
            report.SelectPageCommand.Execute(report.Pages[1]);
            Shot(window, "18-filters-visualizations.png");

            // Area and donut charts draw.
            report.Selected = null;
            report.NewVisualCommand.Execute(VisualType.AreaChart);
            report.AddField(Field(report, "Region"), report.Selected);
            report.AddField(Field(report, "Value"), report.Selected);
            Assert.NotNull(report.Selected!.Result);
            report.Selected.Type = VisualType.DonutChart;
            Assert.Null(report.Selected.Error);
            report.DeleteSelectedCommand.Execute(null);

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
            var filter = window.GetVisualDescendants().OfType<Avalonia.Controls.Button>().Single(b => b.Flyout is Avalonia.Controls.Flyout && b.IsEffectivelyVisible);
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
            var scroller = dialog.GetVisualDescendants().OfType<Avalonia.Controls.ScrollViewer>().First(v => v.Name == "BodyScroller");
            scroller.Offset = new Vector(0, scroller.Extent.Height - scroller.Viewport.Height);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            dialog.CaptureRenderedFrame()?.Save(Path.Combine(ShotDir, "09a-options-caches.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            // Every Power BI Desktop page but Copilot, each with something on it.
            Assert.DoesNotContain(OptionsViewModel.GlobalPages, p => p.Label.Contains("Copilot") || p.Label is "Updates" or "Usage Data");
            Assert.Equal(FileDefault.PerFile, options.TypeDetection);
            Assert.Equal(OptionsViewModel.DefaultEvaluations, options.EngineThreads);
            var caches = new OptionsViewModel(new Preferences(), "p", 1280, 720) { SettingsFolder = dir.FullName };
            Directory.CreateDirectory(caches.QueryCache.Folder);
            File.WriteAllBytes(Path.Combine(caches.QueryCache.Folder, "q.bin"), new byte[2048]);
            Assert.Equal("Currently used: 2 KB", caches.QueryCache.Used);
            caches.QueryCache.ClearCommand.Execute(null);
            Assert.Equal("Currently used: 0 bytes", caches.QueryCache.Used);
            caches.DataCache.MaxMb = 1024;
            Assert.Equal(1024, caches.ToPreferences(new Preferences()).DataCacheMb);
            caches.DataCache.RestoreDefaultsCommand.Execute(null);
            Assert.Equal(Preferences.DefaultCacheMb, caches.DataCache.MaxMb);
            Assert.False(caches.FoldedCache.HasMaximum);
            Assert.Equal(["Data Load", "Regional Settings", "Privacy", "Auto recovery", "Published semantic model settings",
                "Query reduction", "Report settings", "Page"], OptionsViewModel.FilePages.Select(p => p.Label));
            foreach (var page in OptionsViewModel.GlobalPages.Concat(OptionsViewModel.FilePages))
            {
                Select(options, page.Key);
                Dispatcher.UIThread.RunJobs();
                var shown = dialog.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().Count(t => t.IsEffectivelyVisible && t.Classes.Contains("h"));
                Assert.True(shown > 0, $"{page.Label} is empty");
            }
            Select(options, "freport");
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            dialog.CaptureRenderedFrame()?.Save(Path.Combine(ShotDir, "09b-options-report-settings.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            Assert.Equal(3, options.Sections.Single(s => s.Heading == "Export data").Items.Count);
            Select(options, "security");
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            dialog.CaptureRenderedFrame()?.Save(Path.Combine(ShotDir, "09c-options-security.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            Assert.True(options.Sections.SelectMany(s => s.Items).OfType<RadioItemViewModel>().Single(r => r.Label == "Basic check").IsChecked);
            Assert.Equal("Moderate", options.Sections.SelectMany(s => s.Items).OfType<ListItemViewModel>().Single().Selected);
            Select(options, "diagnostics");
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            dialog.CaptureRenderedFrame()?.Save(Path.Combine(ShotDir, "09d-options-diagnostics.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            Select(options, "preview");
            var share = options.Sections[0].Items.OfType<CheckItemViewModel>().Single(c => c.Label == "Share to OneDrive and SharePoint");
            Assert.True(share.IsEnabled);
            options.Sections[0].Items.OfType<CheckItemViewModel>().Single(c => c.Label == "Save to OneDrive and SharePoint").IsChecked = false;
            Assert.False(share.IsEnabled);
            Assert.DoesNotContain(options.Sections[0].Items.OfType<CheckItemViewModel>(), c => c.Label.Contains("Copilot"));
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            dialog.CaptureRenderedFrame()?.Save(Path.Combine(ShotDir, "09e-options-preview.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            Select(options, "save");
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            dialog.CaptureRenderedFrame()?.Save(Path.Combine(ShotDir, "09f-options-save.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            Select(options, "report");
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            dialog.CaptureRenderedFrame()?.Save(Path.Combine(ShotDir, "09g-options-report.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            Assert.DoesNotContain(OptionsViewModel.GlobalPages, p => p.Key == "appearance");
            Assert.Equal("Light", options.ThemeOption.Label);
            Select(options, "python");
            Assert.NotEmpty(options.Sections[0].Items.OfType<ListItemViewModel>().First().Items);
            options.SelectedFilePage = OptionsViewModel.FilePages.First(p => p.Key == "page");
            Assert.Equal("page", options.PageKey);
            Assert.Null(options.SelectedGlobalPage);
            dialog.Close();

            Check(options, "preview", "Key influencers visual").IsChecked = false;
            Check(options, "diagnostics", "Enable tracing").IsChecked = true;
            Check(options, "pq", "Show whitespace and newline characters").IsChecked = true;
            Check(options, "freport", "Enable search for the filter pane").IsChecked = false;
            Select(options, "freport");
            options.Sections.Single(s => s.Heading == "Export data").Items.OfType<RadioItemViewModel>().Last().IsChecked = true;

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
                Assert.Equal("false", reloaded.Options[OptionsCatalog.PreviewKeyInfluencers]);

                // Catalog settings that drive features.
                Assert.DoesNotContain(report.AvailableVisualTypes, t => t.Type == VisualType.KeyInfluencers);
                Assert.Contains(report.AvailableVisualTypes, t => t.Type == VisualType.DecompositionTree);
                Assert.True(report.ShowWhitespace);
                Assert.False(report.IsFilterSearchEnabled);
                Assert.Equal("2", report.FileSettings![FileSettings.ExportData]);
                var trace = Assert.Single(Directory.GetFiles(Path.Combine(dir.FullName, "Traces")));
                Assert.Contains("SELECT", File.ReadAllText(trace));

                // Report settings: page alignment, object names, start-up screen and smart guides.
                var looks = new OptionsViewModel(vm.Settings.Values, report.PageName, report.PageWidth, report.PageHeight, report.FileSettings)
                {
                    AlignPageCenter = true, CopyObjectNames = true, ShowHomeOnStart = false,
                };
                vm.ApplyOptions(looks);
                Assert.False(report.AlignPageTop);
                Assert.True(report.CopyObjectNames);
                Assert.False(new MainViewModel(new AppSettings(Path.Combine(dir.FullName, "settings.json")), vm.Recent).IsHome);
                var guides = new List<(bool, double)>();
                Assert.Equal(100, VisualHost.Align(103, 100, [(0.0, 200.0)], [0, 0.5, 1], true, guides));
                Assert.Equal([(true, 100.0), (true, 200.0)], guides);
                guides.Clear();
                Assert.Equal(140, VisualHost.Align(140, 100, [(0.0, 200.0)], [0, 0.5, 1], true, guides));
                Assert.Empty(guides);

                // Crash Dump Collection, Collect diagnostic information and Clear Traces folder.
                var diagnostics = new OptionsViewModel(vm.Settings.Values, report.PageName, report.PageWidth, report.PageHeight, report.FileSettings);
                Select(diagnostics, "diagnostics");
                var crashToggle = diagnostics.Sections.SelectMany(s => s.Items).OfType<ToggleItemViewModel>().Single();
                Assert.Contains("not enabled", crashToggle.Text);
                crashToggle.ToggleCommand.Execute(null);
                Assert.Equal("Disable", crashToggle.ButtonLabel);
                vm.ApplyOptions(diagnostics);
                var crash = CrashLog.Write(new InvalidOperationException("boom"));
                Assert.Equal(Path.Combine(dir.FullName, "Traces"), Path.GetDirectoryName(crash));
                Assert.Contains("boom", File.ReadAllText(crash!));
                var bundle = OptionsCatalog.CollectDiagnostics(dir.FullName);
                using (var zip = System.IO.Compression.ZipFile.OpenRead(bundle))
                {
                    var names = zip.Entries.Select(e => e.FullName).ToList();
                    Assert.Contains("system.txt", names);
                    Assert.Contains("settings.json", names);
                    Assert.Contains(names, n => n.StartsWith("Traces/crash-"));
                }
                OptionsCatalog.ClearTraces(Path.Combine(dir.FullName, "Traces"));
                Assert.Empty(Directory.GetFiles(Path.Combine(dir.FullName, "Traces")));

                // Type Detection follows the report's own setting unless the Global page overrides it.
                Assert.True(vm.Engine.Import.DetectTypes);
                var noDetect = new OptionsViewModel(vm.Settings.Values, report.PageName, report.PageWidth, report.PageHeight, report.FileSettings);
                Check(noDetect, "fdata", "Detect column types and headers for unstructured sources").IsChecked = false;
                vm.ApplyOptions(noDetect);
                Assert.False(vm.Engine.Import.DetectTypes);
                var always = new OptionsViewModel(vm.Settings.Values, report.PageName, report.PageWidth, report.PageHeight, report.FileSettings)
                {
                    AlwaysDetect = true, AutoDateTime = false,
                };
                vm.ApplyOptions(always);
                Assert.True(vm.Engine.Import.DetectTypes);
                Assert.Equal(DateGrain.None, report.DefaultDateGrain);
                Assert.Equal(0, vm.Settings.Values.EngineThreads);

                // The filter pane's search hides cards that don't match.
                var reopenedOptions = new OptionsViewModel(vm.Settings.Values, report.PageName, report.PageWidth, report.PageHeight, report.FileSettings);
                Check(reopenedOptions, "freport", "Enable search for the filter pane").IsChecked = true;
                vm.ApplyOptions(reopenedOptions);
                Assert.True(report.IsFilterSearchEnabled);
                report.UndoCommand.Execute(null);
                Assert.False(report.IsFilterSearchEnabled);
                report.RedoCommand.Execute(null);
                Assert.True(report.IsFilterSearchEnabled);
                var nodes = report.Tables.SelectMany(t => t.Fields).ToList();
                report.AddPageFilterCommand.Execute(nodes.First(f => f.Name == "Region"));
                report.AddPageFilterCommand.Execute(nodes.First(f => f.Name == "Product"));
                report.FilterSearch = "reg";
                Assert.Contains(report.PageFilters, c => c.Name == "Region" && c.IsMatch);
                Assert.Contains(report.PageFilters, c => c.Name == "Product" && !c.IsMatch);
                report.FilterSearch = "";

                // Auto recovery copies unsaved work, and drops the copy once it's saved or opted out.
                vm.IsDirty = true;
                var copy = vm.SaveRecoveryCopy();
                Assert.True(File.Exists(copy));
                vm.IsDirty = false;
                Assert.False(File.Exists(copy));
                var optOut = new OptionsViewModel(vm.Settings.Values, report.PageName, report.PageWidth, report.PageHeight, report.FileSettings);
                Check(optOut, "frecovery", "Disable auto recovery for this file").IsChecked = true;
                vm.ApplyOptions(optOut);
                Assert.Null(vm.SaveRecoveryCopy());
                Check(optOut, "frecovery", "Disable auto recovery for this file").IsChecked = false;
                vm.ApplyOptions(optOut);

                // Save and Recover: a chosen location, keeping the copy on close, and backups of replaced reports.
                var save = new OptionsViewModel(vm.Settings.Values, report.PageName, report.PageWidth, report.PageHeight, report.FileSettings);
                Select(save, "save");
                var saveItems = save.Sections.SelectMany(s => s.Items).ToList();
                Assert.Equal(10, saveItems.OfType<CheckNumberItemViewModel>().Single().Value);
                var folders = saveItems.OfType<FolderItemViewModel>().ToList();
                Assert.Equal(OptionsCatalog.DocumentsFolder, folders[1].Path);
                folders[0].Path = Path.Combine(dir.FullName, "Recovered");
                saveItems.OfType<CheckItemViewModel>().Single(c => c.Label.StartsWith("Keep the last")).IsChecked = true;
                vm.ApplyOptions(save);
                vm.IsDirty = true;
                vm.DiscardChanges();
                var kept = Assert.Single(Directory.GetFiles(Path.Combine(dir.FullName, "Recovered")));
                vm.IsDirty = false;
                Assert.True(File.Exists(kept));

                var target = Path.Combine(dir.FullName, "report.tsr");
                File.WriteAllText(target, "old version");
                vm.FilePath = target;
                await vm.SaveCommand.ExecuteAsync(null);
                Assert.Equal("old version", File.ReadAllText(Path.Combine(vm.BackupFolder, "report.tsr")));
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

    /// <summary>Answers the window's dialogs without showing them: picks <paramref name="pick"/> and discards changes.</summary>
    sealed class StubDialogs(string pick) : IDialogs
    {
        public string? PickedKind { get; private set; }

        public Task<IReadOnlyList<string>> PickDataFilesAsync(string kind)
        {
            PickedKind = kind;
            return Task.FromResult<IReadOnlyList<string>>([pick]);
        }

        public Task<string?> PickProjectAsync() => Task.FromResult<string?>(pick);
        public Task<string?> PickSavePathAsync(string suggestedName, string? folder = null) => Task.FromResult<string?>(null);
        public Task<SaveChoice> AskSaveChangesAsync(string documentName) => Task.FromResult(SaveChoice.Discard);
        public Task ShowErrorAsync(string title, string message) => throw new Xunit.Sdk.XunitException($"{title}: {message}");
        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
        public Task<bool> ShowOptionsAsync(OptionsViewModel options) => Task.FromResult(false);
        public Task<string?> PickImageAsync() => Task.FromResult<string?>(null);
        public Task<bool> ShowMeasureAsync(MeasureViewModel measure) => Task.FromResult(false);
        public Func<EnterDataViewModel, bool>? EnterData { get; init; }
        public Task<bool> ShowEnterDataAsync(EnterDataViewModel data) => Task.FromResult(EnterData?.Invoke(data) ?? false);
        public Func<RelationshipsViewModel, bool>? Relationships { get; init; }
        public Task<bool> ShowRelationshipsAsync(RelationshipsViewModel r) => Task.FromResult(Relationships?.Invoke(r) ?? false);
        public Func<CalculationViewModel, bool>? Calculation { get; init; }
        public Task<bool> ShowCalculationAsync(CalculationViewModel c) => Task.FromResult(Calculation?.Invoke(c) ?? false);
        public Func<RolesViewModel, bool>? Roles { get; init; }
        public Task<bool> ShowRolesAsync(RolesViewModel r) => Task.FromResult(Roles?.Invoke(r) ?? false);
        public Func<CustomizeThemeViewModel, bool>? Theme { get; init; }
        public Task<bool> ShowCustomizeThemeAsync(CustomizeThemeViewModel t) => Task.FromResult(Theme?.Invoke(t) ?? false);
        public string? ThemeFile { get; init; }
        public Task<string?> PickThemeFileAsync() => Task.FromResult(ThemeFile);
        public Task<string?> PickThemeSavePathAsync(string suggestedName) => Task.FromResult(ThemeFile);
        public string? OpenedUrl { get; private set; }
        public void OpenUrl(string url) => OpenedUrl = url;
    }

    [AvaloniaFact]
    public async Task PowerBiFileCardOpensTheReportEvenOverAnotherOne()
    {
        var dir = Directory.CreateTempSubdirectory("juiceci-ui-");
        try
        {
            var vm = NewVm(dir);
            await vm.GettingStartedCommand.ExecuteAsync(null);
            Assert.NotEmpty(vm.Report.Visuals);

            var dialogs = new StubDialogs(Path.Combine(AppContext.BaseDirectory, "Data", "ai-sample.pbix"));
            vm.Dialogs = dialogs;
            vm.ShowHomeCommand.Execute(null);
            await vm.OpenPowerBiFileCommand.ExecuteAsync(null);

            Assert.Equal("powerbi", dialogs.PickedKind);
            Assert.False(vm.IsHome);
            Assert.Equal("ai-sample", vm.DocumentName);
            Assert.Equal(["Key Influencers", "Decomposition Tree", "Anomaly Detection"], vm.Report.Pages.Select(p => p.Name));
            Assert.NotEmpty(vm.Report.Visuals);
            Assert.DoesNotContain(vm.Engine.Tables, t => t.Name == "Sales");

            // Home: Power BI's ribbon, with Enter data, Quick measure, Format painter and Recent sources working.
            var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
            window.Show();
            vm.SelectRibbonTabCommand.Execute("Home");
            vm.IsRibbonExpanded = true;
            Dispatcher.UIThread.RunJobs();
            Shot(window, "19-home-ribbon.png");

            var entered = new StubDialogs(pick: "")
            {
                EnterData = d =>
                {
                    d.Name = "Targets";
                    d.Text = "Region\tTarget\nNorth America\t25000000\nEMEA\t3000000";
                    return d.Validate();
                },
            };
            vm.Dialogs = entered;
            await vm.EnterDataCommand.ExecuteAsync(null);
            Assert.Equal(2, vm.Engine.FindTable("Targets")!.RowCount);

            foreach (var quick in QuickMeasures.All)
            {
                var draft = QuickMeasures.Build(quick.Key, vm.Engine, new FieldRef { Table = "Opportunities", Column = "Value" });
                Assert.NotNull(draft);
                var check = new MeasureViewModel(vm.Engine, vm.Engine.Tables.Select(t => t.Name).ToList(), draft.Table, null)
                {
                    Name = draft.Name, Expression = draft.Expression,
                };
                Assert.True(check.Validate(), $"{quick.Label}: {draft.Expression} → {check.Error}");
            }

            var styled = vm.Report.Visuals.First(v => v.ShowsData && v.Style?.Accent is not null);
            var plain = vm.Report.Visuals.First(v => v.ShowsData && v != styled);
            plain.Spec.Style = null;
            vm.Report.Selected = styled;
            vm.Report.FormatPainterCommand.Execute(null);
            Assert.True(vm.Report.IsFormatPainting);
            vm.Report.Selected = plain;
            Assert.False(vm.Report.IsFormatPainting);
            Assert.Equal(styled.Style!.Accent, plain.Style!.Accent);

            var csv = Path.Combine(dir.FullName, "extra.csv");
            File.WriteAllText(csv, "a,b\n1,2\n");
            await vm.ImportFilesAsync([csv]);
            Assert.Equal(csv, vm.RecentSources.Items[0].Path);
            File.WriteAllText(csv, "a,b\n1,2\n3,4\n");
            await vm.RefreshCommand.ExecuteAsync(null);
            Assert.Equal(2, vm.Engine.FindTable("extra")!.RowCount);

            // Insert: duplicate a page, add shapes and a bookmark navigator.
            vm.SelectRibbonTabCommand.Execute("Insert");
            vm.IsRibbonExpanded = true;
            var report = vm.Report;
            var visuals = report.Visuals.Count;
            report.DuplicatePageCommand.Execute(null);
            Assert.Equal("Duplicate of Key Influencers", report.PageName);
            Assert.Equal(visuals, report.Visuals.Count);
            Assert.Equal(4, report.Pages.Count);
            report.InsertShapeCommand.Execute("Oval");
            Assert.Equal("Oval", report.Selected!.LayerName);
            report.InsertShapeCommand.Execute("Arrow");
            report.AddBookmarkCommand.Execute(null);
            report.InsertBookmarkNavigatorCommand.Execute(null);
            Assert.Equal(VisualType.BookmarkNavigator, report.Selected!.Type);
            Dispatcher.UIThread.RunJobs();
            Shot(window, "20-insert-ribbon.png");
            report.UndoCommand.Execute(null);

            // Modeling: relationships, a calculated column and table, and a role to view as.
            var relationships = vm.Engine.Model.Relationships.Count;
            vm.Dialogs = new StubDialogs(pick: "")
            {
                Relationships = r =>
                {
                    r.Rows[0].IsActive = false;
                    r.FromTable = "Targets";
                    r.ToTable = "Accounts";
                    r.AddCommand.Execute(null);
                    Assert.NotNull(r.Error);   // Targets has no column that matches Accounts' key
                    return true;
                },
                Calculation = c =>
                {
                    if (c.IsTable) c.Expression = "DISTINCT(Opportunities[Status])";
                    else
                    {
                        c.Table = "Opportunities";
                        c.Name = "Value with tax";
                        c.Expression = "Opportunities[Value] * 1.2";
                    }
                    return c.Validate();
                },
                Roles = r =>
                {
                    r.AddRoleCommand.Execute(null);
                    r.Selected!.Name = "Won only";
                    r.Selected.Permissions.Single(p => p.Table == "Opportunities").Expression = "[Status] = \"Won\"";
                    return r.Validate();
                },
            };
            await vm.ManageRelationshipsCommand.ExecuteAsync(null);
            Assert.Equal(relationships, vm.Engine.Model.Relationships.Count);
            Assert.False(vm.Engine.Model.Relationships[0].IsActive);
            await vm.NewColumnCommand.ExecuteAsync(null);
            Assert.Contains(vm.Engine.FindTable("Opportunities")!.Fields, f => f.Name == "Value with tax");
            var statuses = vm.Engine.DistinctValues("Opportunities", "Status").Count;
            await vm.NewTableCommand.ExecuteAsync(null);
            Assert.Equal(statuses, vm.Engine.FindTable("Table")!.RowCount);
            await vm.ManageRolesCommand.ExecuteAsync(null);
            Assert.Equal(["Won only"], vm.RoleNames);

            var status = new VisualSpec
            {
                Type = VisualType.Table, Category = [new FieldRef { Table = "Opportunities", Column = "Status" }],
            };
            Assert.Equal(statuses, vm.Engine.Query(status).Rows.Count);
            vm.ViewAsCommand.Execute("Won only");
            Assert.True(vm.IsViewingAs);
            Assert.Equal("Won", Assert.Single(vm.Engine.Query(status).Rows)[0]);
            vm.SelectRibbonTabCommand.Execute("Modeling");
            vm.IsRibbonExpanded = true;
            Dispatcher.UIThread.RunJobs();
            Shot(window, "21-modeling-ribbon.png");
            vm.ViewAsCommand.Execute(null);
            Assert.Equal(statuses, vm.Engine.Query(status).Rows.Count);
            vm.Engine.Model = vm.Engine.Model.Copy(m => m.Relationships = m.Relationships.Select(r => r with { IsActive = true }).ToList());
            report.RefreshTables();

            // Design: themes recolour the report, can be customized, downloaded and imported, and undone.
            var themeFile = Path.Combine(dir.FullName, "theme.json");
            var design = new StubDialogs(pick: "")
            {
                ThemeFile = themeFile,
                Theme = t =>
                {
                    t.Name = "House";
                    t.DataColors[0].Hex = "#123456";
                    return t.Validate();
                },
            };
            vm.Dialogs = design;
            report.ApplyThemeCommand.Execute(ReportTheme.BuiltIn.Single(t => t.Name == "Midnight"));
            Assert.Equal("#111114", report.PageBackground);
            await vm.CustomizeThemeCommand.ExecuteAsync(null);
            Assert.Equal(("House", "#123456"), (report.Theme.Name, report.Theme.DataColors[0]));
            await vm.DownloadThemeCommand.ExecuteAsync(null);
            Assert.Contains("#123456", File.ReadAllText(themeFile));
            report.ApplyThemeCommand.Execute(ReportTheme.BuiltIn[0]);
            await vm.ImportThemeCommand.ExecuteAsync(null);
            Assert.Equal("House", report.Theme.Name);
            vm.HowToCreateThemeCommand.Execute(null);
            Assert.Contains("report-themes", design.OpenedUrl);
            report.SetPageSizeCommand.Execute(ReportViewModel.Standard);
            Assert.Equal(960, report.PageWidth);
            report.SetWallpaperCommand.Execute("#D6E4F0");
            Assert.Equal("#D6E4F0", report.PageWallpaper);
            vm.SelectRibbonTabCommand.Execute("Design");
            vm.IsRibbonExpanded = true;
            Dispatcher.UIThread.RunJobs();
            Shot(window, "22-design-ribbon.png");
            report.SetWallpaperCommand.Execute(null);
            report.SetPageSizeCommand.Execute(ReportViewModel.Widescreen);

            // Optimize: paused visuals don't re-query until refreshed; presets set query reduction.
            vm.SelectRibbonTabCommand.Execute("Optimize");
            var chart = report.Visuals.First(v => v.Result is not null && v.Spec.Category.Count > 0);
            var rows = chart.Result!.Rows.Count;
            report.IsVisualsPaused = true;
            report.AddFilter(Field(report, chart.Spec.Category[0].Column), FilterScope.Page);
            var card = report.PageFilters.Last();
            card.Values.First(v => v.Value is not null).IsChecked = true;
            Assert.Equal(rows, chart.Result!.Rows.Count);
            report.RefreshPausedVisualsCommand.Execute(null);
            Assert.Single(chart.Result!.Rows);
            Assert.True(report.IsVisualsPaused);
            card.RemoveCommand.Execute(null);
            report.IsVisualsPaused = false;
            Assert.Equal(rows, chart.Result!.Rows.Count);
            await vm.OptimizationPresetCommand.ExecuteAsync("QueryReduction");
            Assert.Equal("2", report.FileSettings![FileSettings.FilterApply]);
            await vm.OptimizationPresetCommand.ExecuteAsync("Interactivity");
            Assert.Equal("false", report.FileSettings![FileSettings.DisableCrossHighlight]);
            Dispatcher.UIThread.RunJobs();
            Shot(window, "23-optimize-ribbon.png");
            window.Close();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    static void Select(OptionsViewModel options, string key)
    {
        if (OptionsViewModel.FilePages.FirstOrDefault(p => p.Key == key) is { } file) options.SelectedFilePage = file;
        else options.SelectedGlobalPage = OptionsViewModel.GlobalPages.First(p => p.Key == key);
    }

    static CheckItemViewModel Check(OptionsViewModel options, string page, string label)
    {
        Select(options, page);
        return options.Sections.SelectMany(s => s.Items).OfType<CheckItemViewModel>().Single(c => c.Label == label);
    }
}
