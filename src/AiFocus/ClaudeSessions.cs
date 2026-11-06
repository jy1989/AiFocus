using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AiFocus;

/// <summary>
/// Maps Claude Code terminal titles to their sessions in ~/.claude/projects: the title shown in the
/// terminal is stored as an "ai-title" (or "/rename" custom-title) record, next to the session's cwd
/// and the latest assistant reply.
/// </summary>
public sealed class ClaudeSessions
{
    /// <param name="PendingTool">The last transcript entry is a tool call without a result.</param>
    public sealed record Session(string Title, string Directory, string LastMessage, DateTime UpdatedUtc, bool PendingTool, string Path);

    private sealed class Transcript
    {
        public DateTime WriteUtc;
        public long Length = -1;
        public string Directory = "";
        public string Title = "";
        public string LastMessage = "";
        public bool PendingTool;
    }

    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(3);
    private static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(10);
    private const int HeadBytes = 256 * 1024, TailBytes = 512 * 1024;

    private readonly string _projects = Path.Combine(
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir
            ? dir : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
        "projects");
    private readonly Dictionary<string, Transcript> _transcripts = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastRescanUtc = DateTime.MinValue;
    private List<string> _files = new();

    /// <summary>Recent sessions by title; when titles repeat, the most recently written session wins.</summary>
    public IReadOnlyDictionary<string, Session> Scan()
    {
        var result = new Dictionary<string, Session>(StringComparer.Ordinal);
        if (!Directory.Exists(_projects)) return result;
        var now = DateTime.UtcNow;
        if (now - _lastRescanUtc > RescanInterval)
        {
            _lastRescanUtc = now;
            try
            {
                // Top level only: sub-agent transcripts live in nested folders.
                _files = Directory.EnumerateDirectories(_projects)
                    .SelectMany(d => Directory.EnumerateFiles(d, "*.jsonl", SearchOption.TopDirectoryOnly))
                    .Where(f => now - File.GetLastWriteTimeUtc(f) < MaxAge)
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Debug.WriteLine("Claude projects: " + ex.Message); }
        }
        foreach (var path in _files)
        {
            var info = new FileInfo(path);
            if (!info.Exists) continue;
            if (!_transcripts.TryGetValue(path, out var t)) _transcripts[path] = t = new Transcript();
            if (info.LastWriteTimeUtc != t.WriteUtc || info.Length != t.Length) Parse(info, t);
            if (t.Title.Length == 0) continue;
            if (!result.TryGetValue(t.Title, out var existing) || existing.UpdatedUtc < t.WriteUtc)
                result[t.Title] = new Session(t.Title, t.Directory, t.LastMessage, t.WriteUtc, t.PendingTool, path);
        }
        return result;
    }

    private static void Parse(FileInfo info, Transcript t)
    {
        try
        {
            using var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (t.Directory.Length == 0)
            {
                foreach (var line in ReadLines(stream, 0, Math.Min(stream.Length, HeadBytes), skipFirst: false))
                {
                    if (!line.Contains("\"cwd\":", StringComparison.Ordinal)) continue;
                    if (Field(line, "cwd") is { Length: > 0 } dir)
                    {
                        t.Directory = dir;
                        break;
                    }
                }
            }
            long start = Math.Max(0, stream.Length - TailBytes);
            string title = "", customTitle = "", message = "";
            bool? pendingTool = null;
            foreach (var line in ReadLines(stream, start, stream.Length - start, skipFirst: start > 0))
            {
                if (line.Contains("\"type\":\"custom-title\"", StringComparison.Ordinal)) customTitle = Field(line, "customTitle") ?? customTitle;
                else if (line.Contains("\"type\":\"ai-title\"", StringComparison.Ordinal)) title = Field(line, "aiTitle") ?? title;
                else if (line.Contains("\"role\":\"assistant\"", StringComparison.Ordinal))
                {
                    // A tool call with no result yet: Claude Code is running it, or asking permission to.
                    pendingTool = line.Contains("\"type\":\"tool_use\"", StringComparison.Ordinal);
                    if (line.Contains("\"type\":\"text\"", StringComparison.Ordinal)) message = AssistantText(line) ?? message;
                }
                else if (line.Contains("\"role\":\"user\"", StringComparison.Ordinal)) pendingTool = false;
            }
            if (pendingTool is { } pending) t.PendingTool = pending;
            // A /rename beats the generated title; both are what Claude Code puts in the terminal title.
            if (customTitle.Length > 0) t.Title = customTitle;
            else if (title.Length > 0) t.Title = title;
            if (message.Length > 0) t.LastMessage = message;
            t.WriteUtc = info.LastWriteTimeUtc;
            t.Length = info.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine("Claude transcript " + info.Name + ": " + ex.Message);
        }
    }

    private static IEnumerable<string> ReadLines(FileStream stream, long start, long count, bool skipFirst)
    {
        stream.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[count];
        stream.ReadExactly(buffer);
        var lines = Encoding.UTF8.GetString(buffer).Split('\n');
        // A chunk starting mid-file usually begins with half a line.
        return skipFirst ? lines.Skip(1) : lines;
    }

    private static string? Field(string line, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static string? AssistantText(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("message", out var msg) || !msg.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Array) return null;
            var text = new StringBuilder();
            foreach (var part in content.EnumerateArray())
                if (part.TryGetProperty("type", out var type) && type.GetString() == "text" && part.TryGetProperty("text", out var value))
                    text.Append(value.GetString());
            return text.Length > 0 ? text.ToString().Trim() : null;
        }
        catch (JsonException) { return null; }
    }
}
