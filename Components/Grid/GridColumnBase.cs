using System.Globalization;
using Microsoft.AspNetCore.Components;
using ServerGrid.Grid;

namespace ServerGrid.Components.Grid;

/// <summary>Non-generic-over-value base for grid columns. Handles registration with the parent grid and shared presentation parameters.</summary>
public abstract class GridColumnBase<TItem> : ComponentBase, IDisposable
{
    private bool _registered;
    private bool? _lastVisibleParameter;
    private string? _lastWidthParameter;

    [CascadingParameter] internal ServerGrid<TItem> Grid { get; set; } = default!;

    /// <summary>Header text. Defaults to the property name split into words.</summary>
    [Parameter] public string? Title { get; set; }

    /// <summary>Unique key for this column. Defaults to the property path.</summary>
    [Parameter] public string? Key { get; set; }

    /// <summary>Standard .NET format string applied to the value (e.g. <c>N2</c>, <c>yyyy-MM-dd</c>).</summary>
    [Parameter] public string? Format { get; set; }

    /// <summary>Initial width, in CSS pixels ("140" or "140px").</summary>
    [Parameter] public string Width { get; set; } = "140px";

    [Parameter] public bool Sortable { get; set; } = true;
    [Parameter] public bool Filterable { get; set; } = true;

    /// <summary>Whether the toolbar quick-search box looks in this column (string columns only).</summary>
    [Parameter] public bool QuickSearchable { get; set; } = true;

    [Parameter] public bool Visible { get; set; } = true;

    /// <summary>Pins the column to the left edge during horizontal scrolling.</summary>
    [Parameter] public bool Frozen { get; set; }

    [Parameter] public GridAlign? Align { get; set; }
    [Parameter] public string? CssClass { get; set; }

    /// <summary>Optional custom cell content. Sorting and filtering still use the bound property.</summary>
    [Parameter] public RenderFragment<TItem>? CellTemplate { get; set; }

    public double WidthPx { get; internal set; } = 140;
    public bool IsVisible { get; internal set; } = true;

    public abstract string EffectiveKey { get; }
    public abstract string EffectiveTitle { get; }
    public abstract GridColumnDefinition<TItem> Definition { get; }
    public abstract GridValueKind Kind { get; }
    public abstract bool IsNullable { get; }
    public abstract Type ValueType { get; }
    public abstract string FormatCell(TItem item);

    public GridAlign EffectiveAlign => Align ?? (Kind == GridValueKind.Number ? GridAlign.Right : Kind == GridValueKind.Boolean ? GridAlign.Center : GridAlign.Left);

    protected override void OnInitialized()
    {
        if (Grid is null)
            throw new InvalidOperationException($"{GetType().Name} must be placed inside a ServerGrid.");
    }

    protected override void OnParametersSet()
    {
        if (_lastWidthParameter != Width)
        {
            _lastWidthParameter = Width;
            WidthPx = ParseWidth(Width);
        }
        if (_lastVisibleParameter != Visible)
        {
            _lastVisibleParameter = Visible;
            IsVisible = Visible;
        }
        if (!_registered)
        {
            _registered = true;
            Grid.AddColumn(this);
        }
    }

    private static double ParseWidth(string width)
    {
        var s = width.Trim();
        if (s.EndsWith("px", StringComparison.OrdinalIgnoreCase)) s = s[..^2];
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var px) ? Math.Max(40, px) : 140;
    }

    protected static string Humanize(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var sb = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (i > 0 && char.IsUpper(c) && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
                sb.Append(' ');
            sb.Append(c);
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        if (_registered) Grid?.RemoveColumn(this);
    }
}
