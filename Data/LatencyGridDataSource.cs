using ServerGrid.Grid;

namespace ServerGrid.Data;

/// <summary>Demo helper: adds an artificial delay to every data-source call to simulate a slow network or database.</summary>
public sealed class LatencyGridDataSource<TItem>(IGridDataSource<TItem> inner, int delayMs) : IGridDataSource<TItem>
{
    public async ValueTask<GridDataResult<TItem>> GetRowsAsync(GridRequest<TItem> request, int startIndex, int count, CancellationToken cancellationToken)
    {
        await Task.Delay(delayMs, cancellationToken);
        return await inner.GetRowsAsync(request, startIndex, count, cancellationToken);
    }

    public async ValueTask<GridDistinctValuesResult> GetDistinctValuesAsync(GridRequest<TItem> request, string columnKey, string? search, int maxResults, CancellationToken cancellationToken)
    {
        await Task.Delay(delayMs, cancellationToken);
        return await inner.GetDistinctValuesAsync(request, columnKey, search, maxResults, cancellationToken);
    }
}
