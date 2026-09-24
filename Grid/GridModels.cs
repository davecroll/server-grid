using System.Globalization;
using System.Linq.Expressions;
using System.Text;

namespace ServerGrid.Grid;

/// <summary>Condition operators available in a column filter. Which ones apply depends on the column's <see cref="GridValueKind"/>.</summary>
public enum FilterOperator
{
    Contains,
    NotContains,
    EqualTo,
    NotEqualTo,
    StartsWith,
    EndsWith,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    Between,
    IsEmpty,
    IsNotEmpty,
}

/// <summary>How the value-list part of a column filter is interpreted.</summary>
public enum ValueSetMode
{
    /// <summary>No value-list restriction.</summary>
    None,
    /// <summary>Only rows whose value is in the set pass.</summary>
    Include,
    /// <summary>Rows whose value is in the set are removed. This is what makes "untick a few of 50,000 values" cheap.</summary>
    Exclude,
}

public enum GridValueKind { Text, Number, Date, Boolean, Enum, Other }

public sealed record SortDescriptor(string ColumnKey, bool Descending);

/// <summary>
/// Excel-style column filter: an optional condition (operator + one or two operands) AND an optional value list.
/// A <c>null</c> inside <see cref="Values"/> stands for "(Blanks)".
/// </summary>
public sealed class ColumnFilter
{
    public required string ColumnKey { get; init; }
    public FilterOperator? Operator { get; set; }
    public object? Value { get; set; }
    public object? Value2 { get; set; }
    public ValueSetMode ValueSetMode { get; set; } = ValueSetMode.None;
    public IReadOnlySet<object?> Values { get; set; } = new HashSet<object?>();

    public bool HasCondition => Operator is not null;
    public bool HasValueSet => ValueSetMode != ValueSetMode.None;
    public bool IsActive => HasCondition || HasValueSet;

    public ColumnFilter Clone() => new()
    {
        ColumnKey = ColumnKey,
        Operator = Operator,
        Value = Value,
        Value2 = Value2,
        ValueSetMode = ValueSetMode,
        Values = new HashSet<object?>(Values),
    };

    internal void AppendKey(StringBuilder sb)
    {
        sb.Append(ColumnKey).Append('|').Append(Operator).Append('|')
          .Append(Convert.ToString(Value, CultureInfo.InvariantCulture)).Append('|')
          .Append(Convert.ToString(Value2, CultureInfo.InvariantCulture)).Append('|')
          .Append(ValueSetMode).Append('|');
        if (ValueSetMode != ValueSetMode.None)
        {
            foreach (var v in Values.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "\0").Order(StringComparer.Ordinal))
                sb.Append(v).Append('\u001f');
        }
    }
}

/// <summary>
/// The data-source-facing description of a column: how to read the value (as an expression, so it can be
/// pushed into any IQueryable provider), its CLR type, and how to format it for the value list.
/// </summary>
public sealed class GridColumnDefinition<TItem>
{
    private Func<TItem, object?>? _getter;

    public required string Key { get; init; }
    public required LambdaExpression Selector { get; init; }
    public required Type ValueType { get; init; }
    public bool QuickSearchable { get; init; }
    public Func<object?, string> Format { get; init; } = v => Convert.ToString(v, CultureInfo.CurrentCulture) ?? string.Empty;

    public Type NonNullableType => Nullable.GetUnderlyingType(ValueType) ?? ValueType;
    public bool IsNullable => !ValueType.IsValueType || Nullable.GetUnderlyingType(ValueType) is not null;
    public GridValueKind Kind => GetKind(NonNullableType);

    /// <summary>Compiled, boxing getter used by in-memory distinct-value scans.</summary>
    public Func<TItem, object?> GetValue => _getter ??= CompileGetter();

    private Func<TItem, object?> CompileGetter()
    {
        var body = Expression.Convert(Selector.Body, typeof(object));
        return Expression.Lambda<Func<TItem, object?>>(body, Selector.Parameters).Compile();
    }

    public static GridValueKind GetKind(Type t)
    {
        if (t == typeof(string) || t == typeof(char)) return GridValueKind.Text;
        if (t == typeof(bool)) return GridValueKind.Boolean;
        if (t.IsEnum) return GridValueKind.Enum;
        if (t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(DateOnly)) return GridValueKind.Date;
        if (t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort) ||
            t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong) ||
            t == typeof(float) || t == typeof(double) || t == typeof(decimal)) return GridValueKind.Number;
        return GridValueKind.Other;
    }
}

/// <summary>Everything a data source needs to produce rows: the column set plus current filter/sort/search state.</summary>
public sealed class GridRequest<TItem>
{
    public required IReadOnlyList<GridColumnDefinition<TItem>> Columns { get; init; }
    public IReadOnlyList<ColumnFilter> Filters { get; init; } = [];
    public IReadOnlyList<SortDescriptor> Sorts { get; init; } = [];
    public string? QuickSearch { get; init; }

    private string? _cacheKey;

    /// <summary>Stable key describing the filter/sort/search state, used by data sources to cache materialised views.</summary>
    public string CacheKey
    {
        get
        {
            if (_cacheKey is not null) return _cacheKey;
            var sb = new StringBuilder();
            foreach (var f in Filters.Where(f => f.IsActive).OrderBy(f => f.ColumnKey, StringComparer.Ordinal))
            {
                f.AppendKey(sb);
                sb.Append('\u001e');
            }
            sb.Append("||");
            foreach (var s in Sorts) sb.Append(s.ColumnKey).Append(s.Descending ? "↓" : "↑").Append(',');
            sb.Append("||").Append(QuickSearch);
            return _cacheKey = sb.ToString();
        }
    }

    public GridColumnDefinition<TItem> GetColumn(string key) =>
        Columns.FirstOrDefault(c => c.Key == key) ?? throw new InvalidOperationException($"Unknown grid column '{key}'.");
}

/// <summary>A row handed to the virtualised renderer, carrying its absolute index in the filtered/sorted view.</summary>
public readonly record struct GridRow<TItem>(int Index, TItem Item);

public sealed record GridDataResult<TItem>(IReadOnlyList<TItem> Items, int TotalCount, int UnfilteredCount);

public sealed record GridDistinctValuesResult(IReadOnlyList<object> Values, bool HasBlanks, int TotalDistinct)
{
    public bool IsTruncated => Values.Count < TotalDistinct;
}
