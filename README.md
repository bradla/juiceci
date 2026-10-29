# JuiceCI

A desktop BI report builder for Linux: load data, drag fields onto a page, and build
charts, tables and cards. Built with Avalonia (.NET 10), DuckDB and ScottPlot.

## Run

```sh
./juiceci                      # start screen
./juiceci sales.xlsx           # open with a data file loaded
./juiceci report.tsr           # open a saved report
./juiceci model.pbix           # open a Power BI report: data, measures and pages
```

Requires the .NET 10 SDK. For a standalone build:

```sh
dotnet publish src/Tessera.App -c Release -r linux-x64 --self-contained -o dist
./dist/Tessera.App
```

## What works (milestone 1)

- **Start screen**: blank report, Excel, CSV/TSV, Parquet, JSON, Power BI, built-in sample data, recent reports.
  Data files and `.tsr` reports can also be dropped onto the window. Sections collapse; *Recommended* has a
  *Getting started* card that builds a sample sales report, and one to reopen your last report. Recent
  reports can be filtered by keyword and by date modified. *Options and settings* holds the theme,
  clearing the recent list, and About.
- **Top bar**: quick-access Save / Undo / Redo, a search box (Ctrl+F) that jumps to commands, recent
  reports, tables and fields, and a Light / Dark / System default theme menu (remembered in
  `~/.config/juiceci/settings.json`; the report page itself always renders light).
- **Options** (gear in the top bar, or *Options and settings* on the start screen), saved in
  `~/.config/juiceci/settings.json`:
  - *Data Load*, laid out like Power BI's: Type Detection (always, never, or each report's own setting),
    Background Data (saved only), Parallel loading of tables (DuckDB threads and memory per thread),
    Time intelligence (Auto date/time and how new date axes are grouped), DAX Behavior (saved only),
    Data Cache Management (the engine's spill folder: size, Clear Cache, and a maximum that caps it),
    Natural language query cache and Folded Artifacts cache (empty in JuiceCI; the maximum is saved), and every Excel sheet or only the first.
  - *Report settings*: smart guides (dashed lines, and snapping, when a visual's edge or centre lines up with
    another's), snap to grid and grid size, page alignment (top or centre of the canvas), whether JuiceCI
    starts on the Home screen, the theme (Light, Dark or Use system setting), and a right-click *Copy object
    name* on visuals. *Regional Settings*: number/date format locale. *Recent files*: how many to keep.
  - The rest of Power BI Desktop's Global pages (all but Copilot, Updates and Usage Data): Power Query Editor, DirectQuery, R and
    Python scripting (installations detected on PATH), Security, Privacy, Diagnostics,
    Preview features and Save and Recover. Ones that change something: *Diagnostics → Enable tracing* logs
    every engine query to `Traces/`, *Crash Dump Collection* writes unhandled errors there too, and *Collect
    diagnostic information* zips the traces, settings and system details into `Diagnostics/`; *Preview features* hides the decomposition tree or key influencers
    visual; *Save and Recover* copies unsaved reports to `AutoRecovery/` (or a
    folder you choose) every N minutes, can keep that copy when you close without saving, sets where Save As
    starts, and keeps the previous version of a report in `Backup/` whenever a save replaces it; *Power Query
    Editor → Data Preview* shows whitespace and a monospaced font in Table view. The rest are saved, and
    their pages say so.
  - *Current file*: Data Load, Regional Settings, Privacy, Auto recovery, Published semantic model settings,
    Query reduction and Report settings, saved in the report, undoable, and read from a .pbix's own report
    settings when one is opened. *Report settings → Enable search for the filter pane* adds a search box to
    the Filters pane; *Auto recovery → Disable* opts the report out of recovery copies.
  - *Current file → Page*: page name and size (16:9, 4:3, Letter or custom), saved in the report and undoable.
- **Home tab** in Power BI's groups: Paste / Cut / Copy / *Format painter* (copies one visual's look onto the
  next one you click); *Get data*, *Excel workbook*, *Enter data* (type or paste a small table) and *Recent
  sources* (data files you loaded lately); *Refresh* (reads CSV, text, Parquet and JSON tables again from their
  files, which are remembered in saved reports); *New visual*, *Text box*, *More visuals*; *New measure* and
  *Quick measure* (average, distinct count, filtered value and differences from it, ratio, weighted average,
  written out as DAX to review). OneLake catalog, SQL Server, Dataverse, Transform data, visual calculations,
  Sensitivity and Publish are shown greyed out with a tooltip saying why.
- **Insert tab** in Power BI's groups: *New page* (blank or a duplicate of the current page), *New visual* and
  *More visuals*, the AI visuals (*Key influencers*, *Decomposition tree*), *Text box*, *Buttons* (page
  navigator, or a bookmark navigator with a button per bookmark), *Shapes* (rectangle, rounded rectangle, oval,
  triangle, arrow) and *Image*. Narrative, the Power Platform visuals and sparklines are greyed out with a
  tooltip saying why.
- **Modeling tab** in Power BI's groups: *Manage relationships* (add, delete, turn on or off; the "one" side
  must have unique values); *New measure*, *Quick measure*, *New column* (a DAX expression per row of a table,
  worked out again on Refresh) and *New table* (a table, FILTER, DISTINCT/VALUES or CALENDAR); *Manage roles*
  (row-level security: a DAX condition per table) and *View as* a role, with Power BI's yellow "Now viewing
  report as" bar. Visual calculations, Mark as date table, Change detection, parameters and Q&A are greyed out
  with a tooltip saying why. Calculated columns, tables and roles are saved with the report.
