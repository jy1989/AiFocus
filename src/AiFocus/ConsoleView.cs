using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace AiFocus;

/// <summary>
/// "控制台" view: every session as a large card. A session that is what its window currently shows
/// gets a live DWM thumbnail (the same mechanism as Alt+Tab / taskbar previews, composited by the
/// GPU, no screenshots); one in a background tab or a minimised window shows its latest turns.
/// Thumbnails are drawn on top of the host window, so the view never scrolls: at most MaxCards.
/// </summary>
public sealed class ConsoleView
{
    public const int MaxCards = 9;

    private readonly Window _host;
    private readonly Grid _pane;
    private readonly Func<SessionEntry, Task> _activate;
    private readonly Func<SessionEntry, IntPtr> _liveWindow;
    private readonly Func<SessionEntry, Task<IReadOnlyList<ChatMessage>?>> _readConversation;
    private readonly List<Card> _cards = new();
    private string _layoutSignature = "";
    private bool _updating;

    private sealed class Card
    {
        public required SessionEntry Session { get; init; }
        public IntPtr Source { get; init; }
        public IntPtr Thumbnail { get; set; }
        public required Border Body { get; init; }
        public StackPanel? Messages { get; init; }
        public string MessagesSignature { get; set; } = "";
    }

    public ConsoleView(Window host, Grid pane, Func<SessionEntry, Task> activate,
        Func<SessionEntry, IntPtr> liveWindow, Func<SessionEntry, Task<IReadOnlyList<ChatMessage>?>> readConversation)
    {
        _host = host;
        _pane = pane;
        _activate = activate;
        _liveWindow = liveWindow;
        _readConversation = readConversation;
        _pane.SizeChanged += (_, _) => Reposition();
    }

    /// <summary>Called on every refresh while the view is open; rebuilds only when the card set changed.</summary>
    public async void Update(IReadOnlyList<SessionEntry> ordered)
    {
        if (_updating) return;
        _updating = true;
        try
        {
            var plan = ordered.Take(MaxCards).Select(s => (Session: s, Source: _liveWindow(s))).ToList();
            var signature = ordered.Count + "|" + string.Join("|", plan.Select(p => p.Session.RuntimeKey + ":" + p.Source));
            if (signature != _layoutSignature)
            {
                Rebuild(plan, ordered.Count);
                _layoutSignature = signature;
            }
            foreach (var card in _cards.Where(c => c.Messages != null).ToList())
            {
                var messages = await _readConversation(card.Session);
                if (!_cards.Contains(card)) continue; // Rebuilt meanwhile.
                ShowMessages(card, messages);
            }
            Reposition();
        }
        finally { _updating = false; }
    }

    /// <summary>Drops all cards and thumbnails (view closed or panel hidden: no GPU work in the background).</summary>
    public void Clear()
    {
        foreach (var card in _cards) if (card.Thumbnail != IntPtr.Zero) DwmUnregisterThumbnail(card.Thumbnail);
        _cards.Clear();
        _pane.Children.Clear();
        _layoutSignature = "";
    }

    private void Rebuild(List<(SessionEntry Session, IntPtr Source)> plan, int total)
    {
        Clear();
        if (plan.Count == 0)
        {
            _pane.Children.Add(new TextBlock
            {
                Text = "没有会话", FontSize = 15, Foreground = (Brush)_host.FindResource("InkMuted"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            });
            return;
        }
        int columns = plan.Count <= 1 ? 1 : plan.Count <= 4 ? 2 : 3;
        var grid = new UniformGrid { Columns = columns, Rows = (int)Math.Ceiling(plan.Count / (double)columns) };
        var host = new WindowInteropHelper(_host).Handle;
        foreach (var (session, source) in plan)
        {
            var (element, card) = BuildCard(session, source);
            if (source != IntPtr.Zero && DwmRegisterThumbnail(host, source, out var thumbnail) == 0) card.Thumbnail = thumbnail;
            _cards.Add(card);
            grid.Children.Add(element);
        }
        _pane.Children.Add(grid);
        if (total > plan.Count)
        {
            _pane.Children.Add(new TextBlock
            {
                Text = $"另有 {total - plan.Count} 个会话，切回列表查看", FontSize = 12.5,
                Foreground = (Brush)_host.FindResource("InkMuted"),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 8, -18)
            });
        }
    }

