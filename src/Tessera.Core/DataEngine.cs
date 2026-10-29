using DuckDB.NET.Data;

namespace Tessera.Core;

/// <summary>How files are read on import.</summary>
/// <param name="DetectTypes">Infer number/date/boolean columns in CSV and Excel files; when off, every column is text.</param>
/// <param name="AllExcelSheets">Import every non-empty sheet of a workbook, or only the first.</param>
public sealed record ImportOptions(bool DetectTypes = true, bool AllExcelSheets = true);

/// <summary>
/// In-memory columnar store backed by DuckDB. All access is serialized, so it is safe
/// to import on a background thread while the UI queries.
/// </summary>
public sealed class DataEngine : IDisposable
{
    public static readonly string[] SupportedExtensions = [".csv", ".tsv", ".txt", ".xlsx", ".xls", ".parquet", ".json", ".pbix"];

    readonly DuckDBConnection _conn;
    readonly Lock _gate = new();
    IReadOnlyList<TableInfo> _tables = [];

    public DataEngine()
    {
        _conn = new DuckDBConnection("DataSource=:memory:");
        _conn.Open();
    }

    public ImportOptions Import { get; set; } = new();

    /// <summary>Called with each statement before it runs, for the Options dialog's tracing.</summary>
    public Action<string>? Trace { get; set; }

