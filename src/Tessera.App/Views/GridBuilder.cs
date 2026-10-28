using Avalonia.Controls;
using Avalonia.Data;
using Tessera.Core;

namespace Tessera.App.Views;

/// <summary>Shows a <see cref="QueryResult"/> in a DataGrid with one text column per result column.</summary>
public static class GridBuilder
{
    public static void Fill(DataGrid grid, QueryResult? result)
    {
        grid.ItemsSource = null;
        grid.Columns.Clear();
        if (result is null) return;

        for (var i = 0; i < result.Columns.Count; i++)
        {
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = result.Columns[i],
                Binding = new ReflectionBinding($"[{i}]"),
            });
        }
        grid.ItemsSource = result.Rows.Select(r => r.Select((v, i) => Cell(result, v, i)).ToArray()).ToList();
    }

    static string Cell(QueryResult result, object? value, int column) =>
        result.FormatOf(column) is { } format && value is not string && Sql.ToDouble(value) is { } d
            ? Sql.FormatNumber(d, format, compact: false)
            : Sql.Format(value);

    public static DataGrid Create(QueryResult result)
    {
        var grid = new DataGrid
        {
            IsReadOnly = true,
            CanUserSortColumns = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
        };
        Fill(grid, result);
        return grid;
    }
}
