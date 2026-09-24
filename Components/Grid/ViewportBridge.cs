using Microsoft.JSInterop;

namespace ServerGrid.Components.Grid;

/// <summary>Non-generic JS-invokable target that forwards browser-side UI events to the owning grid.</summary>
public sealed class ViewportBridge(Func<double, double, double, bool, Task> onViewportChanged, Action<string, double> onColumnResized)
{
    [JSInvokable]
    public Task OnViewportChanged(double scrollTop, double clientHeight, double delta, bool relative) =>
        onViewportChanged(scrollTop, clientHeight, delta, relative);

    [JSInvokable]
    public void OnColumnResized(string columnKey, double width) => onColumnResized(columnKey, width);
}
