using System.Globalization;
using System.Numerics;

namespace Tessera.Core;

public static class Sql
{
    public static string Ident(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    public static string Literal(string value) => "'" + value.Replace("'", "''") + "'";

    public static FieldKind KindOf(string duckDbType)
    {
        var t = duckDbType.ToUpperInvariant();
        if (t == "BOOLEAN") return FieldKind.Boolean;
        if (t.StartsWith("DATE") || t.StartsWith("TIMESTAMP")) return FieldKind.Date;
        string[] numeric = ["TINYINT", "SMALLINT", "INTEGER", "BIGINT", "HUGEINT", "UTINYINT", "USMALLINT",
            "UINTEGER", "UBIGINT", "UHUGEINT", "FLOAT", "DOUBLE", "REAL"];
        if (numeric.Contains(t) || t.StartsWith("DECIMAL")) return FieldKind.Number;
        return FieldKind.Text;
    }

    public static double? ToDouble(object? value) => value switch
    {
        null or DBNull => null,
        double d => d,
        float f => f,
        decimal m => (double)m,
        BigInteger b => (double)b,
        string s => double.TryParse(s, CultureInfo.InvariantCulture, out var d) ? d : null,
        IConvertible c => c.ToDouble(CultureInfo.InvariantCulture),
        _ => null,
    };

    public static DateTime? ToDateTime(object? value) => value switch
    {
        null or DBNull => null,
        DateTime dt => dt,
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        DateTimeOffset o => o.DateTime,
        _ => DateTime.TryParse(value.ToString(), CultureInfo.InvariantCulture, out var p) ? p : null,
    };

    /// <summary>A query value as DuckDB writes it when cast to VARCHAR, the form filters compare with; null stays null.</summary>
    public static string? FilterText(object? value) => value switch
    {
        null or DBNull => null,
        string s => s,
        bool b => b ? "true" : "false",
        double or float or decimal => ToDouble(value)!.Value.ToString("0.0###############", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    /// <summary>Formats a cell value for display.</summary>
    public static string Format(object? value) => value switch
    {
        null or DBNull => "",
        DateTime dt => dt.TimeOfDay == TimeSpan.Zero ? dt.ToString("yyyy-MM-dd") : dt.ToString("yyyy-MM-dd HH:mm"),
        DateOnly d => d.ToString("yyyy-MM-dd"),
        double or float or decimal or BigInteger => FormatNumber(ToDouble(value)!.Value),
        _ => value.ToString() ?? "",
    };

    public static string FormatNumber(double d) =>
        Math.Abs(d % 1) < 1e-9 ? d.ToString("N0") : d.ToString("N2");

    /// <summary>
    /// Formats a number with a Power BI format string's essentials: percentages keep their decimals,
    /// currency keeps its symbol. Compact mode shortens large numbers the way Power BI's "Auto" units do.
    /// </summary>
    public static string FormatNumber(double d, string? format, bool compact)
    {
        var f = format?.Split(';')[0].Replace("\\", "");
        if (string.IsNullOrEmpty(f)) return compact ? FormatCompact(d) : FormatNumber(d);
        if (f.Contains('%'))
        {
            var dot = f.IndexOf('.');
            var decimals = dot < 0 ? 0 : f.Skip(dot + 1).TakeWhile(c => c is '0' or '#').Count();
            return (d * 100).ToString("N" + decimals) + "%";
        }
        var symbol = f.FirstOrDefault(c => c is '$' or '€' or '£' or '¥');
        var digits = compact ? FormatCompact(Math.Abs(d)) : FormatNumber(Math.Abs(d));
        return symbol == default ? (d < 0 ? "-" : "") + digits : (d < 0 ? "-" : "") + symbol + digits;
    }

    /// <summary>
    /// Data labels in one display unit picked from the largest value, as Power BI's "Auto" units do:
    /// 8.5K, 1.3K, 0.5K rather than each label choosing its own.
    /// </summary>
    public static Func<double, string> UnitFormatter(IEnumerable<double> values, string? format)
    {
        var f = format?.Split(';')[0].Replace("\\", "");
        var max = values.Select(Math.Abs).DefaultIfEmpty(0).Max();
        if (f?.Contains('%') == true || max < 1e3) return d => FormatNumber(d, format, compact: true);
        var (unit, suffix) = max >= 1e9 ? (1e9, "bn") : max >= 1e6 ? (1e6, "M") : (1e3, "K");
        var symbol = f?.FirstOrDefault(c => c is '$' or '€' or '£' or '¥') ?? default;
        return d => (d < 0 ? "-" : "") + (symbol == default ? "" : symbol.ToString()) + (Math.Abs(d) / unit).ToString("0.0") + suffix;
    }

    /// <summary>Short form for card visuals and axis labels: 1.2K, 3.4M, 5.6bn.</summary>
    public static string FormatCompact(double d)
    {
        var a = Math.Abs(d);
        return a switch
        {
            >= 1e9 => (d / 1e9).ToString("0.##") + "bn",
            >= 1e6 => (d / 1e6).ToString("0.##") + "M",
            >= 1e3 => (d / 1e3).ToString("0.##") + "K",
            _ => FormatNumber(d),
        };
    }
}
