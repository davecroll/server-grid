using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using ServerGrid.Grid;

namespace ServerGrid.Components.Grid;

/// <summary>
/// Server-side data grid for Blazor Interactive Server. All data stays on the server: the component asks its
/// <see cref="IGridDataSource{TItem}"/> only for the rows currently in the viewport, renders them, and Blazor ships
/// the resulting HTML diff over the circuit. Sorting, per-column Excel-style filtering, quick search, column
/// resizing/hiding and cell selection are all handled without any custom JavaScript.
/// </summary>
[CascadingTypeParameter(nameof(TItem))]
public partial class ServerGrid<TItem> : ComponentBase, IAsyncDisposable
{
    [Inject] private IJSRuntime JS { get; set; } = default!;

    [Parameter, EditorRequired] public IGridDataSource<TItem> DataSource { get; set; } = default!;
    [Parameter] public RenderFragment? ChildContent { get; set; }

    /// <summary>Fixed row height in pixels. Required for virtualisation maths.</summary>
    [Parameter] public float RowHeight { get; set; } = 30;

    /// <summary>CSS height of the scrolling viewport.</summary>
    [Parameter] public string Height { get; set; } = "calc(100vh - 12rem)";

    [Parameter] public bool ShowToolbar { get; set; } = true;
    [Parameter] public bool ShowStatusBar { get; set; } = true;
    [Parameter] public bool AllowColumnResize { get; set; } = true;

    /// <summary>Shows scroller internals (anchor row, rendered window, fetch count) in the status bar.</summary>
    [Parameter] public bool ShowDiagnostics { get; set; }

    /// <summary>Maximum number of distinct values shown in a filter drop-down before the list is truncated (use search to narrow).</summary>
    [Parameter] public int MaxFilterValues { get; set; } = 250;

    /// <summary>Rows rendered beyond the visible area in the direction of travel. Together with <see cref="FetchMargin"/> this is the
    /// "runway" the browser can scroll through natively before the server is asked for a new window.</summary>
    [Parameter] public int OverscanCount { get; set; } = 40;

    /// <summary>Rows kept rendered behind the visible area (opposite to the direction of travel).</summary>
    [Parameter] public int OverscanBehind { get; set; } = 15;

    /// <summary>A new window is fetched when the visible rows come within this many rows of the rendered window's edge.</summary>
    [Parameter] public int FetchMargin { get; set; } = 10;

    /// <summary>
    /// Guess of how many rows fit in the viewport, used to fetch the first window during the initial render instead of
    /// waiting a round trip for the browser to report its size. A wrong guess only costs one extra fetch.
    /// </summary>
    [Parameter] public int InitialVisibleRows { get; set; } = 30;

    /// <summary>
    /// Browsers cap element heights (Chrome ≈16.7M px, Firefox ≈17.9M px). When rows × RowHeight exceeds this the
    /// scrollbar is scaled: the thumb maps proportionally onto the row range while wheel/keyboard scrolling stays row-accurate.
    /// </summary>
    [Parameter] public double MaxScrollHeight { get; set; } = 15_000_000;

    /// <summary>Stable identity for a row, used as its render key. Defaults to the item reference.</summary>
    [Parameter] public Func<TItem, object>? RowKey { get; set; }

    [Parameter] public EventCallback<TItem> OnRowClick { get; set; }
    [Parameter] public string? CssClass { get; set; }
    [Parameter] public string? Title { get; set; }

    internal readonly List<GridColumnBase<TItem>> Columns = [];

    private readonly List<SortDescriptor> _sorts = [];
    private readonly Dictionary<string, ColumnFilter> _filters = [];
    private string? _quickSearch;
    private string _quickSearchInput = string.Empty;
    private CancellationTokenSource? _searchDebounce;

    private ElementReference _viewportElement;
    private IJSObjectReference? _module;
    private IJSObjectReference? _scroller;
    private DotNetObjectReference<ViewportBridge>? _bridge;

