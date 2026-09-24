using System.Linq.Expressions;
using ServerGrid.Grid;
using Xunit;

namespace ServerGrid.Tests;

public enum Colour { Red, Green, Blue }

public sealed record Item(int Id, string Name, string? Notes, decimal Price, decimal? Discount, DateOnly Day, Colour Colour, bool Active);

public class GridExpressionBuilderTests
{
    private static readonly Item[] Items =
    [
        new(1, "Apple", "fresh", 1.50m, null, new DateOnly(2024, 1, 1), Colour.Red, true),
        new(2, "banana", "", 0.75m, 0.10m, new DateOnly(2024, 1, 2), Colour.Green, false),
        new(3, "Cherry", null, 4.00m, 0.50m, new DateOnly(2024, 2, 1), Colour.Red, true),
        new(4, "Date", "dried", 6.25m, null, new DateOnly(2024, 3, 1), Colour.Blue, false),
        new(5, "apricot", "fresh", 2.00m, 0.25m, new DateOnly(2024, 3, 2), Colour.Green, true),
    ];

    private static GridColumnDefinition<Item> Col<TValue>(string key, Expression<Func<Item, TValue>> selector, bool quickSearch = false) =>
        new() { Key = key, Selector = selector, ValueType = typeof(TValue), QuickSearchable = quickSearch };

    private static readonly GridColumnDefinition<Item>[] Columns =
    [
        Col("Id", i => i.Id),
        Col("Name", i => i.Name, quickSearch: true),
        Col("Notes", i => i.Notes, quickSearch: true),
        Col("Price", i => i.Price),
        Col("Discount", i => i.Discount),
        Col("Day", i => i.Day),
        Col("Colour", i => i.Colour),
        Col("Active", i => i.Active),
    ];

    private static GridRequest<Item> Request(IEnumerable<ColumnFilter>? filters = null, IEnumerable<SortDescriptor>? sorts = null, string? search = null) =>
        new() { Columns = Columns, Filters = filters?.ToList() ?? [], Sorts = sorts?.ToList() ?? [], QuickSearch = search };

    private static int[] Ids(GridRequest<Item> request)
    {
        IEnumerable<Item> q = Items;
        if (GridExpressionBuilder.BuildPredicate(request) is { } predicate) q = q.Where(predicate.Compile());
        return GridExpressionBuilder.ApplySorting(q.AsQueryable(), request).Select(i => i.Id).ToArray();
    }

    [Fact]
    public void Text_contains_is_case_insensitive()
    {
        var ids = Ids(Request([new ColumnFilter { ColumnKey = "Name", Operator = FilterOperator.Contains, Value = "AP" }]));
        Assert.Equal([1, 5], ids);
    }

    [Fact]
    public void Text_not_contains_keeps_rows_without_the_term()
    {
        var ids = Ids(Request([new ColumnFilter { ColumnKey = "Name", Operator = FilterOperator.NotContains, Value = "a" }]));
        Assert.Equal([3], ids); // "Cherry" is the only name without an 'a'
    }

    [Fact]
    public void Is_empty_on_text_matches_null_and_empty_string()
    {
        var ids = Ids(Request([new ColumnFilter { ColumnKey = "Notes", Operator = FilterOperator.IsEmpty }]));
        Assert.Equal([2, 3], ids);
    }

    [Fact]
    public void Value_set_include_with_blanks()
    {
        var filter = new ColumnFilter { ColumnKey = "Notes", ValueSetMode = ValueSetMode.Include, Values = new HashSet<object?> { "dried", null } };
        Assert.Equal([2, 3, 4], Ids(Request([filter])));
    }

    [Fact]
    public void Value_set_exclude_on_enum()
    {
        var filter = new ColumnFilter { ColumnKey = "Colour", ValueSetMode = ValueSetMode.Exclude, Values = new HashSet<object?> { Colour.Red } };
        Assert.Equal([2, 4, 5], Ids(Request([filter])));
    }

