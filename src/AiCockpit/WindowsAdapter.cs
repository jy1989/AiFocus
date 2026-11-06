using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AiCockpit;

public static class WindowsAdapter
{
    private delegate bool EnumWindowsDelegate(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsDelegate cb, IntPtr p);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int nMaxCount);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    private const uint GwOwner = 4;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x80, WsExAppWindow = 0x40000;
    private const int DwmwaCloaked = 14;

    /// <summary>Activity is "running" / "idle" for terminals whose title exposes it (Claude Code), otherwise "".</summary>
    public sealed record WindowInfo(IntPtr Handle, string Title, string Process, string Provider, string Kind, string Activity = "", string ExecutablePath = "")
    {
        /// <summary>Windows Terminal: several tabs per window, readable through UI Automation (see TerminalTabs).</summary>
        public bool IsTabbedTerminal => Process.Equals("WindowsTerminal", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "vivaldi", "opera", "arc", "chromium",
        "360chrome", "360se", "qqbrowser", "sogouexplorer", "liebao", "2345explorer"
    };
    private static readonly HashSet<string> Terminals = new(StringComparer.OrdinalIgnoreCase)
    {
        "windowsterminal", "wezterm-gui", "alacritty", "tabby", "powershell", "pwsh", "cmd",
        "mintty", "conemu64", "conemu", "warp", "hyper", "kitty", "rio", "putty", "mobaxterm", "termius", "xshell"
    };
    // Desktop clients matched by process name (lower-case, spaces kept as reported by Windows).
    private static readonly (string process, string provider, string kind)[] DesktopApps =
    {
        ("chatgpt", "ChatGPT", "desktop"),
        ("claude", "Claude", "desktop"),
        ("cursor", "Cursor", "editor"),
        ("windsurf", "Windsurf", "editor"),
        ("trae", "Trae", "editor"),
        ("trae cn", "Trae", "editor"),
        ("kiro", "Kiro", "editor"),
        ("zed", "Zed", "editor"),
        ("code", "VS Code", "editor"),
        ("code - insiders", "VS Code", "editor"),
        ("doubao", "豆包", "desktop"),
        ("kimi", "Kimi", "desktop"),
        ("deepseek", "DeepSeek", "desktop"),
        ("yuanbao", "腾讯元宝", "desktop"),
        ("qianwen", "通义千问", "desktop"),
        ("perplexity", "Perplexity", "desktop"),
        ("copilot", "Copilot", "desktop"),
        ("cherry studio", "Cherry Studio", "desktop"),
        ("lm studio", "LM Studio", "desktop"),
        ("chatbox", "Chatbox", "desktop"),
        ("ollama app", "Ollama", "desktop"),
        ("poe", "Poe", "desktop"),
    };
    // Browser window titles end with the active tab title, e.g. "Chat name - Claude - Google Chrome".
    private static readonly (string keyword, string provider)[] BrowserTitleKeywords =
    {
        ("chatgpt", "ChatGPT"), ("claude", "Claude"), ("gemini", "Gemini"), ("ai studio", "Google AI Studio"),
        ("notebooklm", "NotebookLM"), ("perplexity", "Perplexity"), ("deepseek", "DeepSeek"), ("kimi", "Kimi"),
        ("豆包", "豆包"), ("通义", "通义千问"), ("千问", "通义千问"), ("qwen", "Qwen"), ("文心", "文心一言"),
        ("元宝", "腾讯元宝"), ("智谱", "智谱清言"), ("chatglm", "智谱清言"), ("grok", "Grok"), ("copilot", "Copilot"),
        ("poe", "Poe"), ("mistral", "Mistral"), ("秘塔", "秘塔 AI"), ("海螺", "MiniMax"), ("minimax", "MiniMax"),
        ("星火", "讯飞星火"), ("天工", "天工 AI"), ("lmarena", "LMArena"), ("manus", "Manus"), ("muse", "Muse"),
    };
    // Claude Code sets the terminal title to "✳ <task>" when idle and a rotating glyph while working.
    private const string ClaudeIdleGlyphs = "✳";
    private const string ClaudeBusyGlyphs = "◐◓◑◒✢✶✻✽·⠁⠂⠄⠈⠐⠠⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏";

    public static IReadOnlyList<WindowInfo> Discover()
    {
        var result = new List<WindowInfo>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsSwitchableWindow(hwnd)) return true;
            var length = GetWindowTextLength(hwnd);
            if (length < 2 || length > 1000) return true;
            var titleBuffer = new StringBuilder(length + 2);
            GetWindowText(hwnd, titleBuffer, titleBuffer.Capacity);
            string title = titleBuffer.ToString().Trim();
            if (title.Length == 0) return true;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == Environment.ProcessId) return true;
            string process;
            string executablePath = "";
            try
            {
                using var p = Process.GetProcessById((int)pid);
                process = p.ProcessName;
                try { executablePath = p.MainModule?.FileName ?? ""; }
                catch { /* Path may be protected; retain the process name. */ }
            }
            catch { return true; }
            var info = Detect(hwnd, process, title, executablePath);
            if (info != null && info.ExecutablePath.Length == 0) info = info with { ExecutablePath = executablePath };
            if (info != null) result.Add(info);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>Same rules as the Alt+Tab list: skips helper popups such as Windows Terminal's "主机弹出窗口".</summary>
    private static bool IsSwitchableWindow(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd)) return false;
        int ex = GetWindowLong(hwnd, GwlExStyle);
        if ((ex & WsExToolWindow) != 0) return false;
        if (GetWindow(hwnd, GwOwner) != IntPtr.Zero && (ex & WsExAppWindow) == 0) return false;
        if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        return true;
    }

    private static WindowInfo? Detect(IntPtr hwnd, string process, string title, string executablePath)
    {
        var p = process.ToLowerInvariant();
        var t = title.ToLowerInvariant();
        // Codex Desktop currently ships a ChatGPT.exe executable inside the Codex installation.
        if (p == "chatgpt" && executablePath.Contains(@"\Codex\app\", StringComparison.OrdinalIgnoreCase))
            return new(hwnd, title, process, "Codex", "desktop", "", executablePath);
        if (Browsers.Contains(p))
        {
            // Browser window fallback only. The optional extension supplies tab-level sessions.
            foreach (var (keyword, provider) in BrowserTitleKeywords)
                if (t.Contains(keyword)) return new(hwnd, title, process, provider, "browser-window");
            return null;
        }
        if (Terminals.Contains(p))
        {
            var (clean, activity) = ParseClaudeCodeTitle(title);
            var provider = activity != "" ? "Claude Code" : AiCliProvider(title);
            // Windows Terminal hosts tabs we cannot see in the window title, so keep the host even when the
            // visible tab is a plain shell; MainWindow expands it per tab. Other plain shells are noise.
            if (p == "windowsterminal") return new(hwnd, provider != null ? clean : title, process, provider ?? "", "terminal", activity);
            return provider == null ? null : new(hwnd, clean, process, provider, "terminal", activity);
        }
        foreach (var (name, provider, kind) in DesktopApps)
            if (p == name) return new(hwnd, title, process, provider, kind);
        if (p.Contains("codex")) return new(hwnd, title, process, "Codex", "desktop");
        return null;
    }

    /// <summary>AI CLI named in a terminal title, or null for a plain shell.</summary>
    public static string? AiCliProvider(string title)
    {
        var t = title.ToLowerInvariant();
        return t.Contains("claude") ? "Claude Code" : t.Contains("codex") ? "Codex" : t.Contains("gemini") ? "Gemini CLI" : null;
    }

    public static (string title, string activity) ParseClaudeCodeTitle(string title)
    {
        if (title.Length < 2 || title[1] != ' ') return (title, "");
        var glyph = title[0];
        if (ClaudeIdleGlyphs.Contains(glyph)) return (title[2..].Trim(), "idle");
        if (ClaudeBusyGlyphs.Contains(glyph)) return (title[2..].Trim(), "running");
        return (title, "");
    }

    public static bool Exists(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd);

    /// <summary>Browser window whose title (= its active tab's title) starts with the given text.</summary>
    public static IntPtr FindBrowserWindow(string tabTitle)
    {
        if (string.IsNullOrWhiteSpace(tabTitle)) return IntPtr.Zero;
        IntPtr found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            if (!IsSwitchableWindow(hwnd)) return true;
            var length = GetWindowTextLength(hwnd);
            if (length < tabTitle.Length) return true;
            var buffer = new StringBuilder(length + 2);
            GetWindowText(hwnd, buffer, buffer.Capacity);
            if (!buffer.ToString().StartsWith(tabTitle, StringComparison.Ordinal)) return true;
            GetWindowThreadProcessId(hwnd, out uint pid);
            try
            {
                using var p = Process.GetProcessById((int)pid);
                if (!Browsers.Contains(p.ProcessName)) return true;
            }
            catch (ArgumentException) { return true; }
            found = hwnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// Brings a window to the front, working around the foreground lock: Windows refuses
    /// SetForegroundWindow from a process that is not in front and did not receive the last input,
    /// which is exactly the case after clicking a tray notification or the floating widget.
    /// Returns true only if the window really ended up in the foreground.
    /// </summary>
    public static bool Activate(IntPtr hwnd)
    {
        if (!Exists(hwnd)) return false;
        if (IsIconic(hwnd)) ShowWindow(hwnd, SwRestore);
        if (SetForegroundWindow(hwnd) && GetForegroundWindow() == hwnd) return true;

        // 1) An empty mouse input makes this process the "last input" owner (same trick as PowerToys);
        //    unlike a synthetic Alt tap it cannot open a menu bar in the current window.
        var input = new Input { Type = InputMouse };
        SendInput(1, new[] { input }, Marshal.SizeOf<Input>());
        if (SetForegroundWindow(hwnd) && GetForegroundWindow() == hwnd) return true;

        // 2) Hold Alt while asking: the foreground lock lets a switch through during an Alt chord.
        //    Needed when the foreground is the Windows notification popup, which ignores 1).
        keybd_event(VkMenu, 0, KeyeventfExtendedKey, UIntPtr.Zero);
        bool switched = SetForegroundWindow(hwnd);
        keybd_event(VkMenu, 0, KeyeventfExtendedKey | KeyeventfKeyUp, UIntPtr.Zero);
        if (switched && GetForegroundWindow() == hwnd) return true;

        // 3) Share the foreground thread's input state for the duration of the call.
        var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var currentThread = GetCurrentThreadId();
        bool attached = foregroundThread != 0 && foregroundThread != currentThread
            && AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(currentThread, foregroundThread, false);
        }
        return GetForegroundWindow() == hwnd;
    }

    /// <summary>Taskbar flash, used when Windows still refuses to switch focus.</summary>
    public static void Flash(IntPtr hwnd)
    {
        var info = new FlashWInfo
        {
            Size = (uint)Marshal.SizeOf<FlashWInfo>(), Hwnd = hwnd,
            Flags = FlashwAll | FlashwTimerNoFg, Count = 3
        };
        FlashWindowEx(ref info);
    }

    private const int SwRestore = 9;
    private const byte VkMenu = 0x12;
    private const uint KeyeventfExtendedKey = 0x1, KeyeventfKeyUp = 0x2;
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    private const uint InputMouse = 0, FlashwAll = 3, FlashwTimerNoFg = 12;
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool doAttach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FlashWInfo info);

    // INPUT with the mouse member; the union is sized for MOUSEINPUT, the largest variant on x64.
    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public int Dx, Dy;
        public uint MouseData, Flags, Time;
        public IntPtr ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWInfo
    {
        public uint Size;
        public IntPtr Hwnd;
        public uint Flags, Count, Timeout;
    }
}
