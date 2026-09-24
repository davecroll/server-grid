// Minimal scroll plumbing for ServerGrid. No data ever flows through here: the module only reports the
// viewport's scroll position/size to .NET, applies scroll offsets the server asks for, and tracks resize drags.
export function attach(viewport, dotnet) {
    let rafPending = false;
    let lastReported = viewport.scrollTop;
    let relativeUntil = 0;          // wheel/touch/keyboard scrolls are "relative"; scrollbar drags are "absolute"
    let pendingRelative = false;    // classification captured when each scroll event arrives, not when the timer fires
    let suppressTop = null;         // ignore the scroll event caused by our own scroll sync
    let disposed = false;

    const markRelative = () => { relativeUntil = performance.now() + 150; };

    const report = () => {
        rafPending = false;
        if (disposed) return;
        const top = viewport.scrollTop;
        const height = viewport.clientHeight;
        if (suppressTop !== null && Math.abs(top - suppressTop) < 1) {
            suppressTop = null;
            lastReported = top;
            return;
        }
        suppressTop = null;
        const delta = top - lastReported;
        lastReported = top;
        const relative = pendingRelative;
        pendingRelative = false;
        dotnet.invokeMethodAsync('OnViewportChanged', top, height, delta, relative).catch(e => console.error('ServerGrid viewport callback failed', e));
    };

    // Coalesce bursts of scroll events into one report per turn. A timer (not requestAnimationFrame) so it
    // also fires while the tab is in the background.
    const schedule = () => {
        if (performance.now() < relativeUntil) pendingRelative = true;
        if (!rafPending) {
            rafPending = true;
            setTimeout(report, 16);
        }
    };

    viewport.addEventListener('scroll', schedule, { passive: true });
    viewport.addEventListener('wheel', markRelative, { passive: true });
    viewport.addEventListener('touchmove', markRelative, { passive: true });
    const navKeys = new Set(['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'PageUp', 'PageDown', 'Home', 'End', ' ']);
    const onKeyDown = e => {
        markRelative();
        // Let the server drive selection + scrolling for navigation keys instead of the browser's native 40px nudge.
        if (navKeys.has(e.key) && e.target === viewport) e.preventDefault();
    };
    viewport.addEventListener('keydown', onKeyDown);
    const resizeObserver = new ResizeObserver(() => { if (!rafPending) { rafPending = true; setTimeout(report, 16); } });
    resizeObserver.observe(viewport);

    // Column resizing: tracked locally with pointer capture so the drag is smooth, then the final
    // width is reported once. The server re-renders with the same width, so the DOM stays in sync.
    const onPointerDown = e => {
        const handle = e.target.closest('.sg-resizer');
        if (!handle || e.button !== 0) return;
        const th = handle.closest('th');
        const table = th.closest('table');
        const col = table.querySelectorAll('colgroup col')[th.cellIndex];
        const startX = e.clientX;
        const startWidth = th.getBoundingClientRect().width;
        const tableStart = table.getBoundingClientRect().width;
        let width = startWidth;
        e.preventDefault();
        handle.setPointerCapture(e.pointerId);
        handle.classList.add('sg-resizer-active');
        const move = ev => {
            width = Math.min(2000, Math.max(40, startWidth + ev.clientX - startX));
            th.style.width = width + 'px';
            if (col) col.style.width = width + 'px';
            table.style.width = (tableStart + width - startWidth) + 'px';
        };
        const up = () => {
            handle.removeEventListener('pointermove', move);
            handle.removeEventListener('pointerup', up);
            handle.removeEventListener('pointercancel', up);
            handle.classList.remove('sg-resizer-active');
            dotnet.invokeMethodAsync('OnColumnResized', th.dataset.key, width).catch(() => { });
        };
        handle.addEventListener('pointermove', move);
        handle.addEventListener('pointerup', up);
        handle.addEventListener('pointercancel', up);
    };
    viewport.addEventListener('pointerdown', onPointerDown);

    // Server-requested scroll positions arrive as a data attribute inside a render batch. Applying them from a
    // MutationObserver means the new rows/spacers and the new scrollTop land in the same frame: no flicker.
    const applyScrollSync = () => {
        const value = viewport.dataset.scrollSync;
        if (!value) return;
        const top = parseFloat(value.split(':')[0]);
        if (!isFinite(top)) return;
        if (Math.abs(viewport.scrollTop - top) < 0.5) { lastReported = viewport.scrollTop; return; }
        suppressTop = top;
        lastReported = top;
        viewport.scrollTop = top;
    };
    const syncObserver = new MutationObserver(applyScrollSync);
    syncObserver.observe(viewport, { attributes: true, attributeFilter: ['data-scroll-sync'] });
    applyScrollSync();
    report();

    return {
        dispose() {
            disposed = true;
            viewport.removeEventListener('scroll', schedule);
            viewport.removeEventListener('wheel', markRelative);
            viewport.removeEventListener('touchmove', markRelative);
            viewport.removeEventListener('keydown', onKeyDown);
            viewport.removeEventListener('pointerdown', onPointerDown);
            resizeObserver.disconnect();
            syncObserver.disconnect();
        }
    };
}
