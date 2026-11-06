using System.IO;
using System.Text.Json;
using System.Windows.Input;

namespace AiCockpit;

public sealed class AppSettings
{
    public const string DefaultHotkey = "Alt+Q";
    public string Hotkey { get; set; } = DefaultHotkey;
    public const string DefaultNextHotkey = "Alt+W";
    /// <summary>Jumps to the session waiting longest for a reply; empty disables it.</summary>
    public string NextHotkey { get; set; } = DefaultNextHotkey;
    /// <summary>Tray notification when a Claude Code / Codex session finishes.</summary>
    public bool NotifyOnComplete { get; set; } = true;
    /// <summary>Always-on-top status capsule.</summary>
    public bool ShowWidget { get; set; } = true;
    /// <summary>Widget position in DIPs; null = default (top-right of the primary screen).</summary>
    public double? WidgetLeft { get; set; }
    public double? WidgetTop { get; set; }

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiCockpit", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Load settings: " + ex.Message); }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, FilePath, overwrite: true);
    }
}

/// <summary>Converts between "Ctrl+Alt+A" text and RegisterHotKey arguments.</summary>
public static class HotkeyText
{
    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8;

    public static string Format(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key is >= Key.D0 and <= Key.D9 ? ((char)('0' + (key - Key.D0))).ToString() : key.ToString());
        return string.Join("+", parts);
    }

    public static bool TryParse(string text, out uint win32Modifiers, out uint virtualKey)
    {
        win32Modifiers = 0;
        virtualKey = 0;
        Key? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": win32Modifiers |= ModControl; break;
                case "alt": win32Modifiers |= ModAlt; break;
                case "shift": win32Modifiers |= ModShift; break;
                case "win": case "windows": win32Modifiers |= ModWin; break;
                default:
                    var name = raw.Length == 1 && char.IsDigit(raw[0]) ? "D" + raw : raw;
                    if (!Enum.TryParse<Key>(name, ignoreCase: true, out var k)) return false;
                    key = k;
                    break;
            }
        }
        // Shift alone would steal ordinary typing, so require Ctrl, Alt or Win.
        if (key is null || (win32Modifiers & (ModControl | ModAlt | ModWin)) == 0) return false;
        virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key.Value);
        return virtualKey != 0;
    }

    public static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;

    /// <summary>Display form with spaces, e.g. "Ctrl + Alt + A".</summary>
    public static string Pretty(string text) => text.Replace("+", " + ");
}
