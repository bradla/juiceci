using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tessera.Core;

namespace Tessera.App.ViewModels;

// ---- Modeling › Manage relationships ----

public sealed partial class RelationshipRowViewModel(Relationship relationship, Action<RelationshipRowViewModel> remove) : ObservableObject
{
    public Relationship Relationship { get; } = relationship;
    public string From => $"{Relationship.FromTable} ({Relationship.FromColumn})";
    public string To => $"{Relationship.ToTable} ({Relationship.ToColumn})";

    [ObservableProperty]
    public partial bool IsActive { get; set; } = relationship.IsActive;

    [RelayCommand]
    void Remove() => remove(this);
}

/// <summary>The relationships between tables: each row of the "from" table matches one row of the "to" table.</summary>
public sealed partial class RelationshipsViewModel : ObservableObject
{
    readonly DataEngine _engine;

    public RelationshipsViewModel(DataEngine engine)
    {
        _engine = engine;
        Tables = engine.Tables.Where(t => t.Fields.Any(f => !f.IsMeasure)).Select(t => t.Name).ToList();
        foreach (var r in engine.Model.Relationships) Rows.Add(new RelationshipRowViewModel(r, row => Rows.Remove(row)));
        FromTable = Tables.FirstOrDefault();
        ToTable = Tables.Skip(1).FirstOrDefault() ?? Tables.FirstOrDefault();
    }

    public IReadOnlyList<string> Tables { get; }
    public ObservableCollection<RelationshipRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FromColumns))]
    public partial string? FromTable { get; set; }

    [ObservableProperty]
    public partial string? FromColumn { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToColumns))]
    public partial string? ToTable { get; set; }

    [ObservableProperty]
    public partial string? ToColumn { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    public IReadOnlyList<string> FromColumns => Columns(FromTable);
    public IReadOnlyList<string> ToColumns => Columns(ToTable);

    IReadOnlyList<string> Columns(string? table) =>
        _engine.FindTable(table ?? "")?.Fields.Where(f => !f.IsMeasure).Select(f => f.Name).ToList() ?? [];

    partial void OnFromTableChanged(string? value) => FromColumn = FromColumns.FirstOrDefault();
    partial void OnToTableChanged(string? value) => ToColumn = ToColumns.FirstOrDefault();

    /// <summary>Adds the relationship in the New row, after checking the "to" column can be a lookup key.</summary>
    [RelayCommand]
    void Add()
    {
        if (FromTable is null || FromColumn is null || ToTable is null || ToColumn is null)
        {
            Error = "Pick a table and column on both sides.";
            return;
        }
        if (FromTable == ToTable)
        {
            Error = "A relationship joins two different tables.";
            return;
        }
        if (Rows.Any(r => r.Relationship.FromTable == FromTable && r.Relationship.ToTable == ToTable))
        {
            Error = $"{FromTable} and {ToTable} are already related.";
            return;
        }
        if (!_engine.IsUnique(ToTable, ToColumn))
        {
            Error = $"{ToTable}[{ToColumn}] has repeated values, so it can't be the \"one\" side. Swap the tables, or pick a key column.";
            return;
        }
        // A second path between tables would be ambiguous, so it starts out inactive, as in Power BI.
        var active = !Rows.Any(r => r.IsActive && r.Relationship.FromTable == FromTable && r.Relationship.ToTable == ToTable);
        Rows.Add(new RelationshipRowViewModel(new Relationship(FromTable, FromColumn, ToTable, ToColumn, active), row => Rows.Remove(row)));
        Error = null;
    }

    public SemanticModel ApplyTo(SemanticModel model) =>
        model.Copy(m => m.Relationships = Rows.Select(r => r.Relationship with { IsActive = r.IsActive }).ToList());
}

// ---- Modeling › New column / New table ----

/// <summary>A calculated column (per-row DAX on one table) or a calculated table (a DAX table expression).</summary>
public sealed partial class CalculationViewModel : ObservableObject
{
    readonly DataEngine _engine;

    public CalculationViewModel(DataEngine engine, bool isTable, string? table)
    {
        _engine = engine;
        IsTable = isTable;
        Tables = engine.Tables.Where(t => t.Fields.Any(f => !f.IsMeasure)).Select(t => t.Name).ToList();
        Table = table is not null && Tables.Contains(table) ? table : Tables.FirstOrDefault() ?? "";
        Name = isTable ? "Table" : "Column";
    }

    public bool IsTable { get; }
    public bool IsColumn => !IsTable;
    public string Heading => IsTable ? "New table" : "New column";
    public IReadOnlyList<string> Tables { get; }

