namespace ServerGrid.Grid;

/// <summary>
/// Supplies rows and filter value lists to <see cref="ServerGrid.Components.Grid.ServerGrid{TItem}"/>.
/// Implementations do all filtering, sorting and paging on the server; only the rows currently
/// visible in the viewport are ever handed to the renderer.
/// </summary>
public interface IGridDataSource<TItem>
{
    /// <summary>Returns rows <paramref name="startIndex"/>..<paramref name="startIndex"/>+<paramref name="count"/> of the filtered, sorted view.</summary>
    ValueTask<GridDataResult<TItem>> GetRowsAsync(GridRequest<TItem> request, int startIndex, int count, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the distinct values of <paramref name="columnKey"/> for the Excel-style value list. The column's own
    /// filter is ignored; every other filter and the quick search still apply (exactly like Excel).
    /// </summary>
    ValueTask<GridDistinctValuesResult> GetDistinctValuesAsync(GridRequest<TItem> request, string columnKey, string? search, int maxResults, CancellationToken cancellationToken);
}
