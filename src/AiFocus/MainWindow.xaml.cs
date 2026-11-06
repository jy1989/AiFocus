using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace AiFocus;

public partial class MainWindow : Window
{
    private const int HotkeyId = 0xA1C0, NextHotkeyId = 0xA1C1, BackHotkeyId = 0xA1C2;
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    private const int DwmwaWindowCornerPreference = 33, DwmwcpRound = 2, DwmwaCloak = 13;
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    private const uint SwpNoSize = 0x1, SwpNoZOrder = 0x4, SwpNoActivate = 0x10;
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativePoint pt, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    private const uint MonitorDefaultToNearest = 2;
    /// <summary>Default panel size in DIPs: wide enough for three tile columns.</summary>
    private const double PanelWidth = 1080, PanelHeight = 680;
    private readonly ObservableCollection<SessionEntry> _sessions = new();
    private readonly SessionStore _store = new();
    private readonly BrowserHub _browser = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly ICollectionView _view;
    private HwndSource? _source;
    private IntPtr _beforeOpen;
    private string _filterMode = "all";
    private readonly HashSet<int> _registeredHotkeys = new();
    private bool _hotkeyRegistered => _registeredHotkeys.Contains(HotkeyId);
    /// <summary>Codex replies older than this are no longer surfaced, even if never opened.</summary>
    private static readonly TimeSpan UnrepliedWindow = TimeSpan.FromHours(8);
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly CodexThreads _codex = new();
    private readonly TerminalTabs _terminalTabs = new();
    private readonly ClaudeSessions _claude = new();
    private string? _codexExe;
    private IntPtr _codexWindow;

    /// <summary>Raised when a watched session stops running while the user is elsewhere.</summary>
    public event Action<SessionEntry>? SessionCompleted;
    /// <summary>Raised once when a running session stops making progress (likely waiting for approval).</summary>
    public event Action<SessionEntry>? SessionStalled;
    public event Action? SettingsChanged;
    /// <summary>Short status message for the tray (e.g. nothing is waiting).</summary>
    public event Action<string>? Notice;
    /// <summary>Raised after every refresh (every few seconds), for the floating widget.</summary>
    public event Action? SessionsRefreshed;
    public IReadOnlyCollection<SessionEntry> Sessions => _sessions;
    public AppSettings Settings => _settings;

    public MainWindow()
    {
        InitializeComponent();
        _view = CollectionViewSource.GetDefaultView(_sessions);
        _view.Filter = Filter;
        ApplyLayout();
        SessionList.ItemsSource = _view;
        SessionList.SizeChanged += (_, _) => UpdateTileWidth();
        UpdateTileWidth();
        SourceInitialized += Window_SourceInitialized;
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _refreshTimer.Tick += (_, _) => RefreshSessionsInBackground();
        UpdateFilterButtons();
    }