    /// <summary>Relationships and measures, as imported from Power BI files. Saved with the project.</summary>
    public SemanticModel Model
    {
        get { lock (_gate) return _model; }
        set
        {
            lock (_gate)
            {
                _model = value;
                RefreshSchema();
            }
            TablesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    SemanticModel _model = new();

    /// <summary>Caps DuckDB's worker threads and memory; null leaves DuckDB's own default.</summary>
    public void SetResourceLimits(int? threads, int? memoryMb)
    {
        lock (_gate)
        {
            Execute(threads is > 0 ? $"SET threads = {threads}" : "RESET threads");
            Execute(memoryMb is > 0 ? $"SET memory_limit = '{memoryMb}MB'" : "RESET memory_limit");
        }
    }

    /// <summary>Where DuckDB spills data that doesn't fit in memory (Power BI's "data cache"), and how much it may write there.</summary>
    public void SetTempDirectory(string path, int? maxMb = null)
    {
        lock (_gate)
        {
            Execute($"SET temp_directory = {Sql.Literal(path)}");
            Execute(maxMb is > 0 ? $"SET max_temp_directory_size = '{maxMb}MB'" : "RESET max_temp_directory_size");
        }
    }

    /// <summary>Raised after tables are added or removed. May fire on a background thread.</summary>
    public event EventHandler? TablesChanged;

    public IReadOnlyList<TableInfo> Tables
    {
        get { lock (_gate) return _tables; }
    }

    public TableInfo? FindTable(string name) =>
        Tables.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Imports a data file, returning the names of the tables it created.</summary>
    public IReadOnlyList<string> ImportFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var name = Path.GetFileNameWithoutExtension(path);
        if (SourceSelect(path) is { } select)
        {
            var table = CreateFromSelect(name, select);
            lock (_gate) _sources[table] = Path.GetFullPath(path);
            return [table];
        }
        return ext switch
        {
            ".xlsx" or ".xls" => ImportExcel(path),
            ".pbix" => ImportPbix(path),
            _ => throw new NotSupportedException($"Unsupported file type: {ext}"),
        };
    }

    /// <summary>The query that reads a single-table file, or null for workbooks and Power BI files.</summary>
    string? SourceSelect(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".csv" or ".tsv" or ".txt" =>
            $"SELECT * FROM read_csv_auto({Sql.Literal(path)}{(Import.DetectTypes ? "" : ", all_varchar = true")})",
        ".parquet" => $"SELECT * FROM read_parquet({Sql.Literal(path)})",
        ".json" => $"SELECT * FROM read_json_auto({Sql.Literal(path)})",
        _ => null,
    };

    readonly Dictionary<string, string> _sources = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The file each table was read from, where it can be read again (Home › Refresh). Saved with the project.</summary>
    public IReadOnlyDictionary<string, string> Sources
    {
        get { lock (_gate) return new Dictionary<string, string>(_sources, StringComparer.OrdinalIgnoreCase); }
    }

    public void SetSource(string table, string path)
    {
        lock (_gate) _sources[table] = path;
    }

    /// <summary>Re-reads every table whose source file is still there; returns the tables refreshed.</summary>
    public IReadOnlyList<string> RefreshSources()
    {
        var refreshed = new List<string>();
        lock (_gate)
        {
            foreach (var (table, path) in _sources.ToList())
            {
                if (!File.Exists(path) || SourceSelect(path) is not { } select || !_tables.Any(t => t.Name == table)) continue;
                Execute($"CREATE OR REPLACE TABLE {Sql.Ident(table)} AS {select}");
                // Re-reading a table drops its calculated columns; work them out again.
                RefreshSchema();
                foreach (var c in _model.CalculatedColumns.Where(c => string.Equals(c.Table, table, StringComparison.OrdinalIgnoreCase)))
                    ApplyCalculatedColumn(table, c.Name, DaxTranslator.TranslateRow(c.Expression, table, _model));
                refreshed.Add(table);
            }
            if (refreshed.Count > 0) RefreshSchema();
        }
        if (refreshed.Count > 0) TablesChanged?.Invoke(this, EventArgs.Empty);
        return refreshed;
    }

    /// <summary>
    /// Home › Enter data: a table typed or pasted as text, the first row naming the columns and
    /// tabs or commas between values. Returns the table's name.
    /// </summary>
    public string ImportText(string name, string text)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n').Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count == 0) throw new FormatException("Type or paste at least a row of column names.");
        var file = Path.Combine(Directory.CreateTempSubdirectory("juiceci-enter-").FullName, "data.csv");
        try
        {
            File.WriteAllLines(file, lines);
            var delimiter = lines[0].Contains('\t') ? "\\t" : ",";
            return CreateFromSelect(string.IsNullOrWhiteSpace(name) ? "Table" : name.Trim(),
                $"SELECT * FROM read_csv({Sql.Literal(file)}, header = true, delim = '{delimiter}'{(Import.DetectTypes ? "" : ", all_varchar = true")})");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }

    public IReadOnlyList<string> ImportExcel(string path) =>
        ImportTables(ExcelImporter.Read(path, Import.DetectTypes, Import.AllExcelSheets).Select(s =>
            (s.Name, s.Columns, s.Rows.Count, (Func<int, int, object?>)((r, c) => s.Rows[r][c]))));

    /// <summary>Imports every table of a Power BI Desktop file's embedded data model, with its relationships and measures.</summary>
    public IReadOnlyList<string> ImportPbix(string path)
    {
        using var pbix = PbixFile.Open(path);
        var created = ImportTables(pbix.ReadTables().Select(t =>
            (t.Name, t.Columns, t.RowCount, (Func<int, int, object?>)((r, c) => t.Values[c][r]))));

        // Tables that clashed with existing ones were renamed; point the model at the new names.
        var names = pbix.TableNames.Zip(created).ToDictionary(p => p.First, p => p.Second, StringComparer.OrdinalIgnoreCase);
        string Map(string table) => names.GetValueOrDefault(table, table);
        lock (_gate)
        {
            _model = _model.Merge(pbix.Model, Map);
            RefreshSchema();
        }
        TablesChanged?.Invoke(this, EventArgs.Empty);
        return created;
    }

    /// <summary>Creates a table per source; each cell is null, double, DateTime, bool or string to match its column.</summary>
    IReadOnlyList<string> ImportTables(
        IEnumerable<(string Name, IReadOnlyList<ColumnSchema> Columns, int RowCount, Func<int, int, object?> Cell)> sources)
    {
        var created = new List<string>();
        lock (_gate)
        {
            try
            {
                foreach (var (sourceName, schema, rowCount, cell) in sources)
                {
                    var name = UniqueName(sourceName, created);
                    var columns = schema.Select(c => $"{Sql.Ident(c.Name)} {c.SqlType}");
                    Execute($"CREATE TABLE {Sql.Ident(name)} ({string.Join(", ", columns)})");
                    created.Add(name);
                    using var appender = _conn.CreateAppender(name);
                    for (var row = 0; row < rowCount; row++)
                    {
                        var r = appender.CreateRow();
                        for (var i = 0; i < schema.Count; i++)
                        {
                            var v = cell(row, i);
                            if (v is null) { r.AppendNullValue(); continue; }
                            switch (schema[i].Kind)
                            {
                                case FieldKind.Number: r.AppendValue((double?)v); break;
                                case FieldKind.Date: r.AppendValue((DateTime?)v); break;
                                case FieldKind.Boolean: r.AppendValue((bool?)v); break;
                                default: r.AppendValue((string)v); break;
                            }
                        }
                        r.EndRow();
                    }
                }
            }
            catch
            {
                // Don't leave half an import behind.
                foreach (var name in created) Execute($"DROP TABLE IF EXISTS {Sql.Ident(name)}");
                RefreshSchema();
                throw;
            }
            RefreshSchema();
        }
        TablesChanged?.Invoke(this, EventArgs.Empty);
        return created;
    }

    public string LoadSampleData()
    {
        lock (_gate)
        {
            var name = UniqueName("Sales");
            Execute($"CREATE TABLE {Sql.Ident(name)} ({string.Join(", ", SampleData.Columns.Select(c => $"{Sql.Ident(c.Name)} {c.SqlType}"))})");
            using (var appender = _conn.CreateAppender(name))
            {
                foreach (var s in SampleData.Generate())
                {
                    appender.CreateRow()
                        .AppendValue(s.Date).AppendValue(s.Region).AppendValue(s.Channel).AppendValue(s.Segment)
                        .AppendValue(s.Category).AppendValue(s.Product).AppendValue(s.Units)
                        .AppendValue(s.UnitPrice).AppendValue(s.Revenue).AppendValue(s.Cost)
                        .EndRow();
                }
            }
            RefreshSchema();
            TablesChanged?.Invoke(this, EventArgs.Empty);
            return name;
        }
    }

    public void ImportParquet(string path, string tableName) =>
        CreateFromSelect(tableName, $"SELECT * FROM read_parquet({Sql.Literal(path)})");

    public void ExportParquet(string tableName, string path)
    {
        lock (_gate) Execute($"COPY {Sql.Ident(tableName)} TO {Sql.Literal(path)} (FORMAT parquet)");
    }

    public void DropTable(string name)
    {
        lock (_gate)
        {
            Execute($"DROP TABLE IF EXISTS {Sql.Ident(name)}");
            _sources.Remove(name);
            _model = _model.Without(name);
            RefreshSchema();
        }
        TablesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (_gate)
        {
            foreach (var t in _tables) Execute($"DROP TABLE IF EXISTS {Sql.Ident(t.Name)}");
            _sources.Clear();
            _model = new SemanticModel();
            RefreshSchema();
        }
        TablesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <param name="model">Evaluate against this model instead of the engine's, e.g. to try out a new measure.</param>
    public QueryResult Query(VisualSpec visual, int limit = 1000, IEnumerable<FieldFilter>? pageFilters = null,
        SemanticModel? model = null)
    {
        lock (_gate)
        {
            var sql = QueryBuilder.Build(visual, model ?? _model, limit, pageFilters, RowFilters(model ?? _model));
            return Run(sql, QueryBuilder.Headers(visual), QueryBuilder.GroupCount(visual)) with { Formats = QueryBuilder.Formats(visual) };
        }
    }

    // ---- Modeling: calculated columns and tables, roles ----

    /// <summary>The role queries run as (Modeling › View as); null sees every row.</summary>
    public string? ViewAs { get; set; }

    List<(string Table, string Sql)>? RowFilters(SemanticModel model) =>
        ViewAs is null || model.Roles.FirstOrDefault(r => r.Name == ViewAs) is not { } role ? null
            : role.Permissions.Where(p => !string.IsNullOrWhiteSpace(p.Expression))
                .Select(p => (p.Table, DaxTranslator.TranslateRow(p.Expression, p.Table, model))).ToList();

    /// <summary>Whether a column has no repeated values, so it can be the "one" side of a relationship.</summary>
    public bool IsUnique(string table, string column)
    {
        lock (_gate)
        {
            using var cmd = Command($"SELECT count({Sql.Ident(column)}) = count(DISTINCT {Sql.Ident(column)}) FROM {Sql.Ident(table)}");
            return cmd.ExecuteScalar() is true;
        }
    }

    /// <summary>Adds (or replaces) a column computed per row by a DAX expression.</summary>
    public void AddCalculatedColumn(string table, string name, string expression)
    {
        lock (_gate)
        {
            var sql = DaxTranslator.TranslateRow(expression, table, _model);
            ApplyCalculatedColumn(table, name, sql);
            _model = _model.Copy(m => m.CalculatedColumns =
                [.. m.CalculatedColumns.Where(c => !(c.Table == table && c.Name == name)), new CalculatedColumn(table, name, expression)]);
            RefreshSchema();
        }
        TablesChanged?.Invoke(this, EventArgs.Empty);
    }

    void ApplyCalculatedColumn(string table, string name, string sql)
    {
        var exists = _tables.FirstOrDefault(t => t.Name == table)?.Fields.Any(f => f.Name == name && !f.IsMeasure) == true;
        var keep = exists ? $"* EXCLUDE ({Sql.Ident(name)})" : "*";
        Execute($"CREATE OR REPLACE TABLE {Sql.Ident(table)} AS SELECT {keep}, ({sql}) AS {Sql.Ident(name)} FROM {Sql.Ident(table)}");
    }

    /// <summary>Creates a table from a DAX table expression; returns its name.</summary>
    public string AddCalculatedTable(string name, string expression)
    {
        var sql = DaxTranslator.TranslateTable(expression, Model);
        var created = CreateFromSelect(name, sql);
        lock (_gate) _model = _model.Copy(m => m.CalculatedTables = [.. m.CalculatedTables, new CalculatedTable(created, expression)]);
        return created;
    }

    /// <summary>A decomposition tree visual's levels along its expanded path.</summary>
    public Decomposition Decompose(VisualSpec visual, IEnumerable<FieldFilter>? pageFilters = null) =>
        Analysis.Decompose(this, visual, pageFilters);

    /// <summary>A key influencers visual's analysis of its target field.</summary>
    public KeyInfluencerAnalysis KeyInfluencers(VisualSpec visual, IEnumerable<FieldFilter>? pageFilters = null) =>
        Analysis.KeyInfluencers(this, visual, pageFilters);

    /// <summary>A column's distinct values as text, the form filters compare with; null is blank.</summary>
    public IReadOnlyList<string?> DistinctValues(string table, string column, int limit = 500)
    {
        lock (_gate)
        {
            var col = Sql.Ident(column);
            using var cmd = Command($"SELECT DISTINCT CAST({col} AS VARCHAR) FROM {Sql.Ident(table)} ORDER BY 1 NULLS FIRST LIMIT {limit}");
            using var reader = cmd.ExecuteReader();
            var values = new List<string?>();
            while (reader.Read()) values.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
            return values;
        }
    }

    public QueryResult Preview(string table, int limit = 1000)
    {
        lock (_gate)
        {
            var columns = _tables.First(t => t.Name == table).Fields.Where(f => !f.IsMeasure).Select(f => f.Name).ToList();
            if (columns.Count == 0) return new QueryResult { Columns = [], Rows = [], CategoryCount = 0 };
            return Run($"SELECT {string.Join(", ", columns.Select(Sql.Ident))} FROM {Sql.Ident(table)} LIMIT {limit}", columns,
                columns.Count);
        }
    }

    string CreateFromSelect(string baseName, string select)
    {
        string name;
        lock (_gate)
        {
            name = UniqueName(baseName);
            Execute($"CREATE TABLE {Sql.Ident(name)} AS {select}");
            RefreshSchema();
        }
        TablesChanged?.Invoke(this, EventArgs.Empty);
        return name;
    }

    DuckDBCommand Command(string sql)
    {
        Trace?.Invoke(sql);
        var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd;
    }

    QueryResult Run(string sql, IReadOnlyList<string> headers, int categoryCount)
    {
        using var cmd = Command(sql);
        using var reader = cmd.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return new QueryResult { Columns = headers, Rows = rows, CategoryCount = categoryCount };
    }

    void Execute(string sql)
    {
        using var cmd = Command(sql);
        cmd.ExecuteNonQuery();
    }

    void RefreshSchema()
    {
        var columns = new List<(string Table, string Column, string Type)>();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT table_name, column_name, data_type FROM information_schema.columns
                WHERE table_schema = 'main' ORDER BY table_name, ordinal_position
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                columns.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        var tables = new List<TableInfo>();
        foreach (var group in columns.GroupBy(c => c.Table))
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT count(*) FROM {Sql.Ident(group.Key)}";
            var count = Convert.ToInt64(cmd.ExecuteScalar());
            var fields = group.Select(c => _model.FindColumn(group.Key, c.Column) is { } d
                ? new FieldInfo(group.Key, c.Column, Sql.KindOf(c.Type)) { IsHidden = d.IsHidden, DisplayFolder = d.DisplayFolder, FormatString = d.FormatString }
                : new FieldInfo(group.Key, c.Column, Sql.KindOf(c.Type))).ToList();
            tables.Add(new TableInfo(group.Key, [.. fields, .. MeasureFields(group.Key)], count));
        }
        // Power BI often keeps measures on a table with no columns of its own.
        foreach (var name in _model.Measures.Select(m => m.Table).Distinct(StringComparer.OrdinalIgnoreCase))
            if (!tables.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
                tables.Add(new TableInfo(name, MeasureFields(name).ToList(), 0));
        _tables = tables.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
    }

    IEnumerable<FieldInfo> MeasureFields(string table) =>
        _model.Measures.Where(m => string.Equals(m.Table, table, StringComparison.OrdinalIgnoreCase))
            .Select(m => new FieldInfo(table, m.Name, FieldKind.Number)
            {
                IsMeasure = true, FormatString = m.FormatString, IsHidden = m.IsHidden, DisplayFolder = m.DisplayFolder,
            });

    string UniqueName(string baseName, IEnumerable<string>? alsoTaken = null)
    {
        var name = string.IsNullOrWhiteSpace(baseName) ? "Table" : baseName.Trim();
        var taken = _tables.Select(t => t.Name).Concat(alsoTaken ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name)) return name;
        for (var i = 2; ; i++)
            if (!taken.Contains($"{name} ({i})")) return $"{name} ({i})";
    }

    public void Dispose() => _conn.Dispose();
}