    // Viewport geometry as last reported by the browser.
    private double _scrollTop;
    private double _clientHeight;
    private bool _viewportKnown;

    // Anchor = the row at the top of the viewport and how far it is scrolled above the viewport edge.
    private int _anchorIndex;
    private double _anchorOffset;

    // The rendered window.
    private GridRow<TItem>[] _rows = [];
    private int _windowStart;
    private double _topSpacer;
    private double _bottomSpacer;
    private string? _windowStateKey;
    private int _scrollDirection;      // +1 down, -1 up, 0 unknown
    private string? _scrollSync;       // "top:seq" — applied by the JS shim in the same frame as the render that carries it
    private int _scrollSyncSeq;
    private int _fetchCount;
    private bool _loading;
    private bool _reloadQueued;
    private bool _forceReload;
    private CancellationTokenSource? _loadCts;

    private int _totalCount = -1;
    private int _unfilteredCount;
    private int _pendingLoads;
    private string? _loadError;
    private long _lastLoadMs;

    private int _selectedRow = -1;
    private string? _selectedColumn;

    private GridColumnBase<TItem>? _menuColumn;
    private bool _columnChooserOpen;

    private bool _hasRendered;

    private IEnumerable<GridColumnBase<TItem>> VisibleColumns => Columns.Where(c => c.IsVisible);
    private int VisibleColumnCount => Columns.Count(c => c.IsVisible);
    private double TableWidth => VisibleColumns.Sum(c => c.WidthPx);
    private bool HasActiveFilters => _filters.Count > 0 || _quickSearch is not null;
    private bool AnyMenuOpen => _menuColumn is not null || _columnChooserOpen;

    // ---- column registration --------------------------------------------------------------------

    internal void AddColumn(GridColumnBase<TItem> column)
    {
        if (Columns.Contains(column)) return;
        Columns.Add(column);
        if (_hasRendered) StateHasChanged();
    }

    internal void RemoveColumn(GridColumnBase<TItem> column)
    {
        if (Columns.Remove(column) && _hasRendered) StateHasChanged();
    }

    // ---- lifecycle ------------------------------------------------------------------------------

    /// <summary>Called by <see cref="Defer"/> during the first render, once every column has registered.</summary>
    private void OnColumnsReady()
    {
        if (_viewportKnown || Columns.Count == 0) return;
        // Provisional viewport: rows land in the first render batches, before the browser has even loaded the shim.
        _clientHeight = InitialVisibleRows * (double)RowHeight;
        _viewportKnown = true;
        _ = RequestWindowAsync(force: true);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        _hasRendered = true;
        if (!firstRender) return;
        _bridge = DotNetObjectReference.Create(new ViewportBridge(OnViewportChangedAsync, OnColumnResized));
        _module = await JS.InvokeAsync<IJSObjectReference>("import", "./Components/Grid/ServerGrid.razor.js");
        _scroller = await _module.InvokeAsync<IJSObjectReference>("attach", _viewportElement, _bridge);
    }

    public async ValueTask DisposeAsync()
    {
        _searchDebounce?.Cancel();
        _searchDebounce?.Dispose();
        _loadCts?.Cancel();
        try
        {
            if (_scroller is not null) await _scroller.InvokeVoidAsync("dispose");
            if (_scroller is not null) await _scroller.DisposeAsync();
            if (_module is not null) await _module.DisposeAsync();
        }
        catch (JSDisconnectedException) { }
        catch (ObjectDisposedException) { }
        _bridge?.Dispose();
    }

    // ---- virtual scrolling ----------------------------------------------------------------------
    //
    // The browser owns scrolling. The server renders a window of rows (visible + overscan) and only replaces it
    // when the visible area gets within FetchMargin rows of the window's edge, so ordinary wheel scrolling is
    // handled natively with no round trip at all. Above the browser height cap (IsScaled) the scrollbar is
    // proportional; the row at the top of the viewport (the "anchor") is tracked from wheel deltas and the
    // scrollbar is re-synced to it only when a window is replaced.