- **Design tab**: a gallery of report themes (series colours, visual background, text and page colour) applied
  to every visual and page that doesn't set its own colours, and undoable; *Customize theme*; *Download theme*
  and *Import theme* as Power BI theme files (a .pbix's own theme comes across too); *Page size*, *Page
  background* and *Wallpaper* (the colour around the page); and a link to Microsoft's theme guide.
- **Optimize tab**: *Pause visuals* (edits stop re-querying until you un-pause), *Refresh visuals*,
  *Optimization presets* (Query reduction / Interactivity set the Query reduction options; Customize opens
  them), and the *Performance analyzer*. *Apply all slicers button* is greyed out, as there are no slicers yet.
- **Filters pane** like Power BI's: cards turn grey and bold when they keep something, show their buttons on
  hover, and carry a warning when their field is gone; sections for this visual, this page and **all pages**;
  an eye to hide the pane from report readers (saved with the report).
- **Visualizations pane**: Power BI's full gallery of visual types. The ones JuiceCI draws (clustered bar and
  column, line, area, pie, donut, card, table, key influencers, decomposition tree) are live; the rest are
  greyed out with a tooltip. **Drill through**: drop a column on a page's drill-through well, then right-click
  a visual grouped by that column on another page › *Drill through* › page › value to open it filtered;
  *Keep all filters* brings the source page's filters along, and *Cross-report* mirrors the report setting.
- **View tab**, as in Power BI: *Page view* (fit to page, fit to width, actual size); *Mobile layout* (the page
  as a phone shows it, using each visual's phone position from a .pbix, with a *Page visuals* list to add the
  rest); *Gridlines*, *Snap to grid* and *Lock objects*; and the *Filters*, *Bookmarks* (save a page's filters
  and hidden visuals and return to them), *Selection* (layer list with show/hide), *Performance analyzer*
  (query and draw time per visual) and *Sync slicers* panes. The report opens on its landing page, marked
  with a house on its tab and in the status bar; right-click a tab to change it.
- **Undo/redo** (Ctrl+Z, Ctrl+Y or Ctrl+Shift+Z) for report edits; a drag or a burst of typing undoes as one
  step. Removing a table can't be undone and clears the history.
- **Data engine**: each file becomes a DuckDB table; types are inferred (text, number, date, boolean).
  Excel workbooks import every non-empty sheet.
