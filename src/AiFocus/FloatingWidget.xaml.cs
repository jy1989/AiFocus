using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using WinForms = System.Windows.Forms;

namespace AiFocus;

/// <summary>
/// Always-on-top badge showing whether any AI is waiting for the user. Never takes focus, and stays
/// a small dot and count until the mouse rests on it, so it does not cover the window being worked in.
/// </summary>
public partial class FloatingWidget : Window
{
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    private const int GwlStyle = -16, GwlExStyle = -20, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000;
    private const int WsPopup = unchecked((int)0x80000000);
    private const int DwmwaWindowCornerPreference = 33, DwmwcpRound = 2;

    private readonly MainWindow _main;
    private readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    /// <summary>Passing over the badge must not open it: the mouse has to rest here first.</summary>
    private readonly DispatcherTimer _expandTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private bool _hovering;
    private bool _expanded;
    /// <summary>Edges that stay put while the size changes, so the widget always grows into the screen.</summary>
    private double? _rightEdge, _bottomEdge;
    private bool _anchored, _placeAtDefault;
    private System.Windows.Size _compactSize;

    public FloatingWidget(MainWindow main)
    {
        InitializeComponent();
        _main = main;
        _main.SessionsRefreshed += Refresh;
        _collapseTimer.Tick += (_, _) => { _collapseTimer.Stop(); if (!_hovering) SetExpanded(false); };
        _expandTimer.Tick += (_, _) => { _expandTimer.Stop(); if (_hovering) SetExpanded(true); };
        SizeChanged += Window_SizeChanged;
        // The first layout ran before the popup style applied, while the wider minimum still held.
        // A real content size change makes WPF size the window again.
        ContentRendered += (_, _) =>
        {
            var height = Capsule.Height;
            Capsule.Height = height + 1;
            UpdateLayout();
            Capsule.Height = height;
        };
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            // Popup: Windows keeps overlapped windows at least ~136 px wide, far wider than the badge.
            SetWindowLong(handle, GwlStyle, GetWindowLong(handle, GwlStyle) | WsPopup);
            // Tool window: not in Alt+Tab / taskbar. No-activate: clicks never steal focus.
            SetWindowLong(handle, GwlExStyle, GetWindowLong(handle, GwlExStyle) | WsExToolWindow | WsExNoActivate);
            int corner = DwmwcpRound;
            DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref corner, sizeof(int));
        };
        using (var icon = typeof(App).Assembly.GetManifestResourceStream("AiFocus.ico")!)
            AppIcon.Source = new IconBitmapDecoder(icon, BitmapCreateOptions.None, BitmapCacheOption.OnLoad)
                .Frames.OrderBy(f => f.PixelWidth).First(f => f.PixelWidth >= 48);
        PlaceInitially();
        Refresh();
    }

    private void PlaceInitially()
    {
        var area = SystemParameters.WorkArea;
        double left = _main.Settings.WidgetLeft ?? area.Right;
        double top = _main.Settings.WidgetTop ?? area.Bottom;
        _placeAtDefault = _main.Settings.WidgetLeft == null || _main.Settings.WidgetTop == null;
        // A saved position may belong to a monitor that is no longer attached.
        var virtualRight = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
        if (left < SystemParameters.VirtualScreenLeft || left > virtualRight - 60 ||
            top < SystemParameters.VirtualScreenTop || top > virtualBottom - 40)
        {
            _placeAtDefault = true;
        }
        Left = left;
        Top = top;
    }

    private const double DefaultMargin = 24;

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_expanded) _compactSize = e.NewSize;
        if (!_anchored)
        {
            // The default corner is only known once the badge has its real size.
            if (_placeAtDefault)
            {
                var area = SystemParameters.WorkArea;
                Left = area.Right - DefaultMargin - e.NewSize.Width;
                Top = area.Bottom - DefaultMargin - e.NewSize.Height;
            }
            _anchored = true;
            UpdateAnchor();
            return;
        }
        if (_rightEdge is { } right) Left = right - e.NewSize.Width;
        if (_bottomEdge is { } bottom) Top = bottom - e.NewSize.Height;
    }

    /// <summary>Anchors to the nearer screen edges, so the list opens towards the middle of the screen.</summary>
    private void UpdateAnchor()
    {
        var screen = WinForms.Screen.FromHandle(new WindowInteropHelper(this).Handle).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        bool right = (Left + ActualWidth / 2) * dpi.DpiScaleX > screen.Left + screen.Width / 2;
        bool bottom = (Top + ActualHeight / 2) * dpi.DpiScaleY > screen.Top + screen.Height / 2;
        _rightEdge = right ? Left + ActualWidth : null;
        _bottomEdge = bottom ? Top + ActualHeight : null;
    }

    private void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        Summary.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        CountText.Visibility = !expanded && CountText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ActiveList.Visibility = expanded && ActiveList.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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
        StatusDot.Fill = Brush(waiting.Count > 0 ? (level == 2 ? "#0B8A84" : "#18B5AD")
            : stalled.Count > 0 ? (level == 2 ? "#C8501C" : "#F0803C")
            : pending.Count > 0 ? "#E6A92E" : running.Count > 0 ? "#3B82F6" : "#A9B6CE");
        SetEscalation(level);

        var active = waiting.Concat(stalled).Concat(pending).Concat(running).ToList();
        ActiveList.ItemsSource = active;
        int needYou = waiting.Count + stalled.Count + pending.Count;
        CountText.Text = needYou > 0 ? needYou.ToString() : "";
        ShowIdleIcon(needYou == 0, running.Count > 0);
        SetExpanded(_expanded);
    }

    /// <summary>Nothing needs the user: the app icon replaces the dot, which shrinks to a corner mark while AIs run.</summary>
    private void ShowIdleIcon(bool idle, bool running)
    {
        AppIcon.Visibility = idle ? Visibility.Visible : Visibility.Collapsed;
        StatusDot.Visibility = !idle || running ? Visibility.Visible : Visibility.Collapsed;
        StatusDot.HorizontalAlignment = idle ? System.Windows.HorizontalAlignment.Right : System.Windows.HorizontalAlignment.Center;
        StatusDot.VerticalAlignment = idle ? VerticalAlignment.Bottom : VerticalAlignment.Center;
        StatusDot.Margin = idle ? new Thickness(0, 0, 4, 4) : new Thickness(0);
        StatusDot.Stroke = idle ? (System.Windows.Media.Brush)FindResource("Paper") : null;
        StatusDot.StrokeThickness = idle ? 1.5 : 0;
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
            Summary.Children.Add(new TextBlock { Text = " · ", FontSize = 14, Foreground = Brush("#A9B6CE"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) });
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
            Summary.Children.Add(new TextBlock { Text = "+" + (group.Count - MaxIconsPerGroup), FontSize = 12, Foreground = Brush("#64728F"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0) });
    }

    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

    /// <summary>Demo screenshots: show the list as if hovered.</summary>
    public void ShowExpanded()
    {
        Refresh();
        SetExpanded(true);
    }

    private void Window_MouseEnter(object sender, MouseEventArgs e)
    {
        _hovering = true;
        _collapseTimer.Stop();
        if (!_expanded) _expandTimer.Start();
    }

    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        _hovering = false;
        _expandTimer.Stop();
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
        UpdateAnchor();
        // Saved as the compact badge's position, whatever size the widget has right now.
        _main.Settings.WidgetLeft = _rightEdge is { } right ? right - _compactSize.Width : Left;
        _main.Settings.WidgetTop = _bottomEdge is { } bottom ? bottom - _compactSize.Height : Top;
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
            SetExpanded(false);
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
