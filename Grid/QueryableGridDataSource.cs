using System.Linq.Expressions;

namespace ServerGrid.Grid;

/// <summary>
/// Data source that pushes filtering, sorting and paging into an <see cref="IQueryable{T}"/> provider
/// (EF Core, etc.) via Where/OrderBy/Skip/Take. Nothing is materialised beyond the requested slice.
/// Supply <see cref="MaterializeAsync"/>/<see cref="CountAsync"/> to use the provider's async APIs.
/// </summary>
public sealed class QueryableGridDataSource<TItem> : IGridDataSource<TItem>
{
    private readonly Func<IQueryable<TItem>> _query;
    private string? _countKey;
    private int _count;
    private int? _unfilteredCount;

    public QueryableGridDataSource(Func<IQueryable<TItem>> query) => _query = query;

    public StringMatchMode StringMatchMode { get; init; } = StringMatchMode.Provider;
    public Func<IQueryable<TItem>, CancellationToken, Task<List<TItem>>> MaterializeAsync { get; init; } = (q, _) => Task.FromResult(q.ToList());
    public Func<IQueryable<TItem>, CancellationToken, Task<int>> CountAsync { get; init; } = (q, _) => Task.FromResult(q.Count());
    public Func<IQueryable<object>, CancellationToken, Task<List<object>>> MaterializeValuesAsync { get; init; } = (q, _) => Task.FromResult(q.ToList());

    public async ValueTask<GridDataResult<TItem>> GetRowsAsync(GridRequest<TItem> request, int startIndex, int count, CancellationToken cancellationToken)
    {
        var baseQuery = _query();
        _unfilteredCount ??= await CountAsync(baseQuery, cancellationToken);

        var filtered = baseQuery;
        if (GridExpressionBuilder.BuildPredicate(request, mode: StringMatchMode) is { } predicate)
            filtered = filtered.Where(predicate);

        if (_countKey != request.CacheKey)
        {
            _count = await CountAsync(filtered, cancellationToken);
            _countKey = request.CacheKey;
        }

        var ordered = GridExpressionBuilder.ApplySorting(filtered, request);
        var items = await MaterializeAsync(ordered.Skip(startIndex).Take(count), cancellationToken);
        return new GridDataResult<TItem>(items, _count, _unfilteredCount.Value);
    }

    public async ValueTask<GridDistinctValuesResult> GetDistinctValuesAsync(GridRequest<TItem> request, string columnKey, string? search, int maxResults, CancellationToken cancellationToken)
    {
        var column = request.GetColumn(columnKey);
        var filtered = _query();
        if (GridExpressionBuilder.BuildPredicate(request, ignoreColumnKey: columnKey, mode: StringMatchMode) is { } predicate)
            filtered = filtered.Where(predicate);

        // filtered.Select(selector).Distinct().OrderBy(v => v)  — built by name so it works for any TValue.
        var selectCall = Expression.Call(typeof(Queryable), nameof(Queryable.Select), [typeof(TItem), column.ValueType], filtered.Expression, Expression.Quote(column.Selector));
        var distinctCall = Expression.Call(typeof(Queryable), nameof(Queryable.Distinct), [column.ValueType], selectCall);
        var v = Expression.Parameter(column.ValueType, "v");
        var identity = Expression.Lambda(v, v);
        var orderCall = Expression.Call(typeof(Queryable), nameof(Queryable.OrderBy), [column.ValueType, column.ValueType], distinctCall, Expression.Quote(identity));
        var boxed = Expression.Call(typeof(Queryable), nameof(Queryable.Select), [column.ValueType, typeof(object)], orderCall,
            Expression.Quote(Expression.Lambda(Expression.Convert(v, typeof(object)), v)));
        var valuesQuery = filtered.Provider.CreateQuery<object>(boxed);

        var all = await MaterializeValuesAsync(valuesQuery, cancellationToken);
        var hasBlanks = all.Any(x => x is null || (x is string s && s.Length == 0));
        IEnumerable<object> values = all.Where(x => x is not null && !(x is string s && s.Length == 0))!;
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            values = values.Where(x => column.Format(x).Contains(term, StringComparison.OrdinalIgnoreCase));
        }
        var list = values.ToList();
        return new GridDistinctValuesResult(list.Take(maxResults).ToList(), hasBlanks, list.Count);
    }
}
