using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace Tessera.Core;

/// <summary>A table stored column by column; each value is null, double, DateTime, bool or string.</summary>
public sealed record ColumnarTable(string Name, IReadOnlyList<ColumnSchema> Columns, IReadOnlyList<object?[]> Values, int RowCount);

/// <summary>
/// Reads the tables of a Power BI Desktop (.pbix) file's embedded model: the XPress9-compressed
/// DataModel is an Analysis Services backup (ABF) whose files hold the schema (a SQLite database)
/// and the VertiPaq column store. Format details follow [MS-XLDM] and pbixray (MIT).
/// </summary>
public static class PbixImporter
{
    /// <summary>Yields each table of the model in turn, so only one is decoded in memory at a time.</summary>
    public static IEnumerable<ColumnarTable> Read(string path)
    {
        using var pbix = PbixFile.Open(path);
        foreach (var table in pbix.ReadTables())
            yield return table;
    }
}

/// <summary>An opened .pbix data model: its schema is read up front, table data on demand.</summary>
public sealed class PbixFile : IDisposable
{
    readonly PbixModel _model;

    PbixFile(PbixModel model) => _model = model;

    public static PbixFile Open(string path) => new(PbixModel.Open(path));

    /// <summary>Visible tables, in the order <see cref="ReadTables"/> yields them.</summary>
    public IReadOnlyList<string> TableNames => _model.Tables.ToList();

    /// <summary>Relationships between the visible tables, and every measure.</summary>
    public SemanticModel Model => _model.Semantics;

    public IEnumerable<ColumnarTable> ReadTables()
    {
        foreach (var table in _model.Tables)
            yield return _model.ReadTable(table);
    }

    public void Dispose() => _model.Dispose();
}

sealed record ColumnMeta(
    string Table, string Name, int DataType, string? Dictionary, List<string> Idfs, long BaseId, double Magnitude);

sealed class PbixModel : IDisposable
{
    // AMO data types.
    const int TypeString = 2, TypeInt64 = 6, TypeDouble = 8, TypeDateTime = 9, TypeDecimal = 10, TypeBoolean = 11, TypeBinary = 17;

    // VertiPaq reserves data id 2 for null; dictionary ids start at 3.
    const long NullDataId = 2;
    const long FirstDataId = 3;

    readonly FileStream _abf;
    readonly Dictionary<string, (long Offset, long Size)> _files = new(StringComparer.OrdinalIgnoreCase);
    List<ColumnMeta> _columns = [];

    public SemanticModel Semantics { get; private set; } = new();

    PbixModel(FileStream abf) => _abf = abf;

    public IEnumerable<string> Tables => _columns.Select(c => c.Table).Distinct();

