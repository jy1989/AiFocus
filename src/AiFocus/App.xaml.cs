using System.Windows;
using Microsoft.Toolkit.Uwp.Notifications;
using WinForms = System.Windows.Forms;

namespace AiFocus;

public partial class App : System.Windows.Application
{
    private WinForms.NotifyIcon? _tray;
    private Mutex? _singleInstance;
    private MainWindow? _window;
    private FloatingWidget? _widget;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Demo.Parse(e.Args);
        if (Demo.Enabled)
        {
            // Screenshot instance: own data folder, made-up sessions, no tray, hotkeys or notifications.
            AppPaths.UseDemoFolder();
            Loc.Init(Demo.Language ?? "auto");
            _window = new MainWindow();
            _window.InitializeInvisible();
            if (Demo.View == "widget")
            {
                _widget = new FloatingWidget(_window);
                _widget.Show();
                _widget.ShowExpanded();
            }
            else _window.ShowPanel();
            return;
        }
        AppPaths.MigrateFromAiCockpit();
        Loc.Init(AppSettings.Load().Language);
        try { AutoStart.RefreshPath(); } catch (UnauthorizedAccessException) { }
        // The extension has a fixed ID, so the bridge can be registered without asking the user.
        Task.Run(NativeHost.EnsureRegistered);
        _singleInstance = new Mutex(initiallyOwned: true, name: @"Local\AiFocus.SingleInstance", createdNew: out bool first);
        if (!first)
        {
            var hotkey = HotkeyText.Pretty(AppSettings.Load().Hotkey);
            System.Windows.MessageBox.Show(Loc.T("AI Focus 已在运行，请按 " + hotkey + " 或点击托盘图标。", "AI Focus is already running. Press " + hotkey + " or click the tray icon."), "AI Focus");
            Shutdown();
            return;
        }
        _window = new MainWindow();
        _window.InitializeInvisible();
        _widget = new FloatingWidget(_window);
        if (_window.Settings.ShowWidget) _widget.Show();

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(Loc.T("打开 AI Focus", "Open AI Focus"), null, (_, _) => Dispatcher.Invoke(() => _window?.ShowPanel()));
        menu.Items.Add(Loc.MenuNext, null, (_, _) => Dispatcher.Invoke(() => _window?.JumpToNextWaitingAsync()));
        menu.Items.Add(Loc.MenuBack, null, (_, _) => Dispatcher.Invoke(() => _window?.ReturnToOwnWindow()));
        var widgetItem = new WinForms.ToolStripMenuItem(Loc.T("显示悬浮窗", "Show widget")) { CheckOnClick = true };
        widgetItem.Click += (_, _) => Dispatcher.Invoke(() =>
        {
            _window.Settings.ShowWidget = widgetItem.Checked;
            _window.Settings.Save();
            ApplyWidgetVisibility();
        });
        menu.Opening += (_, _) => widgetItem.Checked = _window.Settings.ShowWidget;
        menu.Items.Add(widgetItem);
        menu.Items.Add(Loc.T("设置…", "Settings…"), null, (_, _) => Dispatcher.Invoke(() => _window?.ShowSettings()));
        menu.Items.Add(Loc.T("退出", "Exit"), null, (_, _) => Dispatcher.Invoke(Shutdown));
        _tray = new WinForms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true
        };
        UpdateTrayText();
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(() => _window?.ShowPanel());
        // Native toasts instead of tray balloons: clicking a toast activates this process and Windows
        // lets it bring a window to the front. A balloon click leaves the foreground with the shell's
        // notification popup, so the jump was refused ("目标窗口可能已经关闭").
        ToastNotificationManagerCompat.OnActivated += toast => Dispatcher.InvokeAsync(async () =>
        {
            Log.Write("toast clicked: " + toast.Argument);
            var args = ToastArguments.Parse(toast.Argument);
            if (_window != null && args.TryGetValue(SessionArgument, out var key)) await _window.ActivateByKeyAsync(key);
        });
        _window.SettingsChanged += UpdateTrayText;
        _window.SettingsChanged += ApplyWidgetVisibility;
        _window.RestartRequested += () =>
        {
            // Free the single-instance lock first, or the new process would think we are still running.
            try { _singleInstance?.ReleaseMutex(); } catch (ApplicationException) { }
            _singleInstance?.Dispose();
            _singleInstance = null;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true });
            Shutdown();
        };
        _window.Notice += text => new ToastContentBuilder().AddText(text).Show(t => t.ExpirationTime = DateTimeOffset.Now.AddMinutes(1));
        _window.SessionCompleted += s => Notify(s, Loc.T("待回复", "replied"), s.Brief.Length > 0 ? s.Brief : Loc.T("点击跳转查看", "Click to jump there"));
        _window.SessionStalled += s => Notify(s, s.Stall == "approval" ? Loc.T("可能在等你确认", "may need your approval") : Loc.T("可能卡住了", "may be stuck"),
            s.Stall == "approval" ? Loc.T("有一个操作一直没有结果，可能需要你批准", "A step has been pending for a while; it may need your approval") : Loc.T("几分钟没有任何进展", "No progress for several minutes"));
    }
    private const string SessionArgument = "session";

    private void Notify(SessionEntry s, string state, string body)
    {
        if (_window == null || !_window.Settings.NotifyOnComplete) return;
        Log.Write($"notify {state} {s.Kind} \"{s.DisplayTitle}\"");
        var title = s.DisplayTitle.Length > 50 ? s.DisplayTitle[..50] + "…" : s.DisplayTitle;
        var where = s.ProjectLabel.Length > 0 ? s.ProjectLabel + " · " : "";
        new ToastContentBuilder()
            .AddArgument(SessionArgument, s.RuntimeKey)
            .AddText(s.Provider + " " + state + " · " + title)
            .AddText(where + body)
            .Show(toast =>
            {
                // One toast per session: a newer state replaces the older one instead of piling up.
                toast.Tag = ((uint)s.RuntimeKey.GetHashCode()).ToString();
                toast.Group = "sessions";
                toast.ExpirationTime = DateTimeOffset.Now.AddHours(8);
            });
    }
    private void ApplyWidgetVisibility()
    {
        if (_widget == null || _window == null) return;
        if (_window.Settings.ShowWidget) _widget.Show();
        else _widget.Hide();
    }
    private void UpdateTrayText()
    {
        // NotifyIcon.Text is limited to 63 characters.
        if (_tray != null && _window != null) _tray.Text = "AI Focus - " + HotkeyText.Pretty(_window.Settings.Hotkey);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _window?.DisposeServices();
        try { _singleInstance?.ReleaseMutex(); } catch (ApplicationException) { }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
