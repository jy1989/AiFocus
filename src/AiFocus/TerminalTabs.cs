using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows.Automation;

namespace AiFocus;

/// <summary>
/// Reads Windows Terminal tab titles through UI Automation, so Claude Code sessions in background
/// tabs are visible too (the window title only shows the selected tab). Runs on its own thread:
/// UIA calls into another process can take tens of milliseconds and must not stall the panel.
/// </summary>
public sealed class TerminalTabs : IDisposable
{
    public sealed record Tab(int Index, string Title, bool Selected);

    private const string TabViewClass = "Microsoft.UI.Xaml.Controls.TabView";
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1.5);
    private readonly ConcurrentDictionary<IntPtr, IReadOnlyList<Tab>> _tabs = new();
    // The tab elements behind _tabs, same order, so selecting a tab needs no fresh tree walk.
    private readonly ConcurrentDictionary<IntPtr, List<AutomationElement>> _elements = new();
    private readonly CancellationTokenSource _stop = new();
    private volatile IntPtr[] _windows = Array.Empty<IntPtr>();

    public TerminalTabs()
    {
        var thread = new Thread(Loop) { IsBackground = true, Name = "AiFocus.TerminalTabs" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    /// <summary>Sets the Windows Terminal windows to track (called on every refresh).</summary>
    public void Watch(IEnumerable<IntPtr> windows) => _windows = windows.ToArray();

    /// <summary>Latest tab list, or null until the window has been read once.</summary>
    public IReadOnlyList<Tab>? Get(IntPtr hwnd) => _tabs.TryGetValue(hwnd, out var tabs) ? tabs : null;

    /// <summary>
    /// Selects the tab whose (glyph-stripped) title matches, falling back to its last known index.
    /// Fast path: one UIA call on the element the background loop already found; walking the
    /// Terminal's UI tree again is the slow part and only happens if that element went stale.
    /// </summary>
    public bool Select(IntPtr hwnd, string title, int index)
    {
        if (_elements.TryGetValue(hwnd, out var cached) && _tabs.TryGetValue(hwnd, out var tabs))
        {
            var match = tabs.FirstOrDefault(t => WindowsAdapter.ParseClaudeCodeTitle(t.Title).title == title)?.Index ?? index;
            if (match >= 0 && match < cached.Count && TrySelect(cached[match])) return true;
        }
        try
        {
            var items = TabItems(hwnd);
            var target = items.FirstOrDefault(i => WindowsAdapter.ParseClaudeCodeTitle(i.Current.Name).title == title)
                ?? (index >= 0 && index < items.Count ? items[index] : null);
            return target != null && TrySelect(target);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Debug.WriteLine("Select terminal tab: " + ex.Message);
            return false;
        }
    }

    private static bool TrySelect(AutomationElement item)
    {
        try
        {
            if (!item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var p)) return false;
            ((SelectionItemPattern)p).Select();
            return true;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    private void Loop()
    {
        while (!_stop.IsCancellationRequested)
        {
            var windows = _windows;
            foreach (var gone in _tabs.Keys.Where(h => !windows.Contains(h)).ToArray())
            {
                _tabs.TryRemove(gone, out _);
                _elements.TryRemove(gone, out _);
            }
            foreach (var hwnd in windows)
            {
                try
                {
                    var items = TabItems(hwnd);
                    _tabs[hwnd] = items.Select((item, i) => new Tab(i, item.Current.Name, IsSelected(item))).ToList();
                    _elements[hwnd] = items;
                }
                catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException)
                {
                    // Window closing or not ready; keep the previous snapshot.
                    Debug.WriteLine("Read terminal tabs: " + ex.Message);
                }
            }
            _stop.Token.WaitHandle.WaitOne(Interval);
        }
    }

    private static bool IsSelected(AutomationElement item) =>
        item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var p) && ((SelectionItemPattern)p).Current.IsSelected;

    /// <summary>
    /// Walks the raw view: the control-view search misses tabs in some windows (it stops at
    /// Terminal's popup host pane), while the raw tree always contains TabView → ListView → TabItem.
    /// </summary>
    private static List<AutomationElement> TabItems(IntPtr hwnd)
    {
        var result = new List<AutomationElement>();
        var root = AutomationElement.FromHandle(hwnd);
        var tabView = FindByClass(root, TabViewClass, depth: 0);
        if (tabView != null) CollectTabItems(tabView, result, depth: 0);
        return result;
    }

    private static AutomationElement? FindByClass(AutomationElement parent, string className, int depth)
    {
        if (depth > 5) return null;
        var walker = TreeWalker.RawViewWalker;
        for (var child = walker.GetFirstChild(parent); child != null; child = walker.GetNextSibling(child))
        {
            if (child.Current.ClassName == className) return child;
            var found = FindByClass(child, className, depth + 1);
            if (found != null) return found;
        }
        return null;
    }

    private static void CollectTabItems(AutomationElement parent, List<AutomationElement> result, int depth)
    {
        if (depth > 2) return;
        var walker = TreeWalker.RawViewWalker;
        for (var child = walker.GetFirstChild(parent); child != null; child = walker.GetNextSibling(child))
        {
            if (child.Current.ControlType == ControlType.TabItem) result.Add(child);
            else CollectTabItems(child, result, depth + 1);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
    }
}