    [Fact]
    public void Value_set_accepts_string_representations_of_typed_values()
    {
        var filter = new ColumnFilter { ColumnKey = "Id", ValueSetMode = ValueSetMode.Include, Values = new HashSet<object?> { "2", 4 } };
        Assert.Equal([2, 4], Ids(Request([filter])));
    }

    [Fact]
    public void Between_on_decimal_is_inclusive()
    {
        var filter = new ColumnFilter { ColumnKey = "Price", Operator = FilterOperator.Between, Value = "1.5", Value2 = 4 };
        Assert.Equal([1, 3, 5], Ids(Request([filter])));
    }

    [Fact]
    public void Comparisons_on_nullable_columns_skip_nulls()
    {
        var filter = new ColumnFilter { ColumnKey = "Discount", Operator = FilterOperator.LessThan, Value = 0.5m };
        Assert.Equal([2, 5], Ids(Request([filter])));
    }

    [Fact]
    public void Nullable_value_set_with_blanks()
    {
        var filter = new ColumnFilter { ColumnKey = "Discount", ValueSetMode = ValueSetMode.Include, Values = new HashSet<object?> { null, 0.25m } };
        Assert.Equal([1, 4, 5], Ids(Request([filter])));
    }

    [Fact]
    public void Date_filters_parse_iso_strings()
    {
        var filter = new ColumnFilter { ColumnKey = "Day", Operator = FilterOperator.GreaterThanOrEqual, Value = "2024-02-01" };
        Assert.Equal([3, 4, 5], Ids(Request([filter])));
    }

    [Fact]
    public void Enum_equals_and_bool_value_sets()
    {
        var ids = Ids(Request(
        [
            new ColumnFilter { ColumnKey = "Colour", Operator = FilterOperator.EqualTo, Value = "Green" },
            new ColumnFilter { ColumnKey = "Active", ValueSetMode = ValueSetMode.Include, Values = new HashSet<object?> { true } },
        ]));
        Assert.Equal([5], ids);
    }

    [Fact]
    public void Quick_search_spans_all_searchable_string_columns()
    {
        Assert.Equal([1, 5], Ids(Request(search: "fresh")));
        Assert.Equal([2], Ids(Request(search: "BANANA")));
    }

    [Fact]
    public void Multi_level_sorting()
    {
        var ids = Ids(Request(sorts: [new SortDescriptor("Colour", false), new SortDescriptor("Price", true)]));
        Assert.Equal([3, 1, 5, 2, 4], ids); // Red (4.00, 1.50), Green (2.00, 0.75), Blue (6.25)
    }

    [Fact]
    public void Cache_key_is_stable_and_order_independent_for_value_sets()
    {
        var a = Request([new ColumnFilter { ColumnKey = "Name", ValueSetMode = ValueSetMode.Include, Values = new HashSet<object?> { "x", "y" } }]);
        var b = Request([new ColumnFilter { ColumnKey = "Name", ValueSetMode = ValueSetMode.Include, Values = new HashSet<object?> { "y", "x" } }]);
        var c = Request([new ColumnFilter { ColumnKey = "Name", ValueSetMode = ValueSetMode.Exclude, Values = new HashSet<object?> { "x", "y" } }]);
        Assert.Equal(a.CacheKey, b.CacheKey);
        Assert.NotEqual(a.CacheKey, c.CacheKey);
    }
}

public class DataSourceTests
{
    private static readonly Item[] Items = Enumerable.Range(1, 1000)
        .Select(i => new Item(i, $"Item {i:D4}", i % 7 == 0 ? null : $"note {i % 5}", i * 1.5m, i % 3 == 0 ? null : i * 0.1m,
            new DateOnly(2024, 1, 1).AddDays(i % 100), (Colour)(i % 3), i % 2 == 0))
        .ToArray();

