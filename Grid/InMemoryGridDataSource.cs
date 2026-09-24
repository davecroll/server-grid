namespace ServerGrid.Grid;

/// <summary>
/// Data source over an in-memory collection. The filtered + sorted view is materialised once per distinct
/// filter/sort state and cached, so scrolling only slices an array. Heavy work runs on the thread pool so the
/// Blazor circuit stays responsive while a million rows are being sorted.
/// </summary>
public sealed class InMemoryGridDataSource<TItem> : IGridDataSource<TItem>
{
    private readonly TItem[] _items;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _viewKey;
    private TItem[]? _view;

    public InMemoryGridDataSource(IEnumerable<TItem> items)
    {
        _items = items as TItem[] ?? items.ToArray();
    }

    /// <summary>Comparer used when sorting string columns. Ordinal is dramatically faster than culture-aware comparison on large sets.</summary>
    public IComparer<string> StringSortComparer { get; init; } = StringComparer.OrdinalIgnoreCase;

    public async ValueTask<GridDataResult<TItem>> GetRowsAsync(GridRequest<TItem> request, int startIndex, int count, CancellationToken cancellationToken)
    {
        var view = await GetViewAsync(request, cancellationToken);
        if (startIndex >= view.Length || count <= 0)
            return new GridDataResult<TItem>([], view.Length, _items.Length);

        var take = Math.Min(count, view.Length - startIndex);
        return new GridDataResult<TItem>(new ArraySegment<TItem>(view, startIndex, take), view.Length, _items.Length);
    }

    public async ValueTask<GridDistinctValuesResult> GetDistinctValuesAsync(GridRequest<TItem> request, string columnKey, string? search, int maxResults, CancellationToken cancellationToken)
    {
        var column = request.GetColumn(columnKey);
        var predicate = GridExpressionBuilder.BuildPredicate(request, ignoreColumnKey: columnKey)?.Compile();
        var getter = column.GetValue;
        var format = column.Format;
        var items = _items;

        return await Task.Run(() =>
        {
            var set = new HashSet<object>();
            var hasBlanks = false;
            foreach (var item in items)
            {
                if (predicate is not null && !predicate(item)) continue;
                var value = getter(item);
                if (value is null || (value is string s && s.Length == 0)) hasBlanks = true;
                else set.Add(value);
                if ((set.Count & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            }

            IEnumerable<object> values = set;
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                values = values.Where(v => format(v).Contains(term, StringComparison.OrdinalIgnoreCase));
            }

            var ordered = column.ValueType == typeof(string)
                ? values.OrderBy(v => (string)v, StringSortComparer).ToList()
                : values.OrderBy(v => v, Comparer<object>.Default).ToList();

            var page = ordered.Count > maxResults ? ordered.GetRange(0, maxResults) : ordered;
            var showBlanks = hasBlanks && (string.IsNullOrWhiteSpace(search) || "(Blanks)".Contains(search.Trim(), StringComparison.OrdinalIgnoreCase));
            return new GridDistinctValuesResult(page, showBlanks, ordered.Count);
        }, cancellationToken);
    }

    private async ValueTask<TItem[]> GetViewAsync(GridRequest<TItem> request, CancellationToken cancellationToken)
    {
        var key = request.CacheKey;
        if (_viewKey == key && _view is not null) return _view;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_viewKey == key && _view is not null) return _view;

            var predicate = GridExpressionBuilder.BuildPredicate(request)?.Compile();
            var hasSort = request.Sorts.Count > 0;
            var source = _items;

            var view = await Task.Run(() =>
            {
                IEnumerable<TItem> q = source;
                if (predicate is not null) q = q.Where(predicate);
                if (!hasSort) return predicate is null ? source : q.ToArray();
                var filtered = predicate is null ? source : q.ToArray();
                cancellationToken.ThrowIfCancellationRequested();
                return GridExpressionBuilder.ApplySorting(filtered.AsQueryable(), request, StringSortComparer).ToArray();
            }, cancellationToken);

            _view = view;
            _viewKey = key;
            return view;
        }
        finally
        {
            _gate.Release();
        }
    }
}
