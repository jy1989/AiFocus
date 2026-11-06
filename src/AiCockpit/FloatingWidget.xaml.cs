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

namespace AiCockpit;

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
        AddGroup(waiting, "待回复");
        AddGroup(stalled, "等你?");
        AddGroup(pending, "待处理");
        AddGroup(running, "运行中");
        if (Summary.Children.Count == 0) Summary.Children.Add(new TextBlock { Text = "AI 都很安静", FontSize = 14 });
        StatusDot.Fill = Brush(waiting.Count > 0 ? "#6FA582" : stalled.Count > 0 ? "#D98A5F" : pending.Count > 0 ? "#D9A44A" : running.Count > 0 ? "#8FA6C4" : "#C2BEB5");

        var active = waiting.Concat(stalled).Concat(pending).Concat(running).ToList();
        ActiveList.ItemsSource = active;
        ExpandHint.Visibility = active.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (active.Count == 0) ActiveList.Visibility = Visibility.Collapsed;
    }

    private const int MaxIconsPerGroup = 3;

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

    private void Capsule_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var before = new Point(Left, Top);
        DragMove();
        if (Math.Abs(Left - before.X) < 3 && Math.Abs(Top - before.Y) < 3)
        {
            _main.ShowPanel();
            return;
        }
        _main.Settings.WidgetLeft = Left;
        _main.Settings.WidgetTop = Top;
        _main.Settings.Save();
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
    private void Hide_Click(object sender, RoutedEventArgs e)
    {
        _main.Settings.ShowWidget = false;
        _main.Settings.Save();
        Hide();
    }
}
