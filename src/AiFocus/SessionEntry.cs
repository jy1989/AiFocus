using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace AiFocus;

public sealed class SessionEntry : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string RuntimeKey { get; init; } = "";
    public string PersistenceKey { get; init; } = "";
    public string Provider { get; set; } = "";
    public string OriginalTitle { get; set; } = "";
    public string Kind { get; set; } = "";
    public string SourceName { get; set; } = "";
    public IntPtr WindowHandle { get; set; }
    public string? ProfileId { get; set; }
    public int BrowserWindowId { get; set; }
    public int BrowserTabId { get; set; }
    public string? Url { get; set; }
    public bool IsFocused { get; set; }
    public DateTime LastFocusedUtc { get; set; } = DateTime.MinValue;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    public string CustomTitle { get; set; } = "";
    public string Workspace { get; set; } = "";
    public bool Favorite { get; set; }
    public bool NeedsAttention { get; set; }
    /// <summary>"running" / "idle" when the window title exposes it (Claude Code terminals), otherwise "".</summary>
    public string Activity { get; set; } = "";
    /// <summary>Set when a session goes from running to stopped while AI Focus watches; cleared once the user opens it.</summary>
    public bool Completed { get; set; }
    public DateTime CompletedUtc { get; set; }
    /// <summary>
    /// Running but making no progress: "approval" = a tool call has waited a while (likely a permission
    /// prompt), "stuck" = nothing written for minutes. "" otherwise.
    /// </summary>
    public string Stall { get; set; } = "";
    public DateTime StallSinceUtc { get; set; }
    /// <summary>Working directory detected automatically (Claude Code / Codex cwd).</summary>
    public string ProjectPath { get; set; } = "";
    /// <summary>Folder name of ProjectPath; the user's Workspace overrides it in labels.</summary>
    public string Project => System.IO.Path.GetFileName(ProjectPath.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : ProjectPath;
    /// <summary>Latest AI reply, when the source exposes it (Claude Code / Codex).</summary>
    public string Preview { get; set; } = "";
    public string? ThreadId { get; set; }
    /// <summary>Claude Code / Codex transcript (JSONL) backing the conversation view.</summary>
    public string TranscriptPath { get; set; } = "";
    public int TerminalTabIndex { get; set; }
    /// <summary>Web session: the selected tab of its browser window (so the window shows it).</summary>
    public bool TabActive { get; set; }
    /// <summary>Real app icon or favicon; null falls back to the Icon glyph.</summary>
    public System.Windows.Media.ImageSource? IconImage { get; set; }
    public System.Windows.Media.Brush IconBrush => Provider is "Claude" or "Claude Code" ? ColorBrush("#D97757") : ColorBrush("#8C8880");
    public string DisplayTitle => string.IsNullOrWhiteSpace(CustomTitle) ? OriginalTitle : CustomTitle;
    public string ProjectLabel => string.IsNullOrWhiteSpace(Workspace) ? Project : Workspace;
    public string Subtitle => string.Join("  ·  ", new[] { ProjectLabel, Provider, SourceName }.Where(s => !string.IsNullOrWhiteSpace(s)));
    /// <summary>Second line of a tile. The group header and icon already name the tool, so it only
    /// falls back to the tool name when there is nothing else to show; the full source is in the tooltip.</summary>
    public string TileMeta => string.Join("  ·  ", new[] { ProjectLabel, LastUsedText }.Where(s => !string.IsNullOrWhiteSpace(s)))
        is { Length: > 0 } meta ? meta : Provider;
    /// <summary>First meaningful sentence of the latest AI reply, Markdown stripped; "" when unknown.</summary>
    public string Brief => MakeBrief(Preview);
    public System.Windows.Visibility BriefVisibility => (Completed || Stall.Length > 0) && Brief.Length > 0
        ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public string ToolTipText => DisplayTitle + "\n" + Subtitle
        + (ProjectPath.Length > 0 ? "\n" + ProjectPath : "")
        + (Preview.Length > 0 ? "\n\n" + (Preview.Length > 400 ? Preview[..400] + "…" : Preview) : "");
    /// <summary>Floating-widget second line: the icon already says which tool, so lead with the folder.</summary>
    public string WidgetMeta => string.Join("  ·  ", new[] { ProjectLabel.Length > 0 ? ProjectLabel : Provider, WaitText }.Where(x => x.Length > 0));
    public bool WaitingForUser => NeedsAttention || Completed || Stall.Length > 0;

    // ---- Ordering inside a group: tiles move only when their state changes, never because the
    //      user switched windows, so each one keeps its place in spatial memory. ----
    private static long _nextSequence;
    /// <summary>Discovery order; the stable tiebreaker for everything that is not waiting.</summary>
    public long Sequence { get; } = Interlocked.Increment(ref _nextSequence);
    /// <summary>0 = needs the user, 1 = running, 2 = everything else.</summary>
    public int SortTier => WaitingForUser ? 0 : Activity == "running" ? 1 : 2;
    /// <summary>Within tier 0: waiting longest first (same order as "送过去"); manual to-dos last.</summary>
    public DateTime WaitSortUtc => Completed && CompletedUtc != default ? CompletedUtc
        : Stall.Length > 0 && StallSinceUtc != default ? StallSinceUtc
        : DateTime.MaxValue;
    /// <summary>Favourites lead among idle tiles only; waiting and running order by urgency and age.</summary>
    public bool FavoriteFirst => Favorite && SortTier == 2;
    public bool IsBrowser => Kind is "web" or "browser-window";
    public int GroupOrder => IsBrowser ? 2
        : Provider is "Claude" or "Claude Code" ? 0
        : Provider == "Codex" ? 1
        : Kind == "editor" ? 3
        : Kind is "terminal" or "terminal-tab" ? 5
        : 4;
    public string GroupName => GroupOrder switch
    {
        0 => "Claude",
        1 => "Codex",
        2 => "浏览器 · 网页 AI",
        3 => "编辑器",
        5 => "其他 AI 终端",
        _ => "AI 桌面应用"
    };
    public string Icon => Provider switch
    {
        "ChatGPT" => "◎",
        "Claude" or "Claude Code" => "✳",
        "Muse" => "◈",
        "Codex" => "⌘",
        "Gemini" or "Gemini CLI" or "Google AI Studio" or "NotebookLM" => "✦",
        _ => IsBrowser ? "◉" : Kind is "terminal" or "terminal-tab" ? "⌁" : Kind == "editor" ? "⟨⟩" : "▣"
    };
    // Only meaningful states get a badge; plain "open" windows show nothing.
    public string StateText => NeedsAttention ? "待处理"
        : Completed ? "待回复"
        : Stall == "approval" ? "等你确认?"
        : Stall == "stuck" ? "可能卡住"
        : Activity == "running" ? "运行中"
        : IsFocused ? "当前使用"
        : "";
    public System.Windows.Visibility StateVisibility => StateText.Length > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    // Muted tones: only states that concern the user carry colour, everything else stays neutral.
    public System.Windows.Media.Brush StateBackground => StateText switch
    {
        "待处理" => ColorBrush("#F6EAD3"),
        "待回复" => ColorBrush("#E1EEE5"),
        "等你确认?" or "可能卡住" => ColorBrush("#F7E4D8"),
        "运行中" => ColorBrush("#E3E9F1"),
        _ => ColorBrush("#EAE8E2")
    };
    public System.Windows.Media.Brush StateForeground => StateText switch
    {
        "待处理" => ColorBrush("#8A5A12"),
        "待回复" => ColorBrush("#2F6B4A"),
        "等你确认?" or "可能卡住" => ColorBrush("#9A4A1C"),
        "运行中" => ColorBrush("#3E5F86"),
        _ => ColorBrush("#7A7F87")
    };
    /// <summary>Colour strip at the row's left edge; transparent unless the session needs the user.</summary>
    public System.Windows.Media.Brush AccentBrush => NeedsAttention ? ColorBrush("#D9A44A")
        : Completed ? ColorBrush("#7FB08F")
        : Stall.Length > 0 ? ColorBrush("#D98A5F")
        : Activity == "running" ? ColorBrush("#8FA6C4")
        : System.Windows.Media.Brushes.Transparent;
    public string FavoriteSymbol => Favorite ? "★" : "☆";
    public string AttentionSymbol => NeedsAttention ? "✓ 已标记" : "设为待处理";
    /// <summary>Short status for the floating widget, e.g. "等了 12 分钟".</summary>
    public string WaitText => Completed && CompletedUtc != default ? (Ago(CompletedUtc) is { Length: > 0 } ago ? "等了 " + ago : "刚回复")
        : NeedsAttention ? "待处理"
        : Stall.Length > 0 ? StateText + (Ago(StallSinceUtc) is { Length: > 0 } stalled ? " · " + stalled + "无进展" : "")
        : Activity == "running" ? "运行中"
        : "";

    private static readonly char[] MarkdownLead = { '#', '>', '*', '-', '+', '•', ' ', '\t' };
    /// <summary>First readable line of a reply: skips code fences and tables, strips Markdown, keeps ~70 chars.</summary>
    public static string MakeBrief(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        bool inFence = false;
        string heading = "";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal)) { inFence = !inFence; continue; }
            if (inFence || line.Length == 0 || line.StartsWith('|') || line.All(c => c is '-' or '=' or '*' or '_' or ' ')) continue;
            bool isHeading = line.StartsWith('#');
            line = line.TrimStart(MarkdownLead);
            // Numbered list marker "1. ".
            int dot = line.IndexOf(". ", StringComparison.Ordinal);
            if (dot is > 0 and <= 3 && line[..dot].All(char.IsDigit)) line = line[(dot + 2)..];
            line = line.Replace("**", "").Replace("__", "").Replace("`", "").Trim();
            if (line.Length == 0) continue;
            // Headings like "## 完成" say little; prefer the first sentence under them.
            if (isHeading) { if (heading.Length == 0) heading = line; continue; }
            return Clip(line);
        }
        return Clip(heading);

        static string Clip(string s) => s.Length > 70 ? s[..70] + "…" : s;
    }
    private static string Ago(DateTime utc)
    {
        var delta = DateTime.UtcNow - utc;
        if (delta.TotalMinutes < 1) return "";
        if (delta.TotalMinutes < 60) return (int)delta.TotalMinutes + " 分钟";
        return (int)delta.TotalHours + " 小时";
    }
    public string LastUsedText
    {
        get
        {
            if (LastFocusedUtc == DateTime.MinValue) return "";
            var delta = DateTime.UtcNow - LastFocusedUtc;
            if (delta.TotalMinutes < 1) return "刚刚";
            if (delta.TotalMinutes < 60) return ((int)delta.TotalMinutes) + " 分钟前";
            if (delta.TotalHours < 24) return ((int)delta.TotalHours) + " 小时前";
            return ((int)delta.TotalDays) + " 天前";
        }
    }
    private static System.Windows.Media.Brush ColorBrush(string hex) => new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
    public void Changed([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name == null || name == nameof(IsFocused) || name == nameof(NeedsAttention))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StateText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StateVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AccentBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StateBackground)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StateForeground)));
        }
    }
}