- **Power BI (.pbix)**: opening a file brings in the whole report. *Get data → Power BI* adds just its
  tables and measures to the current report.
  - Every table of the embedded model (data, calculated columns and calculated tables; Power BI's hidden
    auto date tables are skipped), decoded natively: XPress9 decompression, the ABF backup container, its
    SQLite schema and the VertiPaq column store.
  - Relationships: a visual can mix tables; rows of the summarized table are joined along active
    many-to-one relationships, as Power BI's filters flow. *Sort by column* settings (month names by
    month number) are kept.
  - Measures, translated from DAX to SQL: aggregations and X iterators, `CALCULATE` / `FILTER` /
    `KEEPFILTERS` row conditions, `DIVIDE`, `IF`, `SWITCH`, `VAR … RETURN`, arithmetic and measure references.
    Their format strings (percent, currency) carry through to labels. Measures using `ALL`, `REMOVEFILTERS`,
    `USERELATIONSHIP` or time intelligence show an explanation instead of a number.
  - Report pages, in both the enhanced (PBIR) and older single-file layouts. Bar, column, line, pie/donut,
    card, table and matrix visuals become live JuiceCI visuals, with fields renamed for the visual keeping
    their names. Text boxes, shapes, images (with theme colours) and the page navigator keep the page's look.
    Other visuals (slicers, maps…) are labelled placeholders. Pages wider than 1280px are scaled down.
  - *Decomposition tree*: opens along the path saved in the file; click a bar to expand or collapse it.
  - *Key influencers*: what makes the target value more likely, ranked by how much. For each value of a text
    field: the likelihood with that value against without it. For numeric fields: the change in odds over
    a one-standard-deviation step, from a logistic fit. Pick the target value from its drop-down; click an
    influencer to chart its field. *Top segments* isn't implemented.
  - Page and visual filters come across into the Filters pane: basic "is one of" filters and advanced ones
    (is / is not blank, contains, starts with, comparisons).
  - Column format strings (currency on a Value column, for example) carry through to labels, and bar labels
    share one display unit as Power BI's do ($10.1M … $0.5M).
  - The look: page background (Power BI's built-in Fluent 2 theme is assumed when the file only names it),
    title colours, and the "Data labels" style (no value axis, labelled bars).
  - Opening a `.pbix` any way (Open, the start screen's Power BI card, drag and drop, the command line, or
    Get data into an empty report) brings in the report.
  - Not carried over: slicer selections, advanced/Top N filters, series (legend) splits, small multiples,
    drill-down and conditional formatting. Reports that connect live to a published dataset have no data in
    the file.
- **Ribbon** (report screens): *File* returns to the start screen.
  - *Home*: clipboard (cut/copy/paste visuals), Get data, recent sources, New visual, Text box, New measure.
  - *Insert*: new page, every visual type, text box, shape, image and page navigator.
  - *Modeling*: New measure, Model view, Page settings, Table view.
  - *View*: fit to page / fit to width / actual size, show or hide each pane, snap to grid, theme.
  - *Help*: getting started, keyboard shortcuts, About. Clicking the current tab folds the ribbon away.
- **Report view**: multiple pages, with tabs under the canvas (green *+* adds one, right-click to delete).
  Pages default to 1280×720. The status bar shows *Page n of m* and a zoom slider; the page fits the
  window until you zoom. Visuals: column, bar, line, pie (donut), table, card, decomposition tree and key
  influencers (*Analyze* and *Explain by* wells), plus text boxes, shapes, images and page navigators.
  - *Filters* pane (a strip until opened): filters on the selected visual and on the page. Drag a field
    in, then tick the values to keep. Filters on related tables follow the relationships.
  - *Visualizations* pane with *Build* (field wells) and *Format* tabs (title on/off and text,
    background, data colour, and text, size, bold and colour for text boxes).
  - *Data* pane: searchable field tree with the model's display folders and hierarchies. Hidden tables and
    columns stay out unless you pick *View hidden* in its ⋯ menu. Right-click a table for New measure / View data / Remove, and
    a measure for Edit / Delete.
  - Panes collapse to a strip with ».
- **Measures**: *New measure* opens a DAX editor (name, home table, format). It checks the formula by
  evaluating it and shows the result before saving.
- **Model view**: every table with its fields, and relationship lines (1 and * ends, dashed when
  inactive). Drag cards by their header.
  - Tick a field or drag it onto the page to start a visual; drag onto a visual or a well to add to it.
  - Move by dragging a visual's header, resize from the corner grip (8px snap). Delete/Backspace removes.
  - Per-field aggregation (sum, average, min, max, count, distinct count, don't summarize).
  - Dates on an axis are grouped by month by default (year / quarter / month / day / exact).
- **Table view**: browse any loaded table.
- **Save/open**: `.tsr` is a zip of `project.json` (layout) plus one Parquet file per table, so reports
  are self-contained.

## Not yet

Slicers, cross-highlighting, advanced filters, creating relationships in JuiceCI itself (they come from
`.pbix` files today), the rest of DAX, and a Power Query-style transform step. Power BI's cloud features
(sign-in, sensitivity labels, Publish/Share, Copilot) have no JuiceCI counterpart.

## Layout

```
src/Tessera.Core        engine (DuckDB), import, query builder, project file — no UI
src/Tessera.App         Avalonia UI: views, view models, chart rendering
tests/Tessera.Core.Tests
tests/Tessera.App.Tests headless UI test; set JUICECI_SCREENSHOTS=dir to save screenshots
```

```sh
dotnet test
```
