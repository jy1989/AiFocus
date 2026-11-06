using System.Globalization;

namespace AiFocus;

/// <summary>
/// UI language: Chinese or English. "auto" follows the Windows display language. Chosen once at
/// startup (XAML reads these values when windows load), so switching takes effect after a restart.
/// </summary>
public static class Loc
{
    public static bool English { get; private set; }

    public static void Init(string setting) => English = setting switch
    {
        "en" => true,
        "zh" => false,
        _ => !CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
    };

    public static string T(string zh, string en) => English ? en : zh;

    // ---- Strings bound from XAML ({x:Static local:Loc.Name}) ----
    public static string SearchHint => T("搜索会话、项目或 AI 工具", "Search sessions, projects or AI tools");
    public static string FilterAll => T("全部", "All");
    public static string FilterAttention => T("待处理", "To do");
    public static string FilterFavorite => T("已收藏", "Starred");
    public static string Conversation => T("看对话  Tab", "Chat  Tab");
    public static string ConversationTip => T("右侧显示选中会话最近几轮对话（Tab 切换）", "Show the selected session's latest turns on the right (Tab)");
    public static string ConsoleMode => T("控制台", "Console");
    public static string ConsoleTip => T("最大化，所有会话一屏显示：实时窗口预览或最近对话", "Maximise and show every session at once: live window preview or latest turns");
    public static string Refresh => T("刷新", "Refresh");
    public static string Settings => T("设置（快捷键、通知）", "Settings (hotkeys, notifications)");
    public static string Hide => T("关闭（Esc）", "Close (Esc)");
    public static string ToggleFavorite => T("收藏 / 取消收藏", "Star / unstar");
    public static string ToggleAttention => T("标记 / 取消待处理", "Mark / unmark to-do");
    public static string MenuOpen => T("打开会话", "Open session");
    public static string MenuRename => T("修改显示名称", "Rename");
    public static string MenuWorkspace => T("设置项目分组", "Set project");
    public static string KeyHints => T("↑↓ 选择 · Enter 跳转 · Esc 关闭 · 右键管理", "↑↓ select · Enter jump · Esc close · right-click to manage");
    public static string ConversationFooter => T("Enter 跳过去处理 · Tab 关闭对话", "Enter to jump there · Tab to close");
    public static string WidgetTitle => T("AI Focus 悬浮窗", "AI Focus widget");
    public static string WidgetTip => T("单击打开面板 · 拖动移动 · 右键更多", "Click to open the panel · drag to move · right-click for more");
    public static string MenuOpenPanel => T("打开面板", "Open panel");
    public static string MenuNext => T("送过去（下一个等你的会话）", "Send me there (next session waiting for you)");
    public static string MenuBack => T("送回来（回到刚才的窗口）", "Bring me back (to my previous window)");
    public static string MenuHideWidget => T("隐藏悬浮窗", "Hide widget");
    public static string AllQuiet => T("AI 都很安静", "All AIs are quiet");
}