    public static PbixModel Open(string path)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"juiceci-pbix-{Guid.NewGuid():N}.abf");
        var abf = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose);
        var model = new PbixModel(abf);
        try
        {
            using (var zip = ZipFile.OpenRead(path))
            {
                var entry = zip.GetEntry("DataModel");
                if (entry is null)
                {
                    throw new InvalidDataException(zip.GetEntry("Connections") is not null
                        ? "This report connects live to a published dataset, so the file holds no data to import."
                        : "This file has no embedded data model.");
                }
                using var input = new BufferedStream(entry.Open(), 1 << 20);
                Xpress9.DecompressDataModel(input, abf);
            }
            abf.Flush();
            model.ReadDirectory();
            model.ReadSchema();
            return model;
        }
        catch
        {
            model.Dispose();
            throw;
        }
    }

    public void Dispose() => _abf.Dispose();

    // ---------------------------------------------------------------- ABF container

    byte[] ReadAt(long offset, long size)
    {
        if (offset < 0 || size < 0 || offset + size > _abf.Length || size > Array.MaxLength)
            throw new InvalidDataException("The data model's file table points outside the backup.");
        var buffer = new byte[size];
        RandomAccess.Read(_abf.SafeFileHandle, buffer, offset);
        return buffer;
    }

    byte[] ReadFile(string name) =>
        _files.TryGetValue(name, out var f) ? ReadAt(f.Offset, f.Size)
            : throw new InvalidDataException($"The data model is missing {name}.");

    static XElement ParseXml(byte[] bytes, bool utf16)
    {
        if (!utf16) return XDocument.Load(new MemoryStream(bytes)).Root!;
        var text = Encoding.Unicode.GetString(bytes).TrimStart('﻿').TrimEnd('\0');
        return XElement.Parse(text);
    }

    static string Text(XElement e, string name) => e.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value ?? "";

    static long Number(XElement e, string name) => long.Parse(Text(e, name), CultureInfo.InvariantCulture);

    static IEnumerable<XElement> Descendants(XElement e, string name) => e.Descendants().Where(x => x.Name.LocalName == name);

    /// <summary>Maps each file in the backup to its location: header page → virtual directory → backup log.</summary>
    void ReadDirectory()
    {
        // The header is one 4 KB page; its XML starts after the 72-byte stream signature.
        var header = ParseXml(ReadAt(72, 4096 - 72), utf16: true);
        if (Text(header, "ApplyCompression") == "true")
            throw new NotSupportedException("This data model uses per-file compression, which isn't supported yet.");
        if (Text(header, "EncryptionFlag") == "true")
            throw new NotSupportedException("This data model is encrypted.");
        var trimTail = Text(header, "ErrorCode") == "true" ? 4 : 0;

        var directory = ParseXml(ReadAt(Number(header, "m_cbOffsetHeader"), Number(header, "DataSize")), utf16: false);
        var stored = Descendants(directory, "BackupFile")
            .Select(f => (Path: Text(f, "Path"), Offset: Number(f, "m_cbOffsetHeader"), Size: Number(f, "Size")))
            .ToList();
        if (stored.Count == 0) throw new InvalidDataException("The data model's file directory is empty.");

        // The last stored file is the backup log, which names every file by its original path.
        var logFile = stored[^1];
        var log = ParseXml(ReadAt(logFile.Offset, logFile.Size - trimTail), utf16: true);
        var byStoragePath = stored.GroupBy(f => f.Path).ToDictionary(g => g.Key, g => g.First());
        foreach (var file in Descendants(log, "BackupFile"))
        {
            if (!byStoragePath.TryGetValue(Text(file, "StoragePath"), out var s)) continue;
            var name = Text(file, "Path").Split('\\')[^1];
            _files.TryAdd(name, (s.Offset, s.Size - trimTail));
        }
    }

    // ---------------------------------------------------------------- Schema

    void ReadSchema()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"juiceci-pbix-{Guid.NewGuid():N}.sqlite");
        File.WriteAllBytes(dbPath, ReadFile("metadata.sqlitedb"));
        try
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
            }.ToString());
            conn.Open();
            _columns = QueryColumns(conn);
            var tables = Tables.ToHashSet(StringComparer.OrdinalIgnoreCase);
            Semantics = new SemanticModel
            {
                Relationships = QueryRelationships(conn).Where(r => tables.Contains(r.FromTable) && tables.Contains(r.ToTable)).ToList(),
                Measures = QueryMeasures(conn),
                SortRules = QuerySortRules(conn).Where(r => tables.Contains(r.Table)).ToList(),
                Columns = QueryColumnDisplay(conn).Where(c => tables.Contains(c.Table)).ToList(),
                Hierarchies = QueryHierarchies(conn).Where(h => tables.Contains(h.Table)).ToList(),
                HiddenTables = QueryHiddenTables(conn).Where(tables.Contains).ToList(),
            };
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    static bool HasColumn(SqliteConnection conn, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM pragma_table_info($t) WHERE name = $c";
        cmd.Parameters.AddWithValue("$t", table);
        cmd.Parameters.AddWithValue("$c", column);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    static List<ColumnMeta> QueryColumns(SqliteConnection conn)
    {
        // Older models call Column.Type "BindingType"; calculated columns keep their real type in InferredDataType.
        var typeCol = HasColumn(conn, "Column", "Type") ? "c.Type" : "c.BindingType";
        var dataType = HasColumn(conn, "Column", "InferredDataType")
            ? "CASE WHEN c.ExplicitDataType = 1 THEN c.InferredDataType ELSE c.ExplicitDataType END"
            : "c.ExplicitDataType";
        // Hide Power BI's private tables, such as the auto date/time tables behind every date column.
        var hidden = new List<string>();
        if (HasColumn(conn, "Table", "IsPrivate")) hidden.Add("t.IsPrivate = 0");
        if (HasColumn(conn, "Table", "ShowAsVariationsOnly")) hidden.Add("t.ShowAsVariationsOnly = 0");
        // SystemFlags bit 1 marks internal hierarchy tables; bit 2 (calculated tables) are the user's own.
        if (HasColumn(conn, "Table", "SystemFlags")) hidden.Add("(t.SystemFlags & 1) = 0");
        var visible = hidden.Count > 0 ? "AND " + string.Join(" AND ", hidden) : "";

        using var cmd = conn.CreateCommand();
        // One row per (column, partition); partitions are concatenated in storage order.
        // Column types: 1 = data, 2 = calculated, 4 = calculated-table column (3 is the internal row number).
        cmd.CommandText = $"""
            SELECT t.Name, COALESCE(c.ExplicitName, c.InferredName), {dataType},
                   sfd.FileName, sfi.FileName, ds.BaseId, ds.Magnitude
            FROM [Column] c
            JOIN [Table] t ON c.TableID = t.ID
            JOIN ColumnStorage cs ON c.ColumnStorageID = cs.ID
            LEFT JOIN DictionaryStorage ds ON ds.ID = cs.DictionaryStorageID
            LEFT JOIN StorageFile sfd ON sfd.ID = ds.StorageFileID
            JOIN ColumnPartitionStorage cps ON cps.ColumnStorageID = cs.ID
            JOIN StorageFile sfi ON sfi.ID = cps.StorageFileID
            JOIN PartitionStorage ps ON ps.ID = cps.PartitionStorageID
            WHERE {typeCol} IN (1, 2, 4) {visible}
            ORDER BY t.ID, cs.StoragePosition, ps.StoragePosition
            """;
        using var reader = cmd.ExecuteReader();
        var columns = new List<ColumnMeta>();
        while (reader.Read())
        {
            var table = reader.GetString(0);
            var name = reader.GetString(1);
            var idf = reader.GetString(4);
            if (columns.Count > 0 && columns[^1].Table == table && columns[^1].Name == name)
            {
                columns[^1].Idfs.Add(idf);
                continue;
            }
            var magnitude = reader.IsDBNull(6) ? 1 : reader.GetDouble(6);
            columns.Add(new ColumnMeta(table, name, reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), [idf],
                reader.IsDBNull(5) ? 0 : reader.GetInt64(5), magnitude == 0 ? 1 : magnitude));
        }
        return columns;
    }

    static List<Relationship> QueryRelationships(SqliteConnection conn)
    {
        if (!HasColumn(conn, "Relationship", "FromColumnID")) return [];
        using var cmd = conn.CreateCommand();
        // Cardinality: 1 = one, 2 = many. Many-to-many links have no single direction to join along.
        cmd.CommandText = """
            SELECT ft.Name, COALESCE(fc.ExplicitName, fc.InferredName), tt.Name, COALESCE(tc.ExplicitName, tc.InferredName),
                   r.IsActive, r.FromCardinality, r.ToCardinality
            FROM Relationship r
            JOIN [Table] ft ON ft.ID = r.FromTableID JOIN [Column] fc ON fc.ID = r.FromColumnID
            JOIN [Table] tt ON tt.ID = r.ToTableID JOIN [Column] tc ON tc.ID = r.ToColumnID
            """;
        using var reader = cmd.ExecuteReader();
        var result = new List<Relationship>();
        while (reader.Read())
        {
            if (reader.GetInt32(6) != 1) continue;
            result.Add(new Relationship(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetBoolean(4)));
        }
        return result;
    }

    static string? Folder(SqliteDataReader r, int i) => r.IsDBNull(i) || r.GetString(i).Length == 0 ? null : r.GetString(i);

    /// <summary>Columns that are hidden, sit in a display folder or have a format string; the rest show plainly.</summary>
    static List<ColumnDisplay> QueryColumnDisplay(SqliteConnection conn)
    {
        if (!HasColumn(conn, "Column", "IsHidden")) return [];
        var folder = HasColumn(conn, "Column", "DisplayFolder") ? "c.DisplayFolder" : "NULL";
        var format = HasColumn(conn, "Column", "FormatString") ? "c.FormatString" : "NULL";
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT t.Name, COALESCE(c.ExplicitName, c.InferredName), c.IsHidden, {folder}, {format}
            FROM [Column] c JOIN [Table] t ON t.ID = c.TableID
            WHERE c.IsHidden <> 0 OR COALESCE({folder}, '') <> '' OR COALESCE({format}, '') <> ''
            """;
        using var reader = cmd.ExecuteReader();
        var result = new List<ColumnDisplay>();
        while (reader.Read())
            result.Add(new ColumnDisplay(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), Folder(reader, 3), Folder(reader, 4)));
        return result;
    }

    static List<Hierarchy> QueryHierarchies(SqliteConnection conn)
    {
        if (!HasColumn(conn, "Level", "ColumnID")) return [];
        var folder = HasColumn(conn, "Hierarchy", "DisplayFolder") ? "h.DisplayFolder" : "NULL";
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT t.Name, h.Name, {folder}, COALESCE(c.ExplicitName, c.InferredName)
            FROM Hierarchy h JOIN [Table] t ON t.ID = h.TableID
            JOIN Level l ON l.HierarchyID = h.ID JOIN [Column] c ON c.ID = l.ColumnID
            WHERE h.IsHidden = 0
            ORDER BY h.ID, l.Ordinal
            """;
        using var reader = cmd.ExecuteReader();
        var result = new List<Hierarchy>();
        while (reader.Read())
        {
            var (table, name) = (reader.GetString(0), reader.GetString(1));
            if (result.Count == 0 || result[^1].Table != table || result[^1].Name != name)
                result.Add(new Hierarchy(table, name, [], Folder(reader, 2)));
            result[^1].Levels.Add(reader.GetString(3));
        }
        return result;
    }

    static List<string> QueryHiddenTables(SqliteConnection conn)
    {
        if (!HasColumn(conn, "Table", "IsHidden")) return [];
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Name FROM [Table] WHERE IsHidden <> 0";
        using var reader = cmd.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    static List<SortRule> QuerySortRules(SqliteConnection conn)
    {
        if (!HasColumn(conn, "Column", "SortByColumnID")) return [];
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT t.Name, COALESCE(c.ExplicitName, c.InferredName), COALESCE(s.ExplicitName, s.InferredName)
            FROM [Column] c JOIN [Table] t ON t.ID = c.TableID JOIN [Column] s ON s.ID = c.SortByColumnID
            """;
        using var reader = cmd.ExecuteReader();
        var result = new List<SortRule>();
        while (reader.Read()) result.Add(new SortRule(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return result;
    }

    static List<Measure> QueryMeasures(SqliteConnection conn)
    {
        if (!HasColumn(conn, "Measure", "Expression")) return [];
        var format = HasColumn(conn, "Measure", "FormatString") ? "m.FormatString" : "NULL";
        var hidden = HasColumn(conn, "Measure", "IsHidden") ? "m.IsHidden" : "0";
        var folder = HasColumn(conn, "Measure", "DisplayFolder") ? "m.DisplayFolder" : "NULL";
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT t.Name, m.Name, m.Expression, {format}, {hidden}, {folder} FROM Measure m JOIN [Table] t ON t.ID = m.TableID";
        using var reader = cmd.ExecuteReader();
        var result = new List<Measure>();
        while (reader.Read())
        {
            if (reader.IsDBNull(2)) continue;
            result.Add(new Measure(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                Folder(reader, 3), reader.GetBoolean(4), Folder(reader, 5)));
        }
        return result;
    }

    // ---------------------------------------------------------------- Tables

    public ColumnarTable ReadTable(string table)
    {
        var schema = new List<ColumnSchema>();
        var values = new List<object?[]>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in _columns.Where(c => c.Table == table))
        {
            if (column.DataType == TypeBinary) continue;
            // DuckDB column names are case-insensitive; Power BI's are not.
            var name = column.Name;
            for (var n = 2; !names.Add(name); n++) name = $"{column.Name} ({n})";
            try
            {
                values.Add(ReadColumn(column));
            }
            catch (Exception e) when (e is InvalidDataException or ArgumentException or IndexOutOfRangeException)
            {
                throw new InvalidDataException($"Couldn't read column {table}[{column.Name}]: {e.Message}", e);
            }
            schema.Add(new ColumnSchema(name, KindOf(column.DataType)));
        }

        var rows = values.Count == 0 ? 0 : values[0].Length;
        if (values.Any(v => v.Length != rows))
            throw new InvalidDataException($"The columns of table {table} have different lengths.");
        return new ColumnarTable(table, schema, values, rows);
    }

    static FieldKind KindOf(int dataType) => dataType switch
    {
        TypeInt64 or TypeDouble or TypeDecimal => FieldKind.Number,
        TypeDateTime => FieldKind.Date,
        TypeBoolean => FieldKind.Boolean,
        _ => FieldKind.Text,
    };

    /// <summary>Converts a stored value (long, double or string) to the column's .NET type.</summary>
    static object? ToValue(object? raw, int dataType)
    {
        if (raw is null) return null;
        if (raw is string s)
        {
            return dataType switch
            {
                TypeInt64 or TypeDouble or TypeDecimal => double.TryParse(s, CultureInfo.InvariantCulture, out var d) ? d : null,
                TypeBoolean => bool.TryParse(s, out var b) ? b : null,
                TypeDateTime => DateTime.TryParse(s, CultureInfo.InvariantCulture, out var dt) ? dt : null,
                _ => s,
            };
        }

        var number = raw is long l ? l : (double)raw;
        switch (dataType)
        {
            case TypeDecimal: return number / 10000;   // fixed decimal: four implied places
            case TypeInt64 or TypeDouble: return number;
            case TypeBoolean: return number != 0;
            case TypeDateTime:
                // OLE automation date: days since 1899-12-30.
                try { return DateTime.FromOADate(number); }
                catch (ArgumentException) { return null; }
            default: return System.Convert.ToString(raw, CultureInfo.InvariantCulture);
        }
    }

    object?[] ReadColumn(ColumnMeta column)
    {
        // Dictionary-encoded columns store ids into a value list; value-encoded ones store (value * Magnitude - BaseId).
        object?[]? dictionary = null;
        if (column.Dictionary is not null)
            dictionary = ReadDictionary(ReadFile(column.Dictionary)).Select(v => ToValue(v, column.DataType)).ToArray();

        var result = new List<object?>();
        foreach (var idf in column.Idfs)
        {
            var segments = ReadSegmentInfo(ReadFile(idf + "meta"));
            var data = ReadFile(idf);
            var pos = 0;
            foreach (var segment in segments)
            {
                if (pos >= data.Length && column.Dictionary is null && segment.Records > 0)
                {
                    // No stored data at all (e.g. an empty calculated column): all nulls.
                    for (var i = 0L; i < segment.Records; i++) result.Add(null);
                    continue;
                }
                foreach (var id in DecodeSegment(data, ref pos, segment))
                {
                    if (dictionary is not null)
                    {
                        var index = id - FirstDataId;
                        result.Add(index >= 0 && index < dictionary.Length ? dictionary[index] : null);
                    }
                    else
                    {
                        result.Add(segment.HasNulls && id == NullDataId ? null
                            : ToValue((id + column.BaseId) / column.Magnitude, column.DataType));
                    }
                }
            }
        }
        return result.ToArray();
    }

    // ---------------------------------------------------------------- Column segments (.idf / .idfmeta)

    sealed record SegmentInfo(long Records, int BitWidth, long MinDataId, bool HasNulls);

    ref struct Reader(ReadOnlySpan<byte> data)
    {
        readonly ReadOnlySpan<byte> _data = data;
        public int Pos;

        public readonly bool AtEnd => Pos >= _data.Length;
        public ReadOnlySpan<byte> Bytes(long n)
        {
            if (n < 0 || Pos + n > _data.Length) throw new InvalidDataException("Unexpected end of column data.");
            var s = _data.Slice(Pos, (int)n);
            Pos += (int)n;
            return s;
        }
        public byte U8() => Bytes(1)[0];
        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Bytes(4));
        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Bytes(4));
        public long I64() => BinaryPrimitives.ReadInt64LittleEndian(Bytes(8));
        public double F64() => BinaryPrimitives.ReadDoubleLittleEndian(Bytes(8));
        public void Expect(string tag)
        {
            var bytes = Bytes(tag.Length + 1);
            for (var i = 0; i < tag.Length; i++)
                if (bytes[i] != tag[i]) throw new InvalidDataException($"Expected {tag} in column metadata.");
        }
    }

    /// <summary>Parses .idfmeta: per-segment row counts, bit widths and id ranges.</summary>
    static List<SegmentInfo> ReadSegmentInfo(byte[] bytes)
    {
        var r = new Reader(bytes);
        r.Expect("<1:CP");
        var count = r.I64();
        var segments = new List<SegmentInfo>();
        for (var i = 0; i < count; i++)
        {
            r.Expect("<1:CS");
            var records = r.I64();
            r.I64();                                    // base id
            var compression = r.U32();
            var subCompression = r.U32();
            if (compression == 0xABA5A) r.Bytes(25);    // hybrid RLE facet data
            r.U32();                                    // first run value
            r.Expect("<1:SS");
            r.I64();                                    // distinct states
            long minDataId = r.U32();
            r.U32();                                    // max data id
            r.U32();                                    // original min data id
            r.I64();                                    // RLE sort order
            r.I64();                                    // row count
            var hasNulls = r.U8() != 0;
            r.I64();                                    // RLE runs
            r.I64();                                    // other RLE runs
            r.Expect("SS:1>");

            if (r.U8() != 0)
            {
                // Bit-packed sub-segment: its row count and base id aren't needed to decode.
                r.Expect("<1:CS");
                r.I64();
                r.I64();
                if (r.U8() != 0) throw new InvalidDataException("Nested sub-segments aren't supported.");
                r.Expect("CS:1>");
            }
            r.Expect("CS:1>");

            var width = BitWidth(compression);
            if (width == 0 && compression == 0xABA5A) width = BitWidth(subCompression);
            // Nullable segments are based at the null id, whatever their own minimum.
            segments.Add(new SegmentInfo(records, width, hasNulls ? NullDataId : minDataId, hasNulls));
        }
        return segments;
    }

    static int BitWidth(uint compressionClass) => compressionClass switch
    {
        >= 0xABA37 and <= 0xABA40 => (int)(compressionClass - 0xABA36),
        0xABA42 => 12,
        0xABA46 => 16,
        0xABA4B => 21,
        0xABA56 => 32,
        _ => 0,
    };

    /// <summary>
    /// Decodes one segment of an .idf: RLE runs of (value, count), where a run whose value field is
    /// 0xFFFFFFFF minus the bit-packed position instead takes its values from the bit-packed block.
    /// </summary>
    static long[] DecodeSegment(byte[] data, ref int pos, SegmentInfo segment)
    {
        var r = new Reader(data) { Pos = pos };
        var runCount = r.I64();
        var runs = r.Bytes(runCount * 8);
        var words = r.Bytes(r.I64() * 8);
        pos = r.Pos;

        var output = new long[segment.Records];
        var written = 0;
        long packedOffset = 0;
        var width = segment.BitWidth;
        var perWord = width > 0 ? 64 / width : 0;
        var mask = width >= 64 ? ulong.MaxValue : (1UL << width) - 1;
        for (var i = 0; i < runCount && written < output.Length; i++)
        {
            long value = BinaryPrimitives.ReadUInt32LittleEndian(runs[(i * 8)..]);
            long repeat = BinaryPrimitives.ReadUInt32LittleEndian(runs[(i * 8 + 4)..]);
            // Trailing slots may hold garbage, so never write more than the segment's row count.
            repeat = Math.Min(repeat, output.Length - written);
            if (value + packedOffset == 0xFFFFFFFF && perWord > 0)
            {
                for (var k = 0; k < repeat; k++, packedOffset++)
                {
                    var word = (int)(packedOffset / perWord);
                    var shift = (int)(packedOffset % perWord) * width;
                    var bits = word * 8 + 8 <= words.Length ? BinaryPrimitives.ReadUInt64LittleEndian(words[(word * 8)..]) : 0;
                    output[written++] = (long)((bits >> shift) & mask) + segment.MinDataId;
                }
            }
            else
            {
                output.AsSpan(written, (int)repeat).Fill(value);
                written += (int)repeat;
            }
        }
        return written == output.Length ? output : output[..written];
    }

    // ---------------------------------------------------------------- Dictionaries

    /// <summary>Reads a dictionary file into its values (long, double or string), ordered by data id.</summary>
    static List<object?> ReadDictionary(byte[] bytes)
    {
        var r = new Reader(bytes);
        var type = r.I32();
        r.Bytes(24);                                    // hash information
        var values = new List<object?>();
        switch (type)
        {
            case 0 or 1:                                // long or real
                var count = r.I64();
                var size = r.I32();
                for (var i = 0; i < count; i++)
                    values.Add(size == 4 ? r.I32() : type == 0 ? r.I64() : r.F64());
                break;

            case 2:
                ReadStrings(ref r, values);
                break;

            default:
                throw new InvalidDataException($"Unknown dictionary type {type}.");
        }
        return values;
    }

    sealed record StringPage(bool Compressed, string? Plain, uint TotalBits, bool SingleCharset, byte Charset, byte[] CodeLengths, byte[] Bits);

    static void ReadStrings(ref Reader r, List<object?> values)
    {
        r.I64();                                        // string count
        r.U8();                                         // compressed flag
        r.I64();                                        // longest string
        var pageCount = r.I64();
        var pages = new List<StringPage>();
        for (var p = 0; p < pageCount; p++)
        {
            r.I64();                                    // page mask
            r.U8();                                     // contains nulls
            r.I64();                                    // start index
            r.I64();                                    // string count
            var compressed = r.U8() != 0;
            if (r.U32() != 0xAABBCCDD) throw new InvalidDataException("Bad dictionary page marker.");
            if (compressed)
            {
                var totalBits = r.U32();
                var charsetType = r.U32();
                var length = r.I64();
                var single = charsetType == 0xABA91;
                var charset = single ? r.U8() : (byte)0;
                r.U32();                                // decode bits
                var lengths = r.Bytes(128).ToArray();
                r.I64();                                // buffer size
                pages.Add(new StringPage(true, null, totalBits, single, charset, lengths, r.Bytes(length).ToArray()));
            }
            else
            {
                r.I64();                                // remaining space
                var used = r.I64();
                var allocated = r.I64();
                var text = Encoding.Unicode.GetString(r.Bytes(used * 2));
                r.Bytes(allocated - used * 2);
                pages.Add(new StringPage(false, text, 0, false, 0, [], []));
            }
            if (r.U32() != 0xABCDABCD) throw new InvalidDataException("Bad dictionary page marker.");
        }

        // Record handles give each compressed string's starting bit within its page.
        var handleCount = r.I64();
        if (r.I32() != 8) throw new InvalidDataException("Bad dictionary record handles.");
        var handles = r.Bytes(handleCount * 8);
        var offsetsByPage = new Dictionary<uint, List<uint>>();
        for (var i = 0; i < handleCount; i++)
        {
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(handles[(i * 8)..]);
            var page = BinaryPrimitives.ReadUInt32LittleEndian(handles[(i * 8 + 4)..]);
            if (!offsetsByPage.TryGetValue(page, out var list)) offsetsByPage[page] = list = [];
            list.Add(offset);
        }

        for (var p = 0; p < pages.Count; p++)
        {
            var page = pages[p];
            if (!page.Compressed)
            {
                var parts = page.Plain!.Split('\0');
                values.AddRange(parts[..^1]);           // each string is NUL-terminated
            }
            else if (offsetsByPage.TryGetValue((uint)p, out var offsets))
            {
                values.AddRange(DecodeHuffmanPage(page, offsets));
            }
        }
    }

    /// <summary>
    /// Decodes a Huffman-compressed dictionary page: 256 four-bit code lengths, canonical codes read
    /// most-significant bit first from a stream of byte-swapped 16-bit words.
    /// </summary>
    static IEnumerable<string> DecodeHuffmanPage(StringPage page, List<uint> offsets)
    {
        var lengths = new byte[256];
        for (var i = 0; i < 128; i++)
        {
            lengths[2 * i] = (byte)(page.CodeLengths[i] & 15);
            lengths[2 * i + 1] = (byte)(page.CodeLengths[i] >> 4);
        }
        var maxLength = lengths.Max();
        if (maxLength == 0) return offsets.Select(_ => "");

        var countByLength = new int[16];
        foreach (var l in lengths) if (l > 0) countByLength[l]++;
        var nextCode = new int[17];
        for (int l = 1, code = 0; l <= maxLength; l++)
        {
            code = (code + countByLength[l - 1]) << 1;
            nextCode[l] = code;
        }
        var table = new ushort[1 << maxLength];     // (symbol << 8) | length
        for (var s = 0; s < 256; s++)
        {
            var l = lengths[s];
            if (l == 0) continue;
            var start = nextCode[l]++ << (maxLength - l);
            Array.Fill(table, (ushort)((s << 8) | l), start, 1 << (maxLength - l));
        }

        var stream = new byte[page.Bits.Length + 8];
        for (var i = 0; i + 1 < page.Bits.Length; i += 2)
        {
            stream[i] = page.Bits[i + 1];
            stream[i + 1] = page.Bits[i];
        }
        if ((page.Bits.Length & 1) != 0) stream[page.Bits.Length - 1] = page.Bits[^1];

        var strings = new string[offsets.Count];
        var chars = new StringBuilder();
        var bytes = new List<byte>();
        for (var i = 0; i < offsets.Count; i++)
        {
            long bit = offsets[i];
            long end = i + 1 < offsets.Count ? offsets[i + 1] : page.TotalBits;
            chars.Clear();
            bytes.Clear();
            while (bit < end)
            {
                var index = (int)(bit >> 3);
                var word = index + 8 <= stream.Length ? BinaryPrimitives.ReadUInt64BigEndian(stream.AsSpan(index)) : 0;
                var entry = table[(int)((word >> (64 - maxLength - (int)(bit & 7))) & (ulong)((1 << maxLength) - 1))];
                if ((entry & 0xff) == 0) throw new InvalidDataException("Corrupt string dictionary.");
                var symbol = (byte)(entry >> 8);
                // Single-charset pages store only the low byte of each UTF-16 character.
                if (page.SingleCharset) chars.Append((char)((page.Charset << 8) | symbol));
                else bytes.Add(symbol);
                bit += entry & 0xff;
            }
            strings[i] = page.SingleCharset ? chars.ToString()
                : Encoding.Unicode.GetString(bytes.ToArray(), 0, bytes.Count & ~1);
        }
        return strings;
    }
}