    private double TotalHeight => Math.Max(0, _totalCount) * (double)RowHeight;
    private double VirtualHeight => Math.Min(TotalHeight, MaxScrollHeight);
    private bool IsScaled => TotalHeight > MaxScrollHeight;
    private int VisibleRowCount => (int)Math.Ceiling(Math.Max(_clientHeight, RowHeight) / RowHeight) + 1;
    private int MaxAnchor => Math.Max(0, _totalCount - VisibleRowCount + 1);
    private double MaxScrollTop => Math.Max(0, VirtualHeight - _clientHeight);

    private Task OnViewportChangedAsync(double scrollTop, double clientHeight, double delta, bool relative)
    {
        var heightChanged = Math.Abs(clientHeight - _clientHeight) > 0.5;
        var previousAnchor = _anchorIndex;
        _scrollTop = scrollTop;
        _clientHeight = clientHeight;
        _viewportKnown = true;

        if (_totalCount >= 0)
        {
            if (!IsScaled)
            {
                _anchorIndex = (int)Math.Floor(scrollTop / RowHeight);
                _anchorOffset = scrollTop - _anchorIndex * (double)RowHeight;
            }
            else if (relative && !heightChanged)
            {
                // Wheel / keyboard: move the anchor by exactly the pixels the user scrolled, so rows glide 1:1.
                var pixels = _anchorOffset + delta;
                var rowsMoved = (int)Math.Floor(pixels / RowHeight);
                _anchorIndex = Math.Clamp(_anchorIndex + rowsMoved, 0, MaxAnchor);
                _anchorOffset = _anchorIndex is 0 && pixels < 0 ? 0 : pixels - rowsMoved * (double)RowHeight;
            }
            else
            {
                // Scrollbar drag: the thumb position maps proportionally onto the whole row range.
                var fraction = MaxScrollTop <= 0 ? 0 : Math.Clamp(scrollTop / MaxScrollTop, 0, 1);
                _anchorIndex = (int)Math.Round(fraction * MaxAnchor);
                _anchorOffset = 0;
            }
        }

        if (_anchorIndex != previousAnchor) _scrollDirection = _anchorIndex > previousAnchor ? 1 : -1;
        else if (delta != 0) _scrollDirection = delta > 0 ? 1 : -1;

        return RequestWindowAsync(force: false);
    }

