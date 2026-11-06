using System.IO;
using System.Text;
using System.Text.Json;

namespace AiFocus;

public sealed record ChatMessage(bool FromUser, string Text)
{
    public string RoleLabel => FromUser ? "你" : "AI";
}

/// <summary>
/// Reads the latest turns of a Claude Code or Codex conversation from its transcript (JSONL),
/// keeping only what the user typed and what the AI wrote back: tool calls, tool output and
/// injected context are skipped.
/// </summary>
public static class Conversation
{
    public const int MaxMessages = 8;
    private const int MaxChars = 2500;
    private const int TailBytes = 1024 * 1024;

    public static IReadOnlyList<ChatMessage> ReadClaude(string path) =>
        Last(TailLines(path).Select(ParseClaude));

    public static IReadOnlyList<ChatMessage> ReadCodex(string path) =>
        Last(TailLines(path).Select(ParseCodex));

    private static IReadOnlyList<ChatMessage> Last(IEnumerable<ChatMessage?> messages)
    {
        var list = messages.OfType<ChatMessage>().ToList();
        return list.Skip(Math.Max(0, list.Count - MaxMessages)).ToList();
    }

    private static ChatMessage? ParseClaude(string line)
    {
        if (!line.Contains("\"type\":\"user\"", StringComparison.Ordinal)
            && !line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal)) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            if (type is not ("user" or "assistant")) return null;
            bool user = type == "user";
            if (root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True) return null;
            if (root.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True) return null;
            if (!root.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content)) return null;
            // User turns are a plain string or text parts; tool results arrive as user turns too and are skipped.
            var text = content.ValueKind == JsonValueKind.String ? content.GetString() ?? "" : TextParts(content, "text");
            return Make(user, text);
        }
        catch (JsonException) { return null; }
    }

    private static ChatMessage? ParseCodex(string line)
    {
        if (!line.Contains("\"type\":\"response_item\"", StringComparison.Ordinal)
            || !line.Contains("\"payload\":{\"type\":\"message\"", StringComparison.Ordinal)) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var payload = doc.RootElement.GetProperty("payload");
            var role = payload.TryGetProperty("role", out var r) ? r.GetString() : null;
            if (role is not ("user" or "assistant") || !payload.TryGetProperty("content", out var content)) return null;
            var text = TextParts(content, role == "user" ? "input_text" : "output_text");
            return Make(role == "user", text);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    private static string TextParts(JsonElement content, string partType)
    {
        if (content.ValueKind != JsonValueKind.Array) return "";
        var text = new StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object) continue;
            if (part.TryGetProperty("type", out var type) && type.GetString() == partType
                && part.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String)
            {
                if (text.Length > 0) text.Append('\n');
                text.Append(value.GetString());
            }
        }
        return text.ToString();
    }

    private static ChatMessage? Make(bool fromUser, string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        // Injected context and command echoes look like XML blocks ("<environment_context>",
        // "<command-name>", "<system-reminder>"); real prompts rarely start that way.
        if (fromUser && (text.StartsWith('<') || text.StartsWith("Caveat:", StringComparison.Ordinal))) return null;
        return new ChatMessage(fromUser, text.Length > MaxChars ? text[..MaxChars] + "\n…" : text);
    }

    private static IEnumerable<string> TailLines(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return Array.Empty<string>();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start = Math.Max(0, stream.Length - TailBytes);
            stream.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[stream.Length - start];
            stream.ReadExactly(buffer);
            var lines = Encoding.UTF8.GetString(buffer).Split('\n');
            return start > 0 ? lines.Skip(1).ToArray() : lines;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }
}