    public void InitializeInvisible()
    {
        new WindowInteropHelper(this).EnsureHandle();
        RefreshSessionsInBackground();
        _refreshTimer.Start();
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WndProc);
        // Windows 11 draws rounded corners and the shadow natively; no-op on Windows 10.
        int corner = DwmwcpRound;
        DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref corner, sizeof(int));
        TryRegisterHotkey(HotkeyId, _settings.Hotkey);
        TryRegisterHotkey(NextHotkeyId, _settings.NextHotkey);
        TryRegisterHotkey(BackHotkeyId, _settings.BackHotkey);
        UpdateHotkeyHint();
    }
    /// <summary>Registers (or, for empty text, just releases) one global hotkey.</summary>
    private bool TryRegisterHotkey(int id, string text)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (_registeredHotkeys.Remove(id)) UnregisterHotKey(handle, id);
        if (string.IsNullOrWhiteSpace(text)) return true;
        bool ok = HotkeyText.TryParse(text, out var modifiers, out var vk)
            && RegisterHotKey(handle, id, modifiers | ModNoRepeat, vk);
        if (ok) _registeredHotkeys.Add(id);
        return ok;
    }
    private void ReleaseHotkeys()
    {
        var handle = new WindowInteropHelper(this).Handle;
        foreach (var id in _registeredHotkeys) UnregisterHotKey(handle, id);
        _registeredHotkeys.Clear();
    }
    private void UpdateHotkeyHint()
    {
        HotkeyHint.Text = HotkeyText.Pretty(_settings.Hotkey)
            + (_settings.NextHotkey.Length > 0 ? "    送过去 " + HotkeyText.Pretty(_settings.NextHotkey) : "")
            + (_settings.BackHotkey.Length > 0 ? "    送回来 " + HotkeyText.Pretty(_settings.BackHotkey) : "");
    }
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            // The panel no longer closes when it loses focus: a press brings it back to the front,
            // and only closes it when it is already the active window.
            if (PanelShown && IsActive) HidePanel(restorePrevious: true);
            else ShowPanel();
        }
        else if (msg == WmHotkey && wParam.ToInt32() == NextHotkeyId)
        {
            handled = true;
            _ = JumpToNextWaitingAsync();
        }
        else if (msg == WmHotkey && wParam.ToInt32() == BackHotkeyId)
        {
            handled = true;
            ReturnToOwnWindow();
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// The window the user was working in before AI Focus sent them to an AI. Kept across a chain of
    /// jumps (Alt+W, Alt+W, ...) so "送回来" always lands on their own work, not on the previous AI.
    /// </summary>
    private IntPtr _returnTo;

    private void RememberReturnWindow(IntPtr origin)
    {
        var title = WindowsAdapter.GetTitle(origin);
        var skip = SkipReturnReason(origin, title);
        Log.Write($"return target candidate {origin} \"{title}\": {(skip ?? "kept")}");
        if (skip == null) _returnTo = origin;
    }

    /// <summary>Why a window is not the user's own work, or null if it is.</summary>
    private string? SkipReturnReason(IntPtr origin, string title)
    {
        if (!WindowsAdapter.Exists(origin)) return "no window";
        // Our own panel, tray menu or widget is never "the user's work".
        if (WindowsAdapter.IsOwnWindow(origin)) return "AI Focus window";
        // Coming from another AI session means we are mid-chain: keep the original work window.
        if (origin == _codexWindow) return "Codex window";
        if (_sessions.FirstOrDefault(s => s.WindowHandle == origin) is { } session) return "AI session " + session.DisplayTitle;
        // Web sessions have no window handle: a browser window showing an AI tab is an AI window too.
        if (_sessions.FirstOrDefault(s => s.Kind == "web" && s.OriginalTitle.Length > 0
                && title.StartsWith(s.OriginalTitle, StringComparison.Ordinal)) is { } tab) return "AI tab " + tab.DisplayTitle;
        return null;
    }

    /// <summary>"送回来": back to the user's own window after handling an AI.</summary>
    public void ReturnToOwnWindow()
    {
        if (PanelShown) HidePanel(restorePrevious: false);
        if (!WindowsAdapter.Exists(_returnTo)) { Log.Write("return: nothing to return to"); Notice?.Invoke("没有可以送回去的窗口"); return; }
        Log.Write("return to " + _returnTo);
        if (!WindowsAdapter.Activate(_returnTo)) WindowsAdapter.Flash(_returnTo);
    }

    /// <summary>Jumps to the session that has been waiting for a reply the longest (first in, first out).</summary>
    public async Task JumpToNextWaitingAsync()
    {
        // No refresh first: the background refresh keeps the list ≤ 3 s old, and waiting on a scan made the jump feel slow.
        // Finished replies first (oldest first), then sessions that look blocked on the user.
        var next = _sessions.Where(s => s.Completed).OrderBy(s => s.CompletedUtc)
            .Concat(_sessions.Where(s => s.Stall.Length > 0 && !s.Completed).OrderBy(s => s.StallSinceUtc))
            .FirstOrDefault();
        if (next == null) { Notice?.Invoke("没有等你回复的 AI"); return; }
        await ActivateSessionAsync(next);
        RefreshSessionsInBackground();
    }

    /// <summary>
    /// Whether the panel is on screen. "Hidden" normally means cloaked, not Hide(): the window stays
    /// shown and fully rendered (the timer keeps its content current), so bringing it back is a
    /// single DWM uncloak instead of WPF re-rendering the whole tree. Cloaked windows get no mouse input.
    /// </summary>
    public bool PanelShown { get; private set; }
    private System.Drawing.Rectangle _placedOn;

    public void ShowPanel()
    {
        if (PanelShown)
        {
            Activate();
            SearchBox.Focus();
            return;
        }
        _beforeOpen = WindowsAdapter.GetForegroundWindow();
        var screen = WinForms.Screen.FromPoint(WinForms.Cursor.Position).WorkingArea;
        // Re-placing means resizing, which costs a re-layout; skip it when nothing changed.
        if (screen != _placedOn || !IsVisible || _placedForConsole != _consoleMode) { PlaceOnScreen(screen); _placedOn = screen; _placedForConsole = _consoleMode; }
        bool firstShow = !IsVisible;
        if (firstShow)
        {
            // Cloaked until WPF has painted its first frame, so an empty frame never flashes up.
            SetCloaked(true);
            Show();
        }
        PanelShown = true;
        Activate();
        SearchBox.Focus();
        SearchBox.SelectAll();
        if (!_hotkeyRegistered) FeedbackText.Text = "快捷键已被其他程序占用，请在设置中更换";
        if (firstShow) RevealWhenRendered();
        else SetCloaked(false);
        // Shown with data from the last background refresh (≤ 3 s old); fresh data follows.
        RefreshSessionsInBackground();
        UpdateConsole();
    }

    private void SetCloaked(bool cloaked)
    {
        int value = cloaked ? 1 : 0;
        DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, DwmwaCloak, ref value, sizeof(int));
    }

    private async void RevealWhenRendered()
    {
        // Rendering fires once the frame is composed; one more idle pass lets it reach the screen.
        var rendered = new TaskCompletionSource();
        void OnRendering(object? s, EventArgs e) { CompositionTarget.Rendering -= OnRendering; rendered.TrySetResult(); }
        CompositionTarget.Rendering += OnRendering;
        await Task.WhenAny(rendered.Task, Task.Delay(150));
        await Dispatcher.Yield(DispatcherPriority.Render);
        if (PanelShown) SetCloaked(false);
    }
    /// <summary>
    /// Sizes and centres the panel on the given work area entirely in physical pixels, using that
    /// monitor's own DPI. WPF's Left/Top/Width/Height go through the DPI of whichever monitor the
    /// window was on before, which misplaces it when monitors use different scaling.
    /// </summary>
    private void PlaceOnScreen(System.Drawing.Rectangle screen)
    {
        var handle = new WindowInteropHelper(this).Handle;
        // Step onto the target monitor first so any DPI change (and WPF's resize for it) happens now.
        SetWindowPos(handle, IntPtr.Zero, screen.Left + screen.Width / 2, screen.Top + screen.Height / 2, 0, 0,
            SwpNoSize | SwpNoZOrder | SwpNoActivate);
        var monitor = MonitorFromPoint(new NativePoint { X = screen.Left + screen.Width / 2, Y = screen.Top + screen.Height / 2 }, MonitorDefaultToNearest);
        double scale = GetDpiForMonitor(monitor, 0, out uint dpiX, out _) == 0 ? dpiX / 96.0 : VisualTreeHelper.GetDpi(this).DpiScaleX;
        // Recomputed on every show (never shrink-only), so moving between screens cannot leave it narrow.
        if (_consoleMode)
        {
            // Console: fill the work area (taskbar stays visible).
            SetWindowPos(handle, IntPtr.Zero, screen.Left, screen.Top, screen.Width, screen.Height, SwpNoZOrder | SwpNoActivate);
            return;
        }
        int width = (int)Math.Min(PanelWidth * scale, screen.Width * 0.94);
        int height = (int)Math.Min(PanelHeight * scale, screen.Height * 0.9);
        int x = screen.Left + (screen.Width - width) / 2;
        int y = screen.Top + Math.Max(8, (int)((screen.Height - height) * .32));
        SetWindowPos(handle, IntPtr.Zero, x, y, width, height, SwpNoZOrder | SwpNoActivate);
    }
    private void HidePanel(bool restorePrevious)
    {
        if (!PanelShown) return;
        PanelShown = false;
        SetCloaked(true);
        _console?.Clear(); // No live thumbnails while hidden.
        if (restorePrevious && _beforeOpen != IntPtr.Zero) WindowsAdapter.Activate(_beforeOpen);
        // A cloaked window that still holds the foreground would swallow keystrokes invisibly
        // (e.g. a jump Windows refused). In that case really hide it to release focus.
        Dispatcher.BeginInvoke(() =>
        {
            if (!PanelShown && IsVisible && WindowsAdapter.GetForegroundWindow() == new WindowInteropHelper(this).Handle) Hide();
        }, DispatcherPriority.Background);
    }
    // ---- 控制台: maximised, every session as a big card with a live window preview or its latest turns. ----
    private bool _consoleMode;
    private bool _placedForConsole;
    private ConsoleView? _console;
    private readonly Dictionary<string, (DateTime Stamp, IReadOnlyList<ChatMessage> Messages)> _transcriptCache = new();

    private void ConsoleMode_Click(object sender, RoutedEventArgs e) => ToggleConsoleMode();

    private void ToggleConsoleMode()
    {
        if (!_consoleMode && _conversationMode) ToggleConversationMode();
        _consoleMode = !_consoleMode;
        SessionList.Visibility = _consoleMode ? Visibility.Collapsed : Visibility.Visible;
        ConsolePane.Visibility = _consoleMode ? Visibility.Visible : Visibility.Collapsed;
        ConsoleButton.Background = _consoleMode ? new SolidColorBrush(Color.FromRgb(0x3A, 0x40, 0x48)) : Brushes.Transparent;
        ConsoleButton.Foreground = _consoleMode ? new SolidColorBrush(Color.FromRgb(0xF4, 0xF3, 0xEF)) : new SolidColorBrush(Color.FromRgb(0x6B, 0x70, 0x78));
        PlaceOnScreen(_placedOn.IsEmpty ? WinForms.Screen.FromPoint(WinForms.Cursor.Position).WorkingArea : _placedOn);
        _placedForConsole = _consoleMode;
        if (_consoleMode) UpdateConsole();
        else _console?.Clear();
    }

    private void UpdateConsole()
    {
        if (!_consoleMode || !PanelShown) return;
        _console ??= new ConsoleView(this, ConsolePane, ActivateSessionAsync, LiveWindowOf, ReadConversationAsync);
        _console.Update(_view.Cast<SessionEntry>().ToList());
    }

    /// <summary>The window to preview live for a session, or zero when its window is not showing it
    /// (background tab, minimised, Codex thread that may not be the open one).</summary>
    private IntPtr LiveWindowOf(SessionEntry s)
    {
        var hwnd = s.Kind switch
        {
            "terminal-tab" => _terminalTabs.Get(s.WindowHandle)?.FirstOrDefault(t => WindowsAdapter.ParseClaudeCodeTitle(t.Title).title == s.OriginalTitle)?.Selected == true
                ? s.WindowHandle : IntPtr.Zero,
            "web" => s.TabActive ? WindowsAdapter.FindBrowserWindow(s.OriginalTitle) : IntPtr.Zero,
            "codex-thread" => IntPtr.Zero,
            _ => s.WindowHandle
        };
        return WindowsAdapter.Exists(hwnd) && !WindowsAdapter.IsMinimized(hwnd) ? hwnd : IntPtr.Zero;
    }

    /// <summary>Latest turns for a card; transcripts are re-read only when they changed on disk.</summary>
    private async Task<IReadOnlyList<ChatMessage>?> ReadConversationAsync(SessionEntry s)
    {
        if (s.TranscriptPath.Length > 0)
        {
            var stamp = System.IO.File.GetLastWriteTimeUtc(s.TranscriptPath);
            if (_transcriptCache.TryGetValue(s.TranscriptPath, out var cached) && cached.Stamp == stamp) return cached.Messages;
            var path = s.TranscriptPath;
            bool codex = s.Kind == "codex-thread";
            var messages = await Task.Run(() => codex ? Conversation.ReadCodex(path) : Conversation.ReadClaude(path));
            _transcriptCache[path] = (stamp, messages);
            return messages;
        }
        if (s.Kind != "web") return null;
        var reply = await _browser.RequestConversationAsync(s);
        return reply is { Supported: true } ? reply.Messages.Select(m => new ChatMessage(m.Role == "user", m.Text)).ToList() : null;
    }
    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { HidePanel(restorePrevious: true); e.Handled = true; }
        if (e.Key == Key.Tab) { ToggleConversationMode(); e.Handled = true; return; }
        if (e.Key == Key.Down && SearchBox.IsKeyboardFocused)
        {
            SessionList.SelectedIndex = Math.Max(0, SessionList.SelectedIndex);
            SessionList.Focus();
            e.Handled = true;
        }
        if (e.Key == Key.Enter && SearchBox.IsKeyboardFocused)
        {
            var first = _view.Cast<SessionEntry>().FirstOrDefault();
            if (first != null) { _ = ActivateSessionAsync(first); e.Handled = true; }
        }
    }
    private void SessionList_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SessionList.SelectedItem is SessionEntry s)
        {
            _ = ActivateSessionAsync(s);
            e.Handled = true;
        }
    }
    // ---- Layout: grouped by tool, or one mixed list. Both use the same urgency order. ----
    private void Layout_Click(object sender, RoutedEventArgs e)
    {
        _settings.GroupByTool = !_settings.GroupByTool;
        _settings.Save();
        ApplyLayout();
    }

    private void ApplyLayout()
    {
        using (_view.DeferRefresh())
        {
            _view.GroupDescriptions.Clear();
            _view.SortDescriptions.Clear();
            if (_settings.GroupByTool)
            {
                _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SessionEntry.GroupName)));
                _view.SortDescriptions.Add(new SortDescription(nameof(SessionEntry.GroupOrder), ListSortDirection.Ascending));
            }
            _view.SortDescriptions.Add(new SortDescription(nameof(SessionEntry.SortTier), ListSortDirection.Ascending));
            _view.SortDescriptions.Add(new SortDescription(nameof(SessionEntry.WaitSortUtc), ListSortDirection.Ascending));
            _view.SortDescriptions.Add(new SortDescription(nameof(SessionEntry.FavoriteFirst), ListSortDirection.Descending));
            _view.SortDescriptions.Add(new SortDescription(nameof(SessionEntry.Sequence), ListSortDirection.Ascending));
        }
        // The button names the current layout; clicking switches to the other one.
        LayoutButton.Content = _settings.GroupByTool ? "分组" : "混合";
        LayoutButton.ToolTip = _settings.GroupByTool ? "当前按工具分组，点击切换为混合排列" : "当前混合排列，点击切换为按工具分组";
        if (_console != null) UpdateConsole();
    }

    // ---- Conversation view: the selected session's latest turns, to decide whether to switch over. ----
    private bool _conversationMode;
    private int _conversationRequest;
    private string _shownConversation = "";

    private void ConversationMode_Click(object sender, RoutedEventArgs e) => ToggleConversationMode();
    private void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_conversationMode) _ = LoadConversationAsync(force: true);
    }

    private void ToggleConversationMode()
    {
        if (!_conversationMode && _consoleMode) ToggleConsoleMode();
        _conversationMode = !_conversationMode;
        // A narrow list column turns the tile grid into a single column automatically.
        ListColumn.Width = _conversationMode ? new GridLength(380) : new GridLength(1, GridUnitType.Star);
        ConversationColumn.Width = _conversationMode ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        ConversationPane.Visibility = _conversationMode ? Visibility.Visible : Visibility.Collapsed;
        ConversationButton.Background = _conversationMode ? new SolidColorBrush(Color.FromRgb(0x3A, 0x40, 0x48)) : Brushes.Transparent;
        ConversationButton.Foreground = _conversationMode ? new SolidColorBrush(Color.FromRgb(0xF4, 0xF3, 0xEF)) : new SolidColorBrush(Color.FromRgb(0x6B, 0x70, 0x78));
        if (_conversationMode && SessionList.SelectedItem == null && SessionList.Items.Count > 0) SessionList.SelectedIndex = 0;
        _ = LoadConversationAsync(force: true);
    }

    private async Task LoadConversationAsync(bool force)
    {
        if (!_conversationMode) return;
        if (SessionList.SelectedItem is not SessionEntry s)
        {
            ConversationTitle.Text = ConversationMeta.Text = "";
            ShowConversation(null, "选择一个会话，这里会显示最近几轮对话");
            return;
        }
        int request = ++_conversationRequest;
        ConversationTitle.Text = s.DisplayTitle;
        ConversationMeta.Text = string.Join("  ·  ", new[] { s.Provider, s.ProjectPath.Length > 0 ? s.ProjectPath : s.SourceName }.Where(x => x.Length > 0));

        IReadOnlyList<ChatMessage>? messages = null;
        string empty;
        if (s.TranscriptPath.Length > 0)
        {
            messages = s.Kind == "codex-thread" ? Conversation.ReadCodex(s.TranscriptPath) : Conversation.ReadClaude(s.TranscriptPath);
            empty = "还没有对话内容";
        }
        else if (s.Kind == "web")
        {
            var reply = await _browser.RequestConversationAsync(s);
            if (request != _conversationRequest) return; // Selection moved on while we waited.
            if (reply == null) empty = "读取不到这个标签页的对话。\n请在 chrome://extensions 里把 AI Focus 扩展重新加载到最新版本，并刷新一下该页面。";
            else if (!reply.Supported) empty = "这个网站暂不支持读取对话\n（目前支持 ChatGPT、Claude、Gemini）";
            else
            {
                messages = reply.Messages.Select(m => new ChatMessage(m.Role == "user", m.Text)).ToList();
                empty = "页面上还没有对话";
            }
        }
        else empty = s.Provider == "Claude Code"
            ? "没有找到这个会话的记录\n（终端标题和 Claude Code 会话记录没对上）"
            : "这个会话没有可读取的对话记录";

        // Refreshes run every few seconds: only re-render (and reset the scroll) when something changed.
        var signature = s.RuntimeKey + "|" + empty + "|" + string.Join("\u0001", messages?.Select(m => m.Text.Length + ":" + m.Text[..Math.Min(40, m.Text.Length)]) ?? Array.Empty<string>());
        if (!force && signature == _shownConversation) return;
        _shownConversation = signature;
        ShowConversation(messages, empty);
    }

    private void ShowConversation(IReadOnlyList<ChatMessage>? messages, string empty)
    {
        bool has = messages is { Count: > 0 };
        ConversationList.ItemsSource = has ? messages : null;
        ConversationEmpty.Text = empty;
        ConversationEmpty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        // Newest turn at the bottom, like the chat itself.
        if (has) Dispatcher.BeginInvoke(ConversationScroll.ScrollToEnd, DispatcherPriority.Loaded);
    }

    private void SessionList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Single click already jumps outside the conversation view; double click is for that view.
        if (_conversationMode && SessionList.SelectedItem is SessionEntry s) _ = ActivateSessionAsync(s);
    }

    /// <summary>One click on a tile jumps (a second click was pure delay). In the conversation view a
    /// click only selects, to preview; double click or Enter jumps there.</summary>
    private void SessionList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_conversationMode) return;
        // Clicks on the star / to-do buttons inside a tile are theirs, not a jump.
        for (var node = e.OriginalSource as DependencyObject; node != null && node != SessionList; node = VisualTreeHelper.GetParent(node))
        {
            if (node is Button) return;
            if (node is ListBoxItem { DataContext: SessionEntry s })
            {
                _ = ActivateSessionAsync(s);
                return;
            }
        }
    }
    private async Task ActivateSessionAsync(SessionEntry s)
    {
        bool success;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        // With the panel open, the user's own window is the one that was in front before it.
        RememberReturnWindow(PanelShown ? _beforeOpen : WindowsAdapter.GetForegroundWindow());
        HidePanel(restorePrevious: false);
        if (s.Kind == "web")
        {
            success = await _browser.ActivateAsync(s);
            // The extension switches the tab, but Chrome is under the same foreground lock as we are:
            // raise its window ourselves the moment its title shows the tab (no fixed wait).
            if (success)
            {
                for (int attempt = 0; attempt < 20; attempt++)
                {
                    var window = WindowsAdapter.FindBrowserWindow(s.OriginalTitle);
                    if (window != IntPtr.Zero) { WindowsAdapter.Activate(window); break; }
                    await Task.Delay(25);
                }
            }
        }
        else if (s.Kind == "codex-thread" && s.ThreadId != null)
        {
            // Bring Codex up immediately; the deep link goes through a short-lived ChatGPT.exe that
            // forwards it to the running app, which is slow to start, so it runs in the background.
            success = _codexWindow != IntPtr.Zero && WindowsAdapter.Activate(_codexWindow);
            var threadId = s.ThreadId;
            var exe = _codexExe;
            bool opened = await Task.Run(() => CodexThreads.Open(threadId, exe));
            success |= opened;
            if (opened) _store.MarkSeen(s.PersistenceKey, DateTime.UtcNow);
        }
        else if (s.Kind == "terminal-tab")
        {
            // Pick the tab first so the window comes up already showing it (cached UIA element: one call).
            _terminalTabs.Select(s.WindowHandle, s.OriginalTitle, s.TerminalTabIndex);
            success = WindowsAdapter.Activate(s.WindowHandle);
        }
        else success = WindowsAdapter.Activate(s.WindowHandle);
        Log.Write($"activate {s.Kind} \"{s.DisplayTitle}\" success={success} in {watch.ElapsedMilliseconds} ms");
        if (success)
        {
            s.Completed = false;
            s.LastFocusedUtc = DateTime.UtcNow;
            s.Changed(null);
        }
        else if (s.Kind == "web")
            Notice?.Invoke("浏览器扩展没有连接，无法切换到「" + s.DisplayTitle + "」");
        else if (s.Kind != "codex-thread" && WindowsAdapter.Exists(s.WindowHandle))
        {
            // Still open; Windows just refused to hand over focus. Point at it instead of claiming it closed.
            WindowsAdapter.Flash(s.WindowHandle);
            Notice?.Invoke("Windows 没有允许切换，已在任务栏高亮「" + s.DisplayTitle + "」");
        }
        else
        {
            Notice?.Invoke("「" + s.DisplayTitle + "」已经关闭");
            RefreshSessions();
        }
    }

    // Drops the site-name suffix ("Chat - Claude", "Chat | Google Gemini"); the provider is shown on the tile already.
    private static string WebTitle(BrowserTab tab, string provider)
    {
        var title = tab.Title.Trim();
        if (title.Length == 0) return tab.Url;
        int cut = Math.Max(title.LastIndexOf(" - ", StringComparison.Ordinal), title.LastIndexOf(" | ", StringComparison.Ordinal));
        if (cut > 0 && title[(cut + 3)..].Contains(provider, StringComparison.OrdinalIgnoreCase))
            title = title[..cut].TrimEnd();
        return title;
    }
    // The extension (v0.2+) resolves the provider from its site table; older versions only send the URL.
    private static string WebProvider(BrowserTab tab) =>
        string.IsNullOrWhiteSpace(tab.Provider) ? ProviderFromUrl(tab.Url) : tab.Provider;
    private static string ProviderFromUrl(string url)
    {
        try
        {
            var host = new Uri(url).Host.ToLowerInvariant();
            if (host.EndsWith("chatgpt.com") || host == "chat.openai.com") return "ChatGPT";
            if (host.EndsWith("claude.ai")) return "Claude";
            if (host.Contains("muse")) return "Muse";
        }
        catch { }
        return "网页 AI";
    }
    private static string PersistentWebKey(string profile, string url)
    {
        try
        {
            var uri = new Uri(url);
            return "web:" + profile + ":" + uri.Host.ToLowerInvariant() + uri.AbsolutePath;
        }
        catch { return "web:" + profile + ":unknown"; }
    }

    /// <summary>Everything a refresh needs from the system, gathered off the UI thread.</summary>
    private sealed record Snapshot(
        IntPtr Focused,
        IReadOnlyList<(string profile, string browser, BrowserTab tab)> WebTabs,
        IReadOnlyList<CodexThreads.Thread> CodexThreads,
        IReadOnlyDictionary<string, ClaudeSessions.Session> ClaudeSessions,
        IReadOnlyList<WindowsAdapter.WindowInfo> Windows);

    private readonly object _gatherLock = new();
    private bool _refreshPending;

    /// <summary>
    /// The slow part of a refresh: window enumeration, process lookups, transcript scans.
    /// Thread-safe (the scanners keep state, so runs are serialised).
    /// </summary>
    private Snapshot Gather()
    {
        lock (_gatherLock)
        {
            return new Snapshot(WindowsAdapter.GetForegroundWindow(), _browser.CurrentTabs(),
                _codex.Scan(), _claude.Scan(), WindowsAdapter.Discover());
        }
    }

    /// <summary>Synchronous refresh, for actions that need fresh data right now (jumps, toast clicks).</summary>
    private void RefreshSessions() => Apply(Gather());

    /// <summary>Background refresh for the timer and for opening the panel: the UI never waits on it.</summary>
    private async void RefreshSessionsInBackground()
    {
        if (_refreshPending) return;
        _refreshPending = true;
        try { Apply(await Task.Run(Gather)); }
        catch (Exception ex) { FeedbackText.Text = "刷新失败：" + ex.Message; }
        finally { _refreshPending = false; }
    }

    private void Apply(Snapshot snapshot)
    {
        try
        {
            var existing = _sessions.ToDictionary(s => s.RuntimeKey, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var focused = snapshot.Focused;
            var webTabs = snapshot.WebTabs;
            var webProviders = webTabs.Select(x => WebProvider(x.tab)).ToHashSet();

            var codexThreads = snapshot.CodexThreads;
            var claudeSessions = snapshot.ClaudeSessions;
            var discovered = snapshot.Windows;
            _terminalTabs.Watch(discovered.Where(w => w.IsTabbedTerminal).Select(w => w.Handle));
            foreach (var w in discovered)
            {
                if (w.Provider == "Codex" && w.Kind == "desktop")
                {
                    _codexWindow = w.Handle;
                    if (w.ExecutablePath.Length > 0) _codexExe = w.ExecutablePath;
                }
                if (w.Kind == "browser-window" && webProviders.Contains(w.Provider)) continue;
                // Windows Terminal: one session per AI tab, including tabs that are not in front.
                if (w.IsTabbedTerminal && _terminalTabs.Get(w.Handle) is { Count: > 0 } tabs)
                {
                    var used = new Dictionary<string, int>(StringComparer.Ordinal);
                    foreach (var tab in tabs)
                    {
                        var (clean, activity) = WindowsAdapter.ParseClaudeCodeTitle(tab.Title);
                        var provider = activity.Length > 0 ? "Claude Code" : WindowsAdapter.AiCliProvider(tab.Title);
                        if (provider == null) continue;
                        // Keyed by title, so a closed or reordered tab can never hand its state to a neighbour.
                        int n = used[clean] = used.GetValueOrDefault(clean) + 1;
                        var tabKey = "wt:" + w.Handle.ToInt64() + ":" + clean + (n > 1 ? "#" + n : "");
                        var t = Upsert(seen, existing, tabKey, "window:windowsterminal:" + clean.ToLowerInvariant(), out bool tabIsNew);
                        t.Provider = provider;
                        t.Kind = "terminal-tab";
                        t.SourceName = "Windows Terminal 标签页";
                        t.OriginalTitle = clean;
                        t.WindowHandle = w.Handle;
                        t.TerminalTabIndex = tab.Index;
                        if (provider == "Claude Code") ApplyClaudeSession(t, claudeSessions);
                        SetFocus(t, focused == w.Handle && tab.Selected);
                        UpdateActivity(t, activity, tabIsNew);
                        t.Changed(null);
                    }
                    continue;
                }
                // Terminal host whose visible tab is a plain shell (tab list not read yet).
                if (w.Provider.Length == 0) continue;
                var displayTitle = w.Provider == "Codex" && w.Title.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase)
                    ? "Codex Desktop" : w.Title;
                var s = Upsert(seen, existing, "window:" + w.Handle.ToInt64(),
                    "window:" + w.Process.ToLowerInvariant() + ":" + w.Title.Trim().ToLowerInvariant(), out bool isNew);
                // A terminal can switch between plain shell and Claude Code, so re-classify every refresh.
                s.Provider = w.Provider;
                s.Kind = w.Kind;
                s.SourceName = w.Process + (w.Kind == "browser-window" ? " · 浏览器窗口" : "");
                s.OriginalTitle = displayTitle;
                s.WindowHandle = w.Handle;
                // Claude Code inside a terminal would only get the terminal icon; keep the Claude glyph instead.
                s.IconImage = w.Kind == "terminal" ? null : IconCache.ForWindow(w.Handle, w.Process, w.ExecutablePath);
                SetFocus(s, focused == w.Handle);
                UpdateActivity(s, w.Activity, isNew);
                if (w.Provider == "Claude Code") ApplyClaudeSession(s, claudeSessions);
                s.Changed(null);
            }

            foreach (var (profile, browser, tab) in webTabs)
            {
                var s = Upsert(seen, existing, "tab:" + profile + ":" + tab.Id, PersistentWebKey(profile, tab.Url), out bool isNew);
                s.Kind = "web";
                s.SourceName = browser + " 标签页";
                s.ProfileId = profile;
                s.Provider = WebProvider(tab);
                s.OriginalTitle = WebTitle(tab, s.Provider);
                s.BrowserWindowId = tab.WindowId;
                s.BrowserTabId = tab.Id;
                s.Url = tab.Url;
                s.TabActive = tab.Active;
                s.IconImage = IconCache.FromDataUrl(tab.Icon);
                SetFocus(s, tab.Focused);
                // Generation state comes from the extension's content script (v0.3+).
                UpdateActivity(s, tab.Generating switch { true => "running", false => "idle", null => "" }, isNew);
                s.Changed(null);
            }
            // Focus tool, not a history browser: only threads that are running or have an unread reply.
            var now = DateTime.UtcNow;
            foreach (var t in codexThreads)
            {
                var key = "codex:" + t.Id;
                bool awaitingReply = t.RepliedUtc is { } replied && now - replied < UnrepliedWindow && replied > _store.SeenUtc(key);
                if (t.Activity != "running" && !awaitingReply) continue;
                seen.Add(key);
                bool isNew = !existing.TryGetValue(key, out var s);
                if (isNew)
                {
                    s = new SessionEntry
                    {
                        RuntimeKey = key, PersistenceKey = key, ThreadId = t.Id,
                        Kind = "codex-thread", Provider = "Codex", SourceName = "Codex 会话"
                    };
                    _store.Apply(s);
                    _sessions.Add(s);
                    existing[key] = s;
                }
                s!.OriginalTitle = t.Title;
                s.IconImage ??= IconCache.ForWindow(_codexWindow, "Codex", _codexExe ?? CodexThreads.DefaultExecutable);
                s.ProjectPath = t.Directory;
                s.Preview = t.LastMessage;
                s.TranscriptPath = t.Path;
                if (t.UpdatedUtc > s.LastFocusedUtc) s.LastFocusedUtc = t.UpdatedUtc;
                s.LastSeenUtc = DateTime.UtcNow;
                UpdateActivity(s, t.Activity, isNew);
                s.Completed = awaitingReply;
                if (awaitingReply) s.CompletedUtc = t.RepliedUtc!.Value;
                SetStall(s, t.Activity == "running" && now - t.UpdatedUtc >= StuckAfter ? "stuck" : "", t.UpdatedUtc);
                s.Changed(null);
            }
            foreach (var stale in _sessions.Where(s => !seen.Contains(s.RuntimeKey)).ToArray())
                _sessions.Remove(stale);

            var old = SessionList.SelectedItem as SessionEntry;
            _view.Refresh();
            if (old != null && _sessions.Contains(old)) SessionList.SelectedItem = old;
            UpdateCounts();
            SessionsRefreshed?.Invoke();
            if (_conversationMode && PanelShown) _ = LoadConversationAsync(force: false);
            UpdateConsole();
            // Remember the last outside window in front, so jumps made while the panel stays open
            // can still "送回来" to the user's own work.
            if (WindowsAdapter.Exists(focused) && !WindowsAdapter.IsOwnWindow(focused)) _beforeOpen = focused;
        }
        catch (Exception ex)
        {
            FeedbackText.Text = "刷新失败：" + ex.Message;
        }
    }

    private SessionEntry Upsert(HashSet<string> seen, Dictionary<string, SessionEntry> existing, string key, string persistenceKey, out bool isNew)
    {
        seen.Add(key);
        isNew = !existing.TryGetValue(key, out var s);
        if (isNew)
        {
            s = new SessionEntry { RuntimeKey = key, PersistenceKey = persistenceKey };
            _store.Apply(s);
            _sessions.Add(s);
            existing[key] = s;
        }
        s!.LastSeenUtc = DateTime.UtcNow;
        return s;
    }
    /// <summary>Terminal title == Claude Code session title, which links the tab to its cwd and last reply.</summary>
    private void ApplyClaudeSession(SessionEntry s, IReadOnlyDictionary<string, ClaudeSessions.Session> sessions)
    {
        if (!sessions.TryGetValue(s.OriginalTitle, out var session)) { SetStall(s, "", default); return; }
        s.ProjectPath = session.Directory;
        s.Preview = session.LastMessage;
        s.TranscriptPath = session.Path;
        var quiet = DateTime.UtcNow - session.UpdatedUtc;
        var stall = s.Activity != "running" ? ""
            : session.PendingTool && quiet >= ApprovalAfter ? "approval"
            : quiet >= StuckAfter ? "stuck"
            : "";
        SetStall(s, stall, session.UpdatedUtc);
    }

    // A tool call this old without a result is most likely a permission prompt (or a long command).
    private static readonly TimeSpan ApprovalAfter = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(3);

    /// <summary>Tracks "running but no progress"; notifies once when a session starts stalling.</summary>
    private void SetStall(SessionEntry s, string stall, DateTime lastProgressUtc)
    {
        var previous = s.Stall;
        s.Stall = stall;
        if (stall.Length == 0 || previous == stall) return;
        s.StallSinceUtc = lastProgressUtc;
        if (previous.Length == 0 && !s.IsFocused) SessionStalled?.Invoke(s);
    }
    private static void SetFocus(SessionEntry s, bool focused)
    {
        s.IsFocused = focused;
        if (!focused) return;
        s.LastFocusedUtc = DateTime.UtcNow;
        s.Completed = false; // Looking at it counts as seen.
    }

    /// <summary>Running -> stopped while the user is not looking at it means "done, waiting for you".</summary>
    private void UpdateActivity(SessionEntry s, string activity, bool isNew)
    {
        var previous = s.Activity;
        s.Activity = activity;
        if (activity == "running") { s.Completed = false; return; }
        if (isNew || previous != "running" || s.IsFocused) return;
        s.Completed = true;
        s.CompletedUtc = DateTime.UtcNow;
        SessionCompleted?.Invoke(s);
    }

    private bool Filter(object obj)
    {
        if (obj is not SessionEntry s) return false;
        if (_filterMode == "attention" && !s.WaitingForUser) return false;
        if (_filterMode == "favorite" && !s.Favorite) return false;
        string q = SearchBox?.Text?.Trim() ?? "";
        return q.Length == 0 ||
            s.DisplayTitle.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            s.Provider.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            s.ProjectLabel.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            s.SourceName.Contains(q, StringComparison.OrdinalIgnoreCase);
    }
    private void UpdateCounts()
    {
        // Header only surfaces what needs attention; the total already sits on the "全部" filter.
        var parts = new List<string>();
        int running = _sessions.Count(s => s.Activity == "running" && !s.WaitingForUser);
        int done = _sessions.Count(s => s.Completed && !s.NeedsAttention), pending = _sessions.Count(s => s.NeedsAttention);
        int stalled = _sessions.Count(s => s.Stall.Length > 0 && !s.Completed && !s.NeedsAttention);
        if (done > 0) parts.Add(done + " 个待回复");
        if (stalled > 0) parts.Add(stalled + " 个可能在等你");
        if (pending > 0) parts.Add(pending + " 个待处理");
        if (running > 0) parts.Add(running + " 个运行中");
        CountsText.Text = string.Join("  ·  ", parts);
        AttentionButton.Content = "待处理 " + _sessions.Count(s => s.WaitingForUser);
        AllButton.Content = "全部 " + _sessions.Count;
    }
    private const double MinTileWidth = 260, TileGap = 8, ScrollBarAllowance = 20;
    private void UpdateTileWidth()
    {
        var available = Math.Max(MinTileWidth, SessionList.ActualWidth - ScrollBarAllowance);
        int columns = Math.Max(1, (int)(available / MinTileWidth));
        Resources["TileWidth"] = Math.Floor(available / columns) - TileGap;
    }
    private void UpdateFilterButtons()
    {
        foreach (var b in new[] { AllButton, AttentionButton, FavoriteButton })
        {
            bool selected = (string)b.Tag == _filterMode;
            // Calm palette: the active filter is a quiet dark chip, the rest stay neutral.
            b.Background = selected ? new SolidColorBrush(Color.FromRgb(0x3A, 0x40, 0x48)) : Brushes.Transparent;
            b.Foreground = selected ? new SolidColorBrush(Color.FromRgb(0xF4, 0xF3, 0xEF)) : new SolidColorBrush(Color.FromRgb(0x6B, 0x70, 0x78));
        }
    }
    private void Filter_Click(object sender, RoutedEventArgs e)
    {
        _filterMode = (string)((Button)sender).Tag;
        _view.Refresh();
        UpdateFilterButtons();
        UpdateConsole();
    }
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _view?.Refresh();
        if (_view != null) UpdateConsole();
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshSessionsInBackground();
    private void Close_Click(object sender, RoutedEventArgs e) => HidePanel(restorePrevious: true);
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Change(SessionEntry s, Action action)
    {
        action();
        _store.Save(s);
        s.Changed(null);
        _view.Refresh();
        UpdateCounts();
        SessionsRefreshed?.Invoke();
    }
    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is SessionEntry s) Change(s, () => s.Favorite = !s.Favorite);
        e.Handled = true;
    }
    private void Attention_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is SessionEntry s) Change(s, () => s.NeedsAttention = !s.NeedsAttention);
        e.Handled = true;
    }
    private static SessionEntry? MenuSession(object sender) => ((MenuItem)sender).DataContext as SessionEntry;
    private void FavoriteMenu_Click(object sender, RoutedEventArgs e)
    {
        if (MenuSession(sender) is SessionEntry s) Change(s, () => s.Favorite = !s.Favorite);
    }
    private void AttentionMenu_Click(object sender, RoutedEventArgs e)
    {
        if (MenuSession(sender) is SessionEntry s) Change(s, () => s.NeedsAttention = !s.NeedsAttention);
    }
    private void OpenMenu_Click(object sender, RoutedEventArgs e)
    {
        if (MenuSession(sender) is SessionEntry s) _ = ActivateSessionAsync(s);
    }
    private void RenameMenu_Click(object sender, RoutedEventArgs e)
    {
        if (MenuSession(sender) is not SessionEntry s) return;
        var name = AskText("修改会话名称", "自定义显示名称（留空恢复原标题）", s.CustomTitle);
        if (name != null) Change(s, () => s.CustomTitle = name);
    }
    private void WorkspaceMenu_Click(object sender, RoutedEventArgs e)
    {
        if (MenuSession(sender) is not SessionEntry s) return;
        var name = AskText("设置工作区", "项目名称（留空取消分组）", s.Workspace);
        if (name != null) Change(s, () => s.Workspace = name);
    }

    private string? AskText(string title, string prompt, string existing)
    {
        var dialog = new Window { Title = title, Width = 460, SizeToContent = SizeToContent.Height, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            Background = Brushes.White, FontFamily = FontFamily, FontSize = 15 };
        TextOptions.SetTextFormattingMode(dialog, TextFormattingMode.Display);
        var layout = new StackPanel { Margin = new Thickness(20) };
        layout.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 12) });
        var input = new TextBox { Text = existing, FontSize = 15, Padding = new Thickness(8) };
        layout.Children.Add(input);
        var ok = new Button { Content = "保存", Width = 90, Height = 34, IsDefault = true, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        ok.Click += (_, _) => { dialog.DialogResult = true; };
        layout.Children.Add(ok);
        dialog.Content = layout;
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }
    public Task ActivateFromNotificationAsync(SessionEntry s) =>
        _sessions.Contains(s) ? ActivateSessionAsync(s) : Task.CompletedTask;

    /// <summary>Toast click: the session is looked up again by key, since entries are rebuilt as windows change.</summary>
    public Task ActivateByKeyAsync(string runtimeKey)
    {
        RefreshSessions();
        var s = _sessions.FirstOrDefault(x => x.RuntimeKey == runtimeKey);
        if (s == null) { Notice?.Invoke("这个会话已经关闭了"); return Task.CompletedTask; }
        return ActivateSessionAsync(s);
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings();

    public void ShowSettings()
    {
        // Dialog owners must have been shown once.
        if (!PanelShown) ShowPanel();
        // Release hotkeys while capturing, otherwise pressing the current one would trigger it.
        ReleaseHotkeys();
        var muted = (System.Windows.Media.Brush)FindResource("InkMuted");

        var dialog = new Window { Title = "AI Focus 设置", Width = 480, SizeToContent = SizeToContent.Height, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            Background = (System.Windows.Media.Brush)FindResource("Paper"), FontFamily = FontFamily, FontSize = 15,
            Foreground = (System.Windows.Media.Brush)FindResource("Ink") };
        TextOptions.SetTextFormattingMode(dialog, TextFormattingMode.Display);
        var layout = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        var hint = new TextBlock { Text = "点击输入框后按下新的组合键（需包含 Ctrl、Alt 或 Win）；Backspace 清空", FontSize = 13,
            Foreground = muted, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };

        // Returns a getter for the chosen hotkey text ("" = disabled when allowEmpty).
        Func<string> AddCapture(string label, string current, bool allowEmpty, double topMargin)
        {
            string chosen = current;
            layout.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, topMargin, 0, 8) });
            var box = new TextBox { Text = chosen.Length > 0 ? HotkeyText.Pretty(chosen) : "未启用", IsReadOnly = true, FontSize = 17,
                Padding = new Thickness(10, 8, 10, 8), IsReadOnlyCaretVisible = false, Background = Brushes.White };
            box.PreviewKeyDown += (_, ke) =>
            {
                ke.Handled = true;
                var key = ke.Key == Key.System ? ke.SystemKey : ke.Key;
                if (HotkeyText.IsModifierKey(key)) return;
                if (allowEmpty && Keyboard.Modifiers == ModifierKeys.None && key is Key.Back or Key.Delete)
                {
                    chosen = "";
                    box.Text = "未启用";
                    return;
                }
                var text = HotkeyText.Format(Keyboard.Modifiers, key);
                if (!HotkeyText.TryParse(text, out uint _m, out uint _vk))
                {
                    hint.Text = "“" + HotkeyText.Pretty(text) + "” 不可用：需要包含 Ctrl、Alt 或 Win";
                    return;
                }
                chosen = text;
                box.Text = HotkeyText.Pretty(text);
                hint.Text = "保存后生效";
            };
            layout.Children.Add(box);
            box.Tag = (Action<string>)(value => { chosen = value; box.Text = value.Length > 0 ? HotkeyText.Pretty(value) : "未启用"; });
            return () => chosen;
        }
        var toggle = AddCapture("呼出 / 隐藏面板", _settings.Hotkey, allowEmpty: false, 0);
        var toggleBox = (TextBox)layout.Children[^1];
        var next = AddCapture("送过去：跳到下一个等你的会话", _settings.NextHotkey, allowEmpty: true, 18);
        var nextBox = (TextBox)layout.Children[^1];
        layout.Children.Add(new TextBlock { Text = "按完成先后依次跳转，无需打开面板", FontSize = 13, Foreground = muted, Margin = new Thickness(0, 6, 0, 0) });
        var back = AddCapture("送回来：回到刚才自己在用的窗口", _settings.BackHotkey, allowEmpty: true, 18);
        var backBox = (TextBox)layout.Children[^1];
        layout.Children.Add(hint);

        var autoStart = new System.Windows.Controls.CheckBox { Content = "开机自动启动",
            IsChecked = AutoStart.IsEnabled, Margin = new Thickness(0, 20, 0, 0), VerticalContentAlignment = VerticalAlignment.Center };
        layout.Children.Add(autoStart);

        var notify = new System.Windows.Controls.CheckBox { Content = "AI 回复完成时弹出通知",
            IsChecked = _settings.NotifyOnComplete, Margin = new Thickness(0, 10, 0, 0), VerticalContentAlignment = VerticalAlignment.Center };
        layout.Children.Add(notify);
        var widget = new System.Windows.Controls.CheckBox { Content = "显示悬浮窗（置顶的状态胶囊）",
            IsChecked = _settings.ShowWidget, Margin = new Thickness(0, 10, 0, 0), VerticalContentAlignment = VerticalAlignment.Center };
        layout.Children.Add(widget);

        var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        var reset = new Button { Content = "恢复默认", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "取消", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var save = new Button { Content = "保存", Padding = new Thickness(18, 6, 18, 6), IsDefault = true };
        reset.Click += (_, _) =>
        {
            ((Action<string>)toggleBox.Tag)(AppSettings.DefaultHotkey);
            ((Action<string>)nextBox.Tag)(AppSettings.DefaultNextHotkey);
            ((Action<string>)backBox.Tag)(AppSettings.DefaultBackHotkey);
            hint.Text = "保存后生效";
        };
        save.Click += (_, _) =>
        {
            var chosen = new[] { toggle(), next(), back() }.Where(k => k.Length > 0).ToList();
            if (chosen.Count != chosen.Distinct().Count())
            {
                hint.Text = "快捷键不能重复";
                return;
            }
            foreach (var (id, text) in new[] { (HotkeyId, toggle()), (NextHotkeyId, next()), (BackHotkeyId, back()) })
            {
                if (TryRegisterHotkey(id, text)) continue;
                ReleaseHotkeys();
                hint.Text = "“" + HotkeyText.Pretty(text) + "” 已被其他程序占用，请换一个组合";
                return;
            }
            try { AutoStart.Set(autoStart.IsChecked == true); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
            {
                hint.Text = "无法写入开机启动项：" + ex.Message;
                return;
            }
            _settings.Hotkey = toggle();
            _settings.NextHotkey = next();
            _settings.BackHotkey = back();
            _settings.NotifyOnComplete = notify.IsChecked == true;
            _settings.ShowWidget = widget.IsChecked == true;
            _settings.Save();
            dialog.DialogResult = true;
        };
        buttons.Children.Add(reset);
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        layout.Children.Add(buttons);
        dialog.Content = layout;
        dialog.Loaded += (_, _) => toggleBox.Focus();
        dialog.ShowDialog();
        // Cancelled (or saved): make sure the stored hotkeys are active again.
        if (!_registeredHotkeys.Contains(HotkeyId)) TryRegisterHotkey(HotkeyId, _settings.Hotkey);
        if (!_registeredHotkeys.Contains(NextHotkeyId)) TryRegisterHotkey(NextHotkeyId, _settings.NextHotkey);
        if (!_registeredHotkeys.Contains(BackHotkeyId)) TryRegisterHotkey(BackHotkeyId, _settings.BackHotkey);
        UpdateHotkeyHint();
        FeedbackText.Text = _hotkeyRegistered ? DefaultFeedback : "快捷键已被其他程序占用，请在设置中更换";
        SettingsChanged?.Invoke();
    }
    private const string DefaultFeedback = "↑↓ 选择 · Enter 跳转 · Esc 关闭 · 右键管理";

    public void DisposeServices()
    {
        _refreshTimer.Stop();
        _browser.Dispose();
        _terminalTabs.Dispose();
        ReleaseHotkeys();
        _source?.RemoveHook(WndProc);
    }
}