    /// <summary>Loads the row window for the current anchor. Calls are coalesced: while a load is running, the newest request wins.</summary>
    private async Task RequestWindowAsync(bool force)
    {
        _forceReload |= force;
        if (_loading)
        {
            _reloadQueued = true;
            return;
        }
        _loading = true;
        try
        {
            do
            {
                _reloadQueued = false;
                var forceThis = _forceReload;
                _forceReload = false;
                await LoadWindowAsync(forceThis);
            }
            while (_reloadQueued);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>True when the visible rows are outside, or about to leave, the rendered window.</summary>
    private bool NeedsNewWindow()
    {
        if (_rows.Length == 0) return _totalCount != 0;
        var windowEnd = _windowStart + _rows.Length - 1;
        var visibleEnd = _anchorIndex + VisibleRowCount - 1;
        if (_anchorIndex < _windowStart || visibleEnd > windowEnd) return true;
        if (_windowStart > 0 && _anchorIndex - _windowStart < FetchMargin) return true;
        if (windowEnd < _totalCount - 1 && windowEnd - visibleEnd < FetchMargin) return true;
        return false;
    }

    private async Task LoadWindowAsync(bool force)
    {
        if (!_viewportKnown) return;

        var request = BuildRequest();
        var stateChanged = request.CacheKey != _windowStateKey;
        if (!force && !stateChanged && !NeedsNewWindow())
            return; // still inside the rendered runway: the browser scrolls natively, nothing to do

        // Bias the window towards the direction of travel.
        var above = _scrollDirection < 0 ? OverscanCount : OverscanBehind;
        var below = _scrollDirection < 0 ? OverscanBehind : OverscanCount;
        var start = Math.Max(0, _anchorIndex - above);
        var count = above + VisibleRowCount + below;

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        var cts = _loadCts = new CancellationTokenSource();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        _pendingLoads++;
        _fetchCount++;
        try
        {
            var result = await DataSource.GetRowsAsync(request, start, count, cts.Token);
            if (cts.IsCancellationRequested) return;

            _totalCount = result.TotalCount;
            _unfilteredCount = result.UnfilteredCount;
            _loadError = null;
            _lastLoadMs = watch.ElapsedMilliseconds;

            if (_anchorIndex > MaxAnchor)
            {
                // The view shrank (e.g. a filter was applied) and the anchor fell off the end; pull it back and refetch.
                _anchorIndex = MaxAnchor;
                _anchorOffset = 0;
                _windowStateKey = null;
                _reloadQueued = true;
                return;
            }

            var rows = new GridRow<TItem>[result.Items.Count];
            for (var i = 0; i < rows.Length; i++) rows[i] = new GridRow<TItem>(start + i, result.Items[i]);
            _rows = rows;
            _windowStart = start;
            _windowStateKey = request.CacheKey;
            PositionWindow();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _loadError = ex.Message;
            _rows = [];
            _totalCount = 0;
            _windowStateKey = null;
        }
        finally
        {
            _pendingLoads--;
            StateHasChanged();
        }
    }

    /// <summary>
    /// Computes the spacer heights around the rendered rows. In scaled mode the window is placed so the anchor row
    /// stays exactly where the user sees it, and the scrollbar is re-synced to the anchor's proportional position in
    /// the same frame (see <see cref="RequestScrollSync"/>), so replacing a window never causes a visible jump.
    /// </summary>
    private void PositionWindow()
    {
        var rowsHeight = _rows.Length * (double)RowHeight;
        if (!IsScaled)
        {
            _topSpacer = _windowStart * (double)RowHeight;
            _bottomSpacer = Math.Max(0, TotalHeight - _topSpacer - rowsHeight);
            return;
        }

        var target = MaxAnchor <= 0 ? 0 : Math.Clamp((double)_anchorIndex / MaxAnchor * MaxScrollTop + _anchorOffset, 0, MaxScrollTop);
        var overscanBefore = _anchorIndex - _windowStart;
        _topSpacer = Math.Max(0, target - _anchorOffset - overscanBefore * (double)RowHeight);
        _topSpacer = Math.Min(_topSpacer, Math.Max(0, VirtualHeight - rowsHeight));
        _bottomSpacer = Math.Max(0, VirtualHeight - _topSpacer - rowsHeight);
        RequestScrollSync(target);
    }

    /// <summary>
    /// Asks the browser to set scrollTop. The value travels as a data attribute in the next render batch; the JS shim
    /// applies it from a MutationObserver, i.e. after the whole batch (new spacers and rows) is in the DOM and before
    /// the next paint, so scroll position and content always change together.
    /// </summary>
    private void RequestScrollSync(double top)
    {
        _scrollTop = top;
        _scrollSync = FormattableString.Invariant($"{top:0.##}:{++_scrollSyncSeq}");
    }

    /// <summary>Scrolls so that <paramref name="rowIndex"/> is visible (public so hosts can implement "go to row").</summary>
    public async Task ScrollToRowAsync(int rowIndex)
    {
        if (_totalCount <= 0) return;
        rowIndex = Math.Clamp(rowIndex, 0, _totalCount - 1);
        var visible = Math.Max(1, (int)Math.Floor(_clientHeight / RowHeight));
        var lastVisible = _anchorIndex + visible - 1 - (_anchorOffset > 0 ? 1 : 0);

        if (rowIndex < _anchorIndex) { _anchorIndex = rowIndex; _anchorOffset = 0; _scrollDirection = -1; }
        else if (rowIndex > lastVisible) { _anchorIndex = Math.Min(MaxAnchor, rowIndex - visible + 1); _anchorOffset = Math.Max(0, visible * RowHeight - _clientHeight); _scrollDirection = 1; }
        else return;

        await RequestWindowAsync(force: false);
        // Position of the anchor row inside the rendered table, valid in both scaled and unscaled mode.
        RequestScrollSync(_topSpacer + (_anchorIndex - _windowStart) * (double)RowHeight + _anchorOffset);
        StateHasChanged();
    }

    // ---- data -----------------------------------------------------------------------------------

    internal GridRequest<TItem> BuildRequest() => new()
    {
        Columns = Columns.Select(c => c.Definition).ToList(),
        Filters = _filters.Values.Select(f => f.Clone()).ToList(),
        Sorts = _sorts.ToList(),
        QuickSearch = _quickSearch,
    };

    /// <summary>Re-queries the data source after a filter/sort/search change. Filters scroll back to the top; sorts keep the scroll position.</summary>
    public Task RefreshAsync(bool scrollToTop = false)
    {
        _selectedRow = -1;
        if (scrollToTop)
        {
            _anchorIndex = 0;
            _anchorOffset = 0;
            _scrollDirection = 0;
            RequestScrollSync(0);
        }
        StateHasChanged();
        return RequestWindowAsync(force: true);
    }

    internal ValueTask<GridDistinctValuesResult> LoadDistinctValuesAsync(string columnKey, string? search, CancellationToken cancellationToken) =>
        DataSource.GetDistinctValuesAsync(BuildRequest(), columnKey, search, MaxFilterValues, cancellationToken);

    // ---- sorting --------------------------------------------------------------------------------

    private Task OnHeaderClickAsync(GridColumnBase<TItem> column, MouseEventArgs e)
    {
        if (!column.Sortable) return Task.CompletedTask;
        var key = column.EffectiveKey;
        var index = _sorts.FindIndex(s => s.ColumnKey == key);

        if (e.ShiftKey)
        {
            // Multi-column sort: asc -> desc -> removed, without touching other sort keys.
            if (index < 0) _sorts.Add(new SortDescriptor(key, false));
            else if (!_sorts[index].Descending) _sorts[index] = new SortDescriptor(key, true);
            else _sorts.RemoveAt(index);
        }
        else if (index >= 0 && _sorts.Count == 1)
        {
            if (!_sorts[0].Descending) _sorts[0] = new SortDescriptor(key, true);
            else _sorts.Clear();
        }
        else
        {
            _sorts.Clear();
            _sorts.Add(new SortDescriptor(key, false));
        }
        return RefreshAsync();
    }

    internal Task SortByAsync(GridColumnBase<TItem> column, bool descending)
    {
        _sorts.Clear();
        _sorts.Add(new SortDescriptor(column.EffectiveKey, descending));
        CloseMenus();
        return RefreshAsync();
    }

    private Task ClearSortAsync()
    {
        _sorts.Clear();
        return RefreshAsync();
    }

    private int SortIndexOf(GridColumnBase<TItem> column) => _sorts.FindIndex(s => s.ColumnKey == column.EffectiveKey);

    // ---- filtering ------------------------------------------------------------------------------

    internal ColumnFilter? GetFilter(string columnKey) => _filters.GetValueOrDefault(columnKey);
    private bool IsFiltered(GridColumnBase<TItem> column) => _filters.ContainsKey(column.EffectiveKey);

    internal Task ApplyFilterAsync(string columnKey, ColumnFilter? filter)
    {
        if (filter is null || !filter.IsActive) _filters.Remove(columnKey);
        else _filters[columnKey] = filter;
        CloseMenus();
        return RefreshAsync(scrollToTop: true);
    }

    private Task ClearFilterAsync(string columnKey)
    {
        _filters.Remove(columnKey);
        return RefreshAsync(scrollToTop: true);
    }

    private Task ClearAllFiltersAsync()
    {
        _filters.Clear();
        _quickSearch = null;
        _quickSearchInput = string.Empty;
        return RefreshAsync(scrollToTop: true);
    }

    private async Task OnQuickSearchInput(ChangeEventArgs e)
    {
        _quickSearchInput = e.Value?.ToString() ?? string.Empty;
        _searchDebounce?.Cancel();
        _searchDebounce?.Dispose();
        var cts = _searchDebounce = new CancellationTokenSource();
        try
        {
            await Task.Delay(300, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        var term = string.IsNullOrWhiteSpace(_quickSearchInput) ? null : _quickSearchInput.Trim();
        if (term == _quickSearch) return;
        _quickSearch = term;
        await RefreshAsync(scrollToTop: true);
    }

    private string DescribeFilter(ColumnFilter filter)
    {
        var column = Columns.FirstOrDefault(c => c.EffectiveKey == filter.ColumnKey);
        var format = column?.Definition.Format ?? (v => Convert.ToString(v, CultureInfo.CurrentCulture) ?? "");
        var parts = new List<string>();
        if (filter.Operator is { } op)
        {
            var text = GridExpressionBuilder.Describe(op);
            if (GridExpressionBuilder.NeedsOneOperand(op)) text += $" \"{format(filter.Value)}\"";
            if (GridExpressionBuilder.NeedsTwoOperands(op)) text += $" and \"{format(filter.Value2)}\"";
            parts.Add(text);
        }
        if (filter.ValueSetMode == ValueSetMode.Include)
            parts.Add(filter.Values.Count switch
            {
                1 => $"is {FormatSetValue(filter.Values.First(), format)}",
                var n when n <= 3 => "is " + string.Join(" or ", filter.Values.Select(v => FormatSetValue(v, format))),
                var n => $"{n} values selected",
            });
        else if (filter.ValueSetMode == ValueSetMode.Exclude)
            parts.Add(filter.Values.Count == 1
                ? $"is not {FormatSetValue(filter.Values.First(), format)}"
                : $"{filter.Values.Count} values excluded");
        return string.Join(", ", parts);
    }

    private static string FormatSetValue(object? value, Func<object?, string> format) =>
        value is null || (value is string s && s.Length == 0) ? "(Blanks)" : $"\"{format(value)}\"";

    // ---- menus ----------------------------------------------------------------------------------

    private void ToggleFilterMenu(GridColumnBase<TItem> column)
    {
        _columnChooserOpen = false;
        _menuColumn = ReferenceEquals(_menuColumn, column) ? null : column;
    }

    private void ToggleColumnChooser()
    {
        _menuColumn = null;
        _columnChooserOpen = !_columnChooserOpen;
    }

    internal void CloseMenus()
    {
        _menuColumn = null;
        _columnChooserOpen = false;
    }

    private void ToggleColumnVisible(GridColumnBase<TItem> column)
    {
        if (column.IsVisible && VisibleColumnCount == 1) return;
        column.IsVisible = !column.IsVisible;
    }

    internal void HideColumn(GridColumnBase<TItem> column)
    {
        if (VisibleColumnCount > 1) column.IsVisible = false;
        CloseMenus();
    }

    private void ShowAllColumns()
    {
        foreach (var c in Columns) c.IsVisible = true;
    }

    /// <summary>Menus on the last couple of columns open to the left so they are not clipped by the viewport.</summary>
    private bool IsNearRightEdge(GridColumnBase<TItem> column)
    {
        var visible = VisibleColumns.ToList();
        var index = visible.IndexOf(column);
        if (index < 0) return false;
        var remaining = visible.Skip(index + 1).Sum(c => c.WidthPx);
        return remaining < 200;
    }

    // ---- column resize (drag tracked in the JS shim; only the final width reaches the server) ----

    private void OnColumnResized(string columnKey, double width)
    {
        var column = Columns.FirstOrDefault(c => c.EffectiveKey == columnKey);
        if (column is null) return;
        column.WidthPx = Math.Clamp(width, 40, 2000);
        StateHasChanged();
    }

    // ---- selection & keyboard -------------------------------------------------------------------

    private Task SelectCellAsync(GridRow<TItem> row, GridColumnBase<TItem> column)
    {
        _selectedRow = row.Index;
        _selectedColumn = column.EffectiveKey;
        return OnRowClick.HasDelegate ? OnRowClick.InvokeAsync(row.Item) : Task.CompletedTask;
    }

    private Task OnKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Escape")
        {
            CloseMenus();
            return Task.CompletedTask;
        }
        if (_totalCount <= 0) return Task.CompletedTask;
        if (_selectedRow < 0)
        {
            _selectedRow = Math.Clamp(_anchorIndex, 0, _totalCount - 1);
            _selectedColumn ??= VisibleColumns.FirstOrDefault()?.EffectiveKey;
            return Task.CompletedTask;
        }

        var visible = VisibleColumns.ToList();
        var colIndex = visible.FindIndex(c => c.EffectiveKey == _selectedColumn);
        switch (e.Key)
        {
            case "ArrowDown": _selectedRow = Math.Min(_totalCount - 1, _selectedRow + 1); break;
            case "ArrowUp": _selectedRow = Math.Max(0, _selectedRow - 1); break;
            case "ArrowRight": if (colIndex < visible.Count - 1) _selectedColumn = visible[colIndex + 1].EffectiveKey; break;
            case "ArrowLeft": if (colIndex > 0) _selectedColumn = visible[colIndex - 1].EffectiveKey; break;
            case "Home": if (e.CtrlKey) _selectedRow = 0; else if (visible.Count > 0) _selectedColumn = visible[0].EffectiveKey; break;
            case "End": if (e.CtrlKey) _selectedRow = _totalCount - 1; else if (visible.Count > 0) _selectedColumn = visible[^1].EffectiveKey; break;
            case "PageDown": _selectedRow = Math.Min(_totalCount - 1, _selectedRow + Math.Max(1, VisibleRowCount - 2)); break;
            case "PageUp": _selectedRow = Math.Max(0, _selectedRow - Math.Max(1, VisibleRowCount - 2)); break;
            default: return Task.CompletedTask;
        }
        return ScrollToRowAsync(_selectedRow);
    }

    private bool IsSelected(GridRow<TItem> row, GridColumnBase<TItem> column) =>
        row.Index == _selectedRow && column.EffectiveKey == _selectedColumn;

    // ---- helpers --------------------------------------------------------------------------------

    private object RowKeyFor(GridRow<TItem> row) => RowKey is not null ? RowKey(row.Item) : row.Item!;

    private static string Px(double value) => value.ToString("0.##", CultureInfo.InvariantCulture) + "px";

    private string DiagnosticsText =>
        $"anchor {_anchorIndex:N0}+{_anchorOffset:0}px · window {_windowStart:N0}–{(_windowStart + _rows.Length - 1):N0} ({_rows.Length}) · dir {_scrollDirection} · {(IsScaled ? "scaled" : "1:1")} · scrollTop {_scrollTop:N0}";

    private string StatusText
    {
        get
        {
            if (_loadError is not null) return "Error: " + _loadError;
            if (_totalCount < 0) return "Loading…";
            var rows = _totalCount.ToString("N0", CultureInfo.CurrentCulture);
            if (HasActiveFilters && _totalCount != _unfilteredCount)
                return $"{rows} of {_unfilteredCount.ToString("N0", CultureInfo.CurrentCulture)} rows (filtered)";
            return $"{rows} rows";
        }
    }
}