    private static readonly GridColumnDefinition<Item>[] Columns =
    [
        new() { Key = "Id", Selector = (Expression<Func<Item, int>>)(i => i.Id), ValueType = typeof(int) },
        new() { Key = "Name", Selector = (Expression<Func<Item, string>>)(i => i.Name), ValueType = typeof(string), QuickSearchable = true },
        new() { Key = "Notes", Selector = (Expression<Func<Item, string?>>)(i => i.Notes), ValueType = typeof(string) },
        new() { Key = "Colour", Selector = (Expression<Func<Item, Colour>>)(i => i.Colour), ValueType = typeof(Colour) },
        new() { Key = "Price", Selector = (Expression<Func<Item, decimal>>)(i => i.Price), ValueType = typeof(decimal) },
    ];

    public static IEnumerable<object[]> Sources()
    {
        yield return [new InMemoryGridDataSource<Item>(Items)];
        yield return [new QueryableGridDataSource<Item>(() => Items.AsQueryable()) { StringMatchMode = StringMatchMode.OrdinalIgnoreCase }];
    }

    [Theory, MemberData(nameof(Sources))]
    public async Task Rows_are_sliced_from_the_filtered_sorted_view(IGridDataSource<Item> source)
    {
        var request = new GridRequest<Item>
        {
            Columns = Columns,
            Filters = [new ColumnFilter { ColumnKey = "Colour", ValueSetMode = ValueSetMode.Include, Values = new HashSet<object?> { Colour.Red } }],
            Sorts = [new SortDescriptor("Price", true)],
        };

        var page = await source.GetRowsAsync(request, 10, 5, CancellationToken.None);

        Assert.Equal(333, page.TotalCount);       // ids divisible by 3 → Colour.Red
        Assert.Equal(1000, page.UnfilteredCount);
        Assert.Equal([969, 966, 963, 960, 957], page.Items.Select(i => i.Id));
    }

    [Theory, MemberData(nameof(Sources))]
    public async Task Requests_past_the_end_return_empty(IGridDataSource<Item> source)
    {
        var page = await source.GetRowsAsync(new GridRequest<Item> { Columns = Columns }, 5000, 50, CancellationToken.None);
        Assert.Empty(page.Items);
        Assert.Equal(1000, page.TotalCount);
    }

    [Theory, MemberData(nameof(Sources))]
    public async Task Distinct_values_ignore_the_columns_own_filter_but_honour_others(IGridDataSource<Item> source)
    {
        var request = new GridRequest<Item>
        {
            Columns = Columns,
            Filters =
            [
                new ColumnFilter { ColumnKey = "Colour", ValueSetMode = ValueSetMode.Include, Values = new HashSet<object?> { Colour.Blue } },
                new ColumnFilter { ColumnKey = "Id", Operator = FilterOperator.LessThanOrEqual, Value = 20 },
            ],
        };

        var colours = await source.GetDistinctValuesAsync(request, "Colour", null, 100, CancellationToken.None);
        Assert.Equal([Colour.Red, Colour.Green, Colour.Blue], colours.Values.Cast<Colour>());

        var notes = await source.GetDistinctValuesAsync(request, "Notes", null, 100, CancellationToken.None);
        Assert.True(notes.HasBlanks);                                        // id 14 (Blue, ≤20) has a null note
        Assert.Equal(["note 0", "note 1", "note 2", "note 3"], notes.Values.Cast<string>().Order()); // ids 2,5,8,11,17,20 → i % 5
    }

    [Theory, MemberData(nameof(Sources))]
    public async Task Distinct_values_support_search_and_truncation(IGridDataSource<Item> source)
    {
        var request = new GridRequest<Item> { Columns = Columns };
        var result = await source.GetDistinctValuesAsync(request, "Name", "099", 3, CancellationToken.None);
        Assert.Equal(11, result.TotalDistinct);   // Item 0099 plus 0990..0999
        Assert.Equal(3, result.Values.Count);
        Assert.True(result.IsTruncated);
    }
}