    public string Hint => IsTable
        ? "A DAX table expression: a table, FILTER(Sales, Sales[Amount] > 100), DISTINCT(Sales[Region]) or CALENDAR(DATE(2024,1,1), DATE(2024,12,31))."
        : "A DAX expression worked out for each row, using the table's own columns: Sales[Amount] * 1.2, IF(Sales[Status] = \"Won\", 1, 0), YEAR(Sales[Date]).";

    [ObservableProperty]
    public partial string Table { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string Expression { get; set; } = "";

    [ObservableProperty]
    public partial string? Error { get; set; }

    public bool Validate()
    {
        Error = null;
        if (string.IsNullOrWhiteSpace(Name)) Error = "Give it a name.";
        else if (string.IsNullOrWhiteSpace(Expression)) Error = "Write a DAX expression.";
        else if (IsColumn && _engine.FindTable(Table)?.Fields.Any(f => f.Name.Equals(Name.Trim(), StringComparison.OrdinalIgnoreCase)) == true)
            Error = $"{Table} already has a field called {Name.Trim()}.";
        else
        {
            try
            {
                if (IsTable) DaxTranslator.TranslateTable(Expression, _engine.Model);
                else DaxTranslator.TranslateRow(Expression, Table, _engine.Model);
            }
            catch (NotSupportedException e)
            {
                Error = e.Message;
            }
        }
        return Error is null;
    }
}

// ---- Modeling › Manage roles ----

public sealed partial class PermissionViewModel(string table, string expression) : ObservableObject
{
    public string Table { get; } = table;

    [ObservableProperty]
    public partial string Expression { get; set; } = expression;
}

public sealed partial class RoleViewModel : ObservableObject
{
    public RoleViewModel(string name, IEnumerable<string> tables, IReadOnlyList<TablePermission> permissions)
    {
        Name = name;
        foreach (var t in tables)
            Permissions.Add(new PermissionViewModel(t, permissions.FirstOrDefault(p => p.Table == t)?.Expression ?? ""));
    }

    [ObservableProperty]
    public partial string Name { get; set; }

    /// <summary>One DAX condition per table; empty means every row of it.</summary>
    public ObservableCollection<PermissionViewModel> Permissions { get; } = [];

    public Role ToRole() => new(Name.Trim(),
        Permissions.Where(p => !string.IsNullOrWhiteSpace(p.Expression)).Select(p => new TablePermission(p.Table, p.Expression.Trim())).ToList());
}

/// <summary>Row-level security roles: each limits tables to the rows a DAX condition keeps.</summary>
public sealed partial class RolesViewModel : ObservableObject
{
    readonly DataEngine _engine;
    readonly List<string> _tables;

    public RolesViewModel(DataEngine engine)
    {
        _engine = engine;
        _tables = engine.Tables.Where(t => t.Fields.Any(f => !f.IsMeasure)).Select(t => t.Name).ToList();
        foreach (var r in engine.Model.Roles) Roles.Add(new RoleViewModel(r.Name, _tables, r.Permissions));
        Selected = Roles.FirstOrDefault();
    }

    public ObservableCollection<RoleViewModel> Roles { get; } = [];

    [ObservableProperty]
    public partial RoleViewModel? Selected { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    [RelayCommand]
    void AddRole()
    {
        var n = 1;
        while (Roles.Any(r => r.Name == $"Role {n}")) n++;
        var role = new RoleViewModel($"Role {n}", _tables, []);
        Roles.Add(role);
        Selected = role;
    }

    [RelayCommand]
    void RemoveRole()
    {
        if (Selected is null) return;
        Roles.Remove(Selected);
        Selected = Roles.FirstOrDefault();
    }

    public bool Validate()
    {
        Error = null;
        foreach (var role in Roles)
        {
            if (string.IsNullOrWhiteSpace(role.Name)) Error = "Every role needs a name.";
            else if (Roles.Count(r => r.Name.Trim() == role.Name.Trim()) > 1) Error = $"There are two roles called {role.Name}.";
            else
                foreach (var p in role.Permissions.Where(p => !string.IsNullOrWhiteSpace(p.Expression)))
                {
                    try
                    {
                        DaxTranslator.TranslateRow(p.Expression, p.Table, _engine.Model);
                    }
                    catch (NotSupportedException e)
                    {
                        Error = $"{role.Name}, {p.Table}: {e.Message}";
                        Selected = role;
                        break;
                    }
                }
            if (Error is not null) return false;
        }
        return true;
    }

    public SemanticModel ApplyTo(SemanticModel model) => model.Copy(m => m.Roles = Roles.Select(r => r.ToRole()).ToList());
}
