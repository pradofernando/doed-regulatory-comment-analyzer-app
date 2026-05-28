// Lightweight theme manager. Reads/writes localStorage and toggles
// a `data-theme` attribute on <html>. Exposes window.appTheme.
(function () {
    const STORAGE_KEY = 'doed.theme';
    const DEFAULT = 'light';
    const ATTR = 'data-theme';
    const root = document.documentElement;
    let applying = false;

    function get() {
        try {
            return localStorage.getItem(STORAGE_KEY) || DEFAULT;
        } catch {
            return DEFAULT;
        }
    }

    function apply(theme) {
        const t = theme === 'dark' ? 'dark' : 'light';
        if (root.getAttribute(ATTR) === t) return;
        applying = true;
        root.setAttribute(ATTR, t);
        applying = false;
    }

    function set(theme) {
        const t = theme === 'dark' ? 'dark' : 'light';
        try { localStorage.setItem(STORAGE_KEY, t); } catch { }
        apply(t);
        return t;
    }

    function toggle() {
        return set(get() === 'dark' ? 'light' : 'dark');
    }

    // Apply immediately so the very first paint has the correct theme.
    apply(get());

    // Blazor's enhanced navigation morphs the DOM and can strip attributes
    // on <html> that weren't in the server response. Watch for that and
    // restore the theme synchronously before the browser paints.
    new MutationObserver(() => {
        if (applying) return;
        const want = get();
        if (root.getAttribute(ATTR) !== want) {
            apply(want);
        }
    }).observe(root, { attributes: true, attributeFilter: [ATTR] });

    // Safety net for enhanced navigation events.
    document.addEventListener('enhancedload', () => apply(get()));

    window.appTheme = { get, set, toggle, apply };
})();
