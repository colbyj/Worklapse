using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Worklapse.Core.Platform;

namespace Worklapse.Capture.Windows;

/// <summary>
/// Reads which site a browser window shows, from its address bar through UI Automation: Chrome, Edge and
/// Firefox; when the address bar doesn't show an address (it has the focus, or Firefox shows search terms),
/// from the page itself. Private windows (Incognito, InPrivate, Private Browsing) give no site.
/// <para>
/// UI Automation calls go into the browser's process and can be slow, or hang while it does. They run on a
/// thread of their own; a capture waits at most <see cref="Timeout"/> and records no site if the answer isn't
/// there by then, or if an earlier read is still stuck.
/// </para>
/// <para>
/// Private windows are recognised by English labels ("Private Browsing" in Firefox's title, "[InPrivate]" in
/// Edge's, "(Incognito)" / "(InPrivate)" in Chrome's and Edge's top view); a browser in another language would
/// need its own words added.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class BrowserSiteReader : IDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(300);

    /// <summary>Browser windows whose address bar is remembered; more than this and the least recently used goes.</summary>
    private const int CachedWindows = 16;

    private readonly Thread _thread;
    private readonly AutoResetEvent _work = new(false);
    private readonly AutoResetEvent _done = new(false);
    private readonly Lock _gate = new();
    private readonly Dictionary<nint, Window> _windows = []; // reader thread only
    private (long Id, nint Hwnd, Browser Browser)? _pending;  // under _gate
    private (long Id, string? Site) _result;                  // under _gate
    private long _lastId;                                     // under _gate
    private bool _reading;                                    // under _gate
    private volatile bool _disposed;

    public BrowserSiteReader()
    {
        // UI Automation clients belong on an MTA thread.
        _thread = new Thread(Run) { Name = "Worklapse browser site reader", IsBackground = true };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    private enum Browser
    {
        Chromium, // Chrome, Edge
        Firefox,
    }

    /// <summary>The site shown in a browser's window, or null (not a browser, no site, private, or too slow).</summary>
    public string? ReadSite(nint hwnd, string processName, string title)
    {
        Browser? browser = processName.ToLowerInvariant() switch
        {
            "chrome.exe" or "msedge.exe" => Browser.Chromium,
            "firefox.exe" => Browser.Firefox,
            _ => null,
        };
        if (browser is null || IsPrivateByTitle(title))
            return null;

        long id;
        lock (_gate)
        {
            // A read still running (the browser hanging) means no site now rather than a wait.
            if (_reading || _disposed)
                return null;
            id = ++_lastId;
            _pending = (id, hwnd, browser.Value);
        }
        _work.Set();

        var deadline = Environment.TickCount64 + (long)Timeout.TotalMilliseconds;
        for (var left = Timeout.TotalMilliseconds; left > 0; left = deadline - Environment.TickCount64)
        {
            if (!_done.WaitOne(TimeSpan.FromMilliseconds(left)))
                break;
            lock (_gate)
            {
                if (_result.Id == id)
                    return _result.Site;
            }
            // That was an earlier read finishing late; keep waiting for this one.
        }

        lock (_gate)
        {
            if (_pending?.Id == id)
                _pending = null; // not started yet: no need to any more
        }
        return null;
    }

    /// <summary>
    /// Stops the reader. Its thread is a background thread, so a read stuck in a hung browser doesn't keep
    /// the app from exiting.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        _work.Set();
    }

    private static bool IsPrivateByTitle(string title) =>
        title.EndsWith("Private Browsing", StringComparison.Ordinal) // Firefox: "Page — Mozilla Firefox Private Browsing"
        || title.Contains("[InPrivate]", StringComparison.Ordinal);  // Edge: "Page - [InPrivate] - Microsoft Edge"

    private void Run()
    {
        IUIAutomation automation;
        IUIAutomationCondition chromiumAddressBar, firefoxAddressBar, chromiumRootView, visibleDocument;
        try
        {
            automation = (IUIAutomation)new CUIAutomation();
            chromiumAddressBar = automation.CreatePropertyCondition(Uia.ClassNamePropertyId, "OmniboxViewViews");
            firefoxAddressBar = automation.CreatePropertyCondition(Uia.AutomationIdPropertyId, "urlbar-input");
            chromiumRootView = automation.CreatePropertyCondition(Uia.ClassNamePropertyId, "BrowserRootView");
            visibleDocument = automation.CreateAndCondition(
                automation.CreatePropertyCondition(Uia.ControlTypePropertyId, Uia.DocumentControlTypeId),
                automation.CreatePropertyCondition(Uia.IsOffscreenPropertyId, false));
        }
        catch (COMException)
        {
            _disposed = true; // UI Automation unavailable: captures simply get no site
            return;
        }

        while (true)
        {
            _work.WaitOne();
            if (_disposed)
                return;

            (long Id, nint Hwnd, Browser Browser) job;
            lock (_gate)
            {
                if (_pending is not { } pending)
                    continue;
                job = pending;
                _pending = null;
                _reading = true;
            }

            string? site = null;
            try
            {
                site = Read(job.Hwnd, job.Browser);
            }
            catch (Exception)
            {
                // The answers come from another program's accessibility code. Whatever it does wrong means no
                // site for this capture, not a reader thread (and with it the recorder) brought down.
                Forget(job.Hwnd);
            }
            finally
            {
                lock (_gate)
                {
                    _reading = false;
                    _result = (job.Id, site);
                }
                _done.Set();
            }
        }

        string? Read(nint hwnd, Browser browser)
        {
            // A remembered element can belong to a window that has closed (and whose handle may be reused): on
            // a failure, forget the window and look once more from scratch.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var window = WindowFor(hwnd, browser);
                    if (window is null || window.IsPrivate)
                        return null;

                    // The address bar is quick to read (its element is remembered), but it doesn't always show
                    // the address: while it has the focus it may hold a search being typed, and Firefox shows
                    // the search terms instead on a search engine's results page. Then the page itself says.
                    if (window.AddressBar.GetCurrentPropertyValue(Uia.HasKeyboardFocusPropertyId) is not true
                        && SiteDomain.FromAddressBar(window.AddressBar.GetCurrentPropertyValue(Uia.ValueValuePropertyId) as string) is { } site)
                        return site;
                    return PageSite(hwnd);
                }
                catch (COMException)
                {
                    Forget(hwnd);
                }
            }
            return null;
        }

        Window? WindowFor(nint hwnd, Browser browser)
        {
            if (_windows.TryGetValue(hwnd, out var known) && known.Browser == browser)
            {
                known.LastUsed = Environment.TickCount64;
                return known;
            }
            Forget(hwnd);

            var root = automation.ElementFromHandle(hwnd);
            try
            {
                var addressBar = root.FindFirst(Uia.TreeScopeDescendants, browser == Browser.Firefox ? firefoxAddressBar : chromiumAddressBar);
                if (addressBar is null)
                    return null; // a window without one (a popup), or Firefox still building its tree: try again next time

                // Chrome's window title doesn't say Incognito, but its top view's name does.
                var isPrivate = false;
                if (browser == Browser.Chromium && root.FindFirst(Uia.TreeScopeDescendants, chromiumRootView) is { } view)
                {
                    var name = view.GetCurrentPropertyValue(Uia.NamePropertyId) as string ?? "";
                    isPrivate = name.EndsWith("(Incognito)", StringComparison.Ordinal) || name.EndsWith("(InPrivate)", StringComparison.Ordinal);
                    Marshal.ReleaseComObject(view);
                }

                if (_windows.Count >= CachedWindows)
                    Forget(_windows.MinBy(w => w.Value.LastUsed).Key);
                return _windows[hwnd] = new Window(browser, addressBar, isPrivate);
            }
            finally
            {
                Marshal.ReleaseComObject(root);
            }
        }

        // The address of the page on screen, which the browser gives its document element as its value (a
        // tab's preloaded or background pages are off screen). Chrome and Edge don't expose the page while
        // minimized, but they show the address in the address bar anyway.
        string? PageSite(nint hwnd)
        {
            var root = automation.ElementFromHandle(hwnd);
            try
            {
                if (root.FindFirst(Uia.TreeScopeDescendants, visibleDocument) is not { } document)
                    return null;
                try
                {
                    return SiteDomain.FromAddressBar(document.GetCurrentPropertyValue(Uia.ValueValuePropertyId) as string);
                }
                finally
                {
                    Marshal.ReleaseComObject(document);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(root);
            }
        }

        void Forget(nint hwnd)
        {
            if (_windows.Remove(hwnd, out var window))
                Marshal.ReleaseComObject(window.AddressBar);
        }
    }

    private sealed class Window(Browser browser, IUIAutomationElement addressBar, bool isPrivate)
    {
        public Browser Browser { get; } = browser;
        public IUIAutomationElement AddressBar { get; } = addressBar;
        public bool IsPrivate { get; } = isPrivate;
        public long LastUsed { get; set; } = Environment.TickCount64;
    }
}
