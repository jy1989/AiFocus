using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace AiFocus;

/// <summary>
/// Always-on-top capsule showing whether any AI is waiting for the user. Never takes focus,
/// so glancing at or clicking it does not disturb the window being worked in.
/// </summary>
public partial class FloatingWidget : Window
{
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    private const int GwlExStyle = -20, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000;
    private const int DwmwaWindowCornerPreference = 33, DwmwcpRound = 2;

    private readonly MainWindow _main;
    private readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private bool _hovering;

    public FloatingWidget(MainWindow main)
    {
        InitializeComponent();
        _main = main;
        _main.SessionsRefreshed += Refresh;
        _collapseTimer.Tick += (_, _) => { _collapseTimer.Stop(); if (!_hovering) ActiveList.Visibility = Visibility.Collapsed; };
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            // Tool window: not in Alt+Tab / taskbar. No-activate: clicks never steal focus.
            SetWindowLong(handle, GwlExStyle, GetWindowLong(handle, GwlExStyle) | WsExToolWindow | WsExNoActivate);
            int corner = DwmwcpRound;
            DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref corner, sizeof(int));
        };
        PlaceInitially();
        Refresh();
    }

    private void PlaceInitially()
    {
        var area = SystemParameters.WorkArea;
        double left = _main.Settings.WidgetLeft ?? area.Right - Width - 24;
        double top = _main.Settings.WidgetTop ?? area.Top + 24;
        // A saved position may belong to a monitor that is no longer attached.
        var virtualRight = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
        if (left < SystemParameters.VirtualScreenLeft || left > virtualRight - 60 ||
            top < SystemParameters.VirtualScreenTop || top > virtualBottom - 40)
        {
            left = area.Right - Width - 24;
            top = area.Top + 24;
        }
        Left = left;
        Top = top;
    }

    public void Refresh()
    {
        var sessions = _main.Sessions;
        // Waiting longest first, then manual to-dos, then whatever is still running.
        var waiting = sessions.Where(s => s.Completed).OrderBy(s => s.CompletedUtc).ToList();
        var stalled = sessions.Where(s => s.Stall.Length > 0 && !s.Completed && !s.NeedsAttention).OrderBy(s => s.StallSinceUtc).ToList();
        var pending = sessions.Where(s => s.NeedsAttention && !s.Completed).ToList();
        var running = sessions.Where(s => s.Activity == "running" && !s.WaitingForUser).ToList();

        Summary.Children.Clear();
        AddGroup(waiting, Loc.T("待回复", "replied"));
        AddGroup(stalled, Loc.T("等你?", "need you?"));
        AddGroup(pending, Loc.T("待处理", "to do"));
        AddGroup(running, Loc.T("运行中", "running"));
        if (Summary.Children.Count == 0) Summary.Children.Add(new TextBlock { Text = Loc.AllQuiet, FontSize = 14 });
        // How long the oldest thing has been waiting on the user drives a calm escalation of the dot.
        var oldest = waiting.Select(s => s.CompletedUtc).Concat(stalled.Select(s => s.StallSinceUtc))
            .Where(t => t != default).DefaultIfEmpty(DateTime.UtcNow).Min();
        var waitedFor = DateTime.UtcNow - oldest;
        int level = waitedFor >= LongWait ? 2 : waitedFor >= Wait ? 1 : 0;
        StatusDot.Fill = Brush(waiting.Count > 0 ? (level == 2 ? "#3E7D57" : "#6FA582")
            : stalled.Count > 0 ? (level == 2 ? "#B8562A" : "#D98A5F")
            : pending.Count > 0 ? "#D9A44A" : running.Count > 0 ? "#8FA6C4" : "#C2BEB5");
        SetEscalation(level);

        var active = waiting.Concat(stalled).Concat(pending).Concat(running).ToList();
        ActiveList.ItemsSource = active;
        ExpandHint.Visibility = active.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (active.Count == 0) ActiveList.Visibility = Visibility.Collapsed;
    }

    private const int MaxIconsPerGroup = 2;
    private static readonly TimeSpan Wait = TimeSpan.FromMinutes(10), LongWait = TimeSpan.FromMinutes(30);
    private int _escalation = -1;

    /// <summary>
    /// 0: still. 1 (waiting ≥ 10 min): the dot breathes slowly. 2 (≥ 30 min): deeper colour, larger
    /// dot, quicker breathing. No sound, no popup: noticeable in peripheral vision, never jarring.
    /// </summary>
    private void SetEscalation(int level)
    {
        if (level == _escalation) return; // Refresh runs every few seconds; don't restart the animation.
        _escalation = level;
        StatusDot.BeginAnimation(OpacityProperty, null);
        StatusDot.Opacity = 1;
        StatusDot.Width = StatusDot.Height = level == 2 ? 11 : 9;
        if (level == 0) return;
        var breathe = new System.Windows.Media.Animation.DoubleAnimation(1, 0.3, TimeSpan.FromSeconds(level == 2 ? 0.9 : 1.6))
        {
            AutoReverse = true,
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
            EasingFunction = new System.Windows.Media.Animation.SineEase()
        };
        StatusDot.BeginAnimation(OpacityProperty, breathe);
    }

    /// <summary>"N 个运行中" followed by the sessions' icons, so the capsule shows *which* AIs at a glance.</summary>
    private void AddGroup(IReadOnlyList<SessionEntry> group, string label)
    {
        if (group.Count == 0) return;
        if (Summary.Children.Count > 0)
            Summary.Children.Add(new TextBlock { Text = " · ", FontSize = 14, Foreground = Brush("#C2BEB5"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) });
        Summary.Children.Add(new TextBlock { Text = group.Count + " " + label, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        foreach (var s in group.Take(MaxIconsPerGroup))
        {
            Summary.Children.Add(new ContentControl
            {
                Content = s, ContentTemplate = (DataTemplate)FindResource("SessionIcon"), Focusable = false,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(1, 0, 1, 0),
                LayoutTransform = new ScaleTransform(0.85, 0.85), ToolTip = s.DisplayTitle
            });
        }
        if (group.Count > MaxIconsPerGroup)
            Summary.Children.Add(new TextBlock { Text = "+" + (group.Count - MaxIconsPerGroup), FontSize = 12, Foreground = Brush("#8A8780"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0) });
    }

    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

    /// <summary>Demo screenshots: show the list as if hovered.</summary>
    public void ShowExpanded()
    {
        Refresh();
        if (ActiveList.Items.Count > 0) ActiveList.Visibility = Visibility.Visible;
    }

    private void Window_MouseEnter(object sender, MouseEventArgs e)
    {
        _hovering = true;
        _collapseTimer.Stop();
        if (ActiveList.Items.Count > 0) ActiveList.Visibility = Visibility.Visible;
    }

    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        _hovering = false;
        _collapseTimer.Start();
    }

    // Click vs drag is decided here, not by DragMove: entering the system move loop on every press
    // made a plain click wait for Windows to give up on the drag (~1 s) before the panel opened.
    private Point? _pressedAt;

    private void Capsule_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressedAt = e.GetPosition(this);
        Capsule.CaptureMouse();
        e.Handled = true;
    }

    private void Capsule_MouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedAt is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(this) - start;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        // A real drag: hand over to the system move loop, then remember where it was dropped.
        _pressedAt = null;
        Capsule.ReleaseMouseCapture();
        DragMove();
        _main.Settings.WidgetLeft = Left;
        _main.Settings.WidgetTop = Top;
        _main.Settings.Save();
    }

    private void Capsule_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_pressedAt == null) return;
        _pressedAt = null;
        Capsule.ReleaseMouseCapture();
        _main.ShowPanel();
    }

    private void Row_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is SessionEntry s)
        {
            ActiveList.Visibility = Visibility.Collapsed;
            _ = _main.ActivateFromNotificationAsync(s);
        }
    }

    private void OpenPanel_Click(object sender, RoutedEventArgs e) => _main.ShowPanel();
    private void Next_Click(object sender, RoutedEventArgs e) => _ = _main.JumpToNextWaitingAsync();
    private void Back_Click(object sender, RoutedEventArgs e) => _main.ReturnToOwnWindow();
    private void Hide_Click(object sender, RoutedEventArgs e)
    {
        _main.Settings.ShowWidget = false;
        _main.Settings.Save();
        Hide();
    }
}
