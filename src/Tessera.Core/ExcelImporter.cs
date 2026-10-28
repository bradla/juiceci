using System.Globalization;
using System.Text;
using ExcelDataReader;

namespace Tessera.Core;

public sealed record ColumnSchema(string Name, FieldKind Kind)
{
    public string SqlType => Kind switch
    {
        FieldKind.Number => "DOUBLE",
        FieldKind.Date => "TIMESTAMP",
        FieldKind.Boolean => "BOOLEAN",
        _ => "VARCHAR",
    };
}

/// <summary>A sheet with typed columns; each row value is null, double, DateTime, bool or string.</summary>
public sealed record SheetData(string Name, IReadOnlyList<ColumnSchema> Columns, IReadOnlyList<object?[]> Rows);

public static class ExcelImporter
{
    static ExcelImporter() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static IEnumerable<SheetData> Read(string path, bool detectTypes = true, bool allSheets = true)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = ExcelReaderFactory.CreateReader(stream);
        var sheets = new List<SheetData>();
        do
        {
            var raw = new List<object?[]>();
            while (reader.Read())
            {
                var row = new object?[reader.FieldCount];
                for (var i = 0; i < row.Length; i++) row[i] = reader.GetValue(i);
                raw.Add(row);
            }
            var sheet = FromRows(reader.Name, raw, detectTypes);
            if (sheet is not null) sheets.Add(sheet);
        } while ((allSheets || sheets.Count == 0) && reader.NextResult());
        return sheets;
    }

    /// <summary>
    /// Builds a typed sheet from raw cells. The first non-empty row is the header; blank
    /// trailing columns are dropped; a column is typed only if every non-null cell agrees
    /// (or is always text when <paramref name="detectTypes"/> is off).
    /// </summary>
    public static SheetData? FromRows(string sheetName, IReadOnlyList<object?[]> raw, bool detectTypes = true)
    {
        static bool Empty(object? v) => v is null || v is string s && string.IsNullOrWhiteSpace(s);

        var headerIndex = -1;
        for (var i = 0; i < raw.Count; i++)
            if (raw[i].Any(v => !Empty(v))) { headerIndex = i; break; }
        if (headerIndex < 0) return null;

        var width = raw.Skip(headerIndex).Max(r => Array.FindLastIndex(r, v => !Empty(v)) + 1);
        var header = raw[headerIndex];
        var names = new List<string>();
        for (var i = 0; i < width; i++)
        {
            var baseName = i < header.Length && !Empty(header[i]) ? Convert.ToString(header[i], CultureInfo.InvariantCulture)!.Trim() : $"Column{i + 1}";
            var name = baseName;
            for (var n = 2; names.Contains(name, StringComparer.OrdinalIgnoreCase); n++) name = $"{baseName} ({n})";
            names.Add(name);
        }

        var body = raw.Skip(headerIndex + 1)
            .Select(r => Enumerable.Range(0, width).Select(i => i < r.Length && !Empty(r[i]) ? r[i] : null).ToArray())
            .Where(r => r.Any(v => v is not null))
            .ToList();

        var columns = new List<ColumnSchema>();
        for (var c = 0; c < width; c++)
        {
            var values = body.Select(r => r[c]).Where(v => v is not null).ToList();
            var kind = values.Count == 0 || !detectTypes ? FieldKind.Text
                : values.All(v => v is double or int or long or decimal) ? FieldKind.Number
                : values.All(v => v is DateTime) ? FieldKind.Date
                : values.All(v => v is bool) ? FieldKind.Boolean
                : FieldKind.Text;
            columns.Add(new ColumnSchema(names[c], kind));

            foreach (var row in body)
            {
                row[c] = row[c] switch
                {
                    null => null,
                    var v when kind == FieldKind.Number => Convert.ToDouble(v, CultureInfo.InvariantCulture),
                    var v when kind == FieldKind.Text => v is DateTime dt ? dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                        : Convert.ToString(v, CultureInfo.InvariantCulture),
                    var v => v,
                };
            }
        }

        return new SheetData(sheetName, columns, body);
    }
}