    private (FrameworkElement, Card) BuildCard(SessionEntry session, IntPtr source)
    {
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var icon = new ContentControl { Content = session, ContentTemplate = (DataTemplate)_host.FindResource("SessionIcon"), Focusable = false, Margin = new Thickness(0, 0, 8, 0) };
        DockPanel.SetDock(icon, Dock.Left);
        header.Children.Add(icon);
        var badge = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 2, 8, 2), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        badge.SetBinding(Border.BackgroundProperty, nameof(SessionEntry.StateBackground));
        badge.SetBinding(UIElement.VisibilityProperty, nameof(SessionEntry.StateVisibility));
        var badgeText = new TextBlock { FontSize = 12 };
        badgeText.SetBinding(TextBlock.TextProperty, nameof(SessionEntry.StateText));
        badgeText.SetBinding(TextBlock.ForegroundProperty, nameof(SessionEntry.StateForeground));
        badge.Child = badgeText;
        DockPanel.SetDock(badge, Dock.Right);
        header.Children.Add(badge);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock { FontSize = 14.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        title.SetBinding(TextBlock.TextProperty, nameof(SessionEntry.DisplayTitle));
        var meta = new TextBlock { FontSize = 12, Foreground = (Brush)_host.FindResource("InkMuted"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
        meta.SetBinding(TextBlock.TextProperty, nameof(SessionEntry.WidgetMeta));
        titles.Children.Add(title);
        titles.Children.Add(meta);
        header.Children.Add(titles);

        StackPanel? messages = null;
        var body = new Border { CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xEC, 0xE6)), ClipToBounds = true };
        if (source == IntPtr.Zero)
        {
            // Latest turns, newest at the bottom like the chat itself; older ones are clipped at the top.
            body.Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF6, 0xF2));
            messages = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10, 6, 10, 8) };
            body.Child = messages;
        }

        var layout = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);
        layout.Children.Add(body);
        var card = new Border
        {
            DataContext = session, Margin = new Thickness(6), Padding = new Thickness(12, 10, 12, 12), CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromRgb(0xFB, 0xFA, 0xF7)), BorderThickness = new Thickness(3, 0, 0, 0),
            Cursor = System.Windows.Input.Cursors.Hand, Child = layout, ToolTip = "单击切换过去"
        };
        card.SetBinding(Border.BorderBrushProperty, nameof(SessionEntry.AccentBrush));
        card.MouseLeftButtonUp += (_, _) => _ = _activate(session);
        return (card, new Card { Session = session, Source = source, Body = body, Messages = messages });
    }

    private void ShowMessages(Card card, IReadOnlyList<ChatMessage>? messages)
    {
        var recent = messages?.Skip(Math.Max(0, messages.Count - 4)).ToList();
        var signature = recent == null ? "none:" + card.Session.Brief : string.Join("\u0001", recent.Select(m => m.FromUser + m.Text.Length.ToString() + m.Text[..Math.Min(30, m.Text.Length)]));
        if (signature == card.MessagesSignature) return;
        card.MessagesSignature = signature;
        var panel = card.Messages!;
        panel.Children.Clear();
        var muted = (Brush)_host.FindResource("InkMuted");
        if (recent == null || recent.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = card.Session.Brief.Length > 0 ? card.Session.Brief : "后台标签或窗口已最小化，没有可预览的内容",
                FontSize = 12.5, Foreground = muted, TextWrapping = TextWrapping.Wrap
            });
            return;
        }
        foreach (var m in recent)
        {
            // Long replies are cut; the bottom of the card shows the newest turn, older ones clip at the top.
            var text = m.Text.Length > 1500 ? m.Text[..1500] + "…" : m.Text;
            panel.Children.Add(new TextBlock
            {
                Text = m.RoleLabel, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = muted, Margin = new Thickness(0, 8, 0, 1)
            });
            var body = MarkdownView.Render(text, 12.5);
            if (m.FromUser) TextElement.SetForeground(body, (Brush)_host.FindResource("InkSoft"));
            panel.Children.Add(body);
        }
    }

    /// <summary>Places each live thumbnail over its card body, keeping the source's aspect ratio.</summary>
    public void Reposition()
    {
        if (_cards.Count == 0) return;
        _host.Dispatcher.BeginInvoke(() =>
        {
            var dpi = VisualTreeHelper.GetDpi(_host);
            foreach (var card in _cards.Where(c => c.Thumbnail != IntPtr.Zero))
            {
                var props = new ThumbnailProperties
                {
                    Flags = TnpRectDestination | TnpVisible | TnpOpacity | TnpSourceClientAreaOnly,
                    Opacity = 255, SourceClientAreaOnly = true
                };
                if (card.Body.ActualWidth < 4 || card.Body.ActualHeight < 4 || !card.Body.IsVisible)
                {
                    props.Visible = false;
                }
                else
                {
                    var origin = card.Body.TransformToAncestor(_host).Transform(new Point(0, 0));
                    double left = origin.X * dpi.DpiScaleX, top = origin.Y * dpi.DpiScaleY;
                    double width = card.Body.ActualWidth * dpi.DpiScaleX, height = card.Body.ActualHeight * dpi.DpiScaleY;
                    if (DwmQueryThumbnailSourceSize(card.Thumbnail, out var size) == 0 && size.X > 0 && size.Y > 0)
                    {
                        // Letterbox: fit the whole source window inside the body.
                        double scale = Math.Min(width / size.X, height / size.Y);
                        double w = size.X * scale, h = size.Y * scale;
                        left += (width - w) / 2;
                        top += (height - h) / 2;
                        width = w;
                        height = h;
                    }
                    props.Destination = new NativeRect { Left = (int)left, Top = (int)top, Right = (int)(left + width), Bottom = (int)(top + height) };
                    props.Visible = true;
                }
                DwmUpdateThumbnailProperties(card.Thumbnail, ref props);
            }
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private const uint TnpRectDestination = 0x1, TnpOpacity = 0x4, TnpVisible = 0x8, TnpSourceClientAreaOnly = 0x10;

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ThumbnailProperties
    {
        public uint Flags;
        public NativeRect Destination;
        public NativeRect Source;
        public byte Opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool Visible;
        [MarshalAs(UnmanagedType.Bool)] public bool SourceClientAreaOnly;
    }

    [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(IntPtr destination, IntPtr source, out IntPtr thumbnail);
    [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(IntPtr thumbnail);
    [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail, ref ThumbnailProperties properties);
    [DllImport("dwmapi.dll")] private static extern int DwmQueryThumbnailSourceSize(IntPtr thumbnail, out NativeSize size);
}
