# ServerGrid — a server-side data grid for Blazor Interactive Server

A reusable `ServerGrid<TItem>` component that keeps the entire data set on the server and streams only
rendered HTML diffs to the browser over the Blazor circuit. The demo page binds it to **1,000,000**
in-memory trades and stays responsive: filtering, sorting, searching and scrolling all happen server-side.

## What it does

| Feature | Details |
|---|---|
| Infinite scroll | Custom virtualisation: only the rows in the viewport (plus a small overscan) are rendered. No pagination UI. |
| Very large sets | Browsers cap element height at ~16.7M px. Above that the scrollbar is scaled onto the row range while wheel/keyboard scrolling stays row-accurate, so 1M+ rows work. |
| Sorting | Click a header to toggle asc/desc/off. **Shift+click** adds sort levels (numbered indicators). |
| Excel-style filters | Every column header has a menu with: sort shortcuts, a typed condition (contains / begins with / between / greater than / is blank …) **and** a searchable distinct-value checklist with (Select All) and (Blanks). Value lists respect the other columns' filters, exactly like Excel. |
| Quick search | Toolbar box searches all text columns at once (debounced). |
| Filter chips | Active filters/sorts show as removable chips. "Clear filters" / "Clear sort" buttons. |
| Columns | Drag header edges to resize, hide/show via the Columns menu, optional frozen (sticky) columns, custom cell templates, format strings. |
| Selection & keyboard | Click a cell to select; arrows / PageUp / PageDown / Home / End / Ctrl+Home / Ctrl+End move the selection and scroll it into view. |
| Status bar | `n of N rows (filtered)` plus selected row and last fetch time. |

## Run it

```bash
dotnet run
# then open http://localhost:5247
```

Row count is configurable in `appsettings.json` (`Grid:RowCount`, default 1,000,000).

Tests:

```bash
dotnet test tests/ServerGrid.Tests
```

## How the data stays on the server

```
┌───────────── browser ─────────────┐        ┌──────────────── server (circuit) ────────────────┐
│ scroll position / clicks  ───────────────▶ │ ServerGrid<TItem>                                  │
│                                   │        │   builds GridRequest (filters, sorts, search)      │
│ ◀─────────  HTML diff (≈40 rows)  ─────── │   asks IGridDataSource for rows [start, start+n)   │
└───────────────────────────────────┘        │ InMemoryGridDataSource / QueryableGridDataSource   │
                                             │   expression trees → Where / OrderBy / Skip / Take │
                                             └────────────────────────────────────────────────────┘
```

* `Grid/GridExpressionBuilder.cs` turns filter and sort state into **expression trees**, so the same
  logic runs against an in-memory array (compiled) or an `IQueryable` provider such as EF Core
  (translated to SQL). Use `StringMatchMode.Provider` for databases.
* `Grid/InMemoryGridDataSource.cs` materialises the filtered/sorted view once per distinct state and
  caches it, so scrolling is an array slice. Heavy work runs on the thread pool so the circuit stays
  responsive. A 1M-row string sort takes a few hundred ms; scrolling fetches take ~0 ms.
* `Grid/QueryableGridDataSource.cs` pushes everything into `IQueryable` (`Where`, `OrderBy`, `Skip`,
  `Take`, `Distinct`) — plug in EF Core's `ToListAsync`/`CountAsync` via its delegates.

The only JavaScript is `Components/Grid/ServerGrid.razor.js` (~90 lines): it reports the viewport's
scroll position to the server, applies scroll offsets the server requests, and tracks column-resize drags
locally so they are smooth. **No row data ever passes through it.**

## Using the component

```razor
<ServerGrid TItem="Trade" DataSource="_dataSource" Height="70vh" RowHeight="30" RowKey="t => t.Id">
    <GridColumn Property="@(t => t.Id)"        Title="Trade #" Width="90" Frozen="true" />
    <GridColumn Property="@(t => t.TradeDate)" />
    <GridColumn Property="@(t => t.Trader)"    Width="150" />
    <GridColumn Property="@(t => t.Notional)"  Format="N2" />
    <GridColumn Property="@(t => t.Side)">
        <CellTemplate><span class="side">@context.Side</span></CellTemplate>
    </GridColumn>
</ServerGrid>

@code {
    // One data source per circuit: it caches this user's current view.
    private readonly InMemoryGridDataSource<Trade> _dataSource = new(repository.Trades);
}
```

`GridColumn` infers the key, title and value type from the property expression. Sorting, filtering and
the value list all derive from that expression, so custom `CellTemplate`s never break them.

## Project layout

```
Grid/                       Engine: models, expression builder, data sources (no UI dependencies)
Components/Grid/            ServerGrid, GridColumn, ColumnFilterMenu, scoped CSS, scroll shim JS
Data/                       Demo Trade model + deterministic 1M-row generator
Components/Pages/Home.razor Demo page
tests/ServerGrid.Tests      xunit tests for the engine and both data sources
```

## Notes and limits

* Memory: the in-memory source caches one `TItem[]` view per grid instance (8 MB of references for 1M
  rows) — fine for a demo, but for many concurrent users prefer the `IQueryable` source over a database.
* Text sorting uses ordinal, case-insensitive comparison for speed; change `StringSortComparer` if you
  need culture-aware ordering.
* Value lists are capped (`MaxFilterValues`, default 250); the menu tells you to type to narrow the list.
