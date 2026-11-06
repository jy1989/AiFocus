using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AiCockpit;

/// <summary>
/// Lists Codex threads (Desktop and CLI) from CODEX_HOME: names from session_index.jsonl,
/// project / state / last reply from each sessions/**/rollout-*.jsonl. Archived threads live
/// elsewhere, so the sessions folder mirrors what the Codex sidebar shows.
/// </summary>
public sealed class CodexThreads
{
    /// <param name="RepliedUtc">When the AI finished its last turn; null while running or after the user interrupted it.</param>
    public sealed record Thread(string Id, string Title, string Directory, DateTime UpdatedUtc, string Activity, string LastMessage, DateTime? RepliedUtc);

    private sealed class Rollout
    {
        public string Id = "";
        public DateTime WriteUtc;
        public long Length = -1;
        public string Directory = "";
        public string Activity = "idle";
        public string LastMessage = "";
        public DateTime? RepliedUtc;
    }

    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    // A turn that started but has not written anything for this long was most likely interrupted.
    private static readonly TimeSpan StaleRunning = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(10);
    private const int TailBytes = 512 * 1024;

    private readonly string _home = Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } h
        ? h : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    private readonly Dictionary<string, Rollout> _rollouts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _indexWriteUtc;
    private DateTime _lastRescanUtc = DateTime.MinValue;
    private List<string> _files = new();

    public IReadOnlyList<Thread> Scan()
    {
        var sessions = Path.Combine(_home, "sessions");
        if (!Directory.Exists(sessions)) return Array.Empty<Thread>();
        LoadIndex();
        var now = DateTime.UtcNow;
        if (now - _lastRescanUtc > RescanInterval)
        {
            _lastRescanUtc = now;
            try { _files = Directory.EnumerateFiles(sessions, "rollout-*.jsonl", SearchOption.AllDirectories).ToList(); }
            catch (Exception ex) { Debug.WriteLine("Codex sessions: " + ex.Message); }
        }
        var result = new List<Thread>();
        foreach (var path in _files)
        {
            var info = new FileInfo(path);
            if (!info.Exists || now - info.LastWriteTimeUtc > MaxAge) continue;
            if (!_rollouts.TryGetValue(path, out var r))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (name.Length < 36) continue;
                r = new Rollout { Id = name[^36..] };
                _rollouts[path] = r;
            }
            if (info.LastWriteTimeUtc != r.WriteUtc || info.Length != r.Length) Parse(info, r);
            var activity = r.Activity == "running" && now - r.WriteUtc > StaleRunning ? "idle" : r.Activity;
            var title = _names.TryGetValue(r.Id, out var n) && n.Length > 0 ? n : "未命名会话";
            result.Add(new Thread(r.Id, title, r.Directory, r.WriteUtc, activity, r.LastMessage, activity == "running" ? null : r.RepliedUtc));
        }
        return result;
    }

    private void LoadIndex()
    {
        var path = Path.Combine(_home, "session_index.jsonl");
        try
        {
            var write = File.GetLastWriteTimeUtc(path);
            if (write == _indexWriteUtc) return;
            _indexWriteUtc = write;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            // Appended over time; later lines (renames) win.
            while (reader.ReadLine() is { } line)
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("id", out var id) && root.TryGetProperty("thread_name", out var name))
                        _names[id.GetString() ?? ""] = name.GetString() ?? "";
                }
                catch (JsonException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void Parse(FileInfo info, Rollout r)
    {
        try
        {
            using var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (r.Directory.Length == 0)
            {
                using var head = new StreamReader(stream, Encoding.UTF8, false, 64 * 1024, leaveOpen: true);
                if (head.ReadLine() is { } first) r.Directory = DirectoryFromMeta(first);
            }
            long start = Math.Max(0, stream.Length - TailBytes);
            stream.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[stream.Length - start];
            stream.ReadExactly(buffer);
            var lines = Encoding.UTF8.GetString(buffer).Split('\n');
            // The first line of a mid-file chunk is usually cut in half.
            for (int i = lines.Length - 1; i >= (start > 0 ? 1 : 0); i--)
            {
                var line = lines[i];
                if (!line.Contains("\"payload\":{\"type\":\"task_", StringComparison.Ordinal) &&
                    !line.Contains("\"payload\":{\"type\":\"turn_aborted\"", StringComparison.Ordinal)) continue;
                using var doc = JsonDocument.Parse(line);
                var payload = doc.RootElement.GetProperty("payload");
                var type = payload.GetProperty("type").GetString();
                if (type == "task_started") { r.Activity = "running"; r.RepliedUtc = null; break; }
                if (type == "task_complete")
                {
                    r.Activity = "idle";
                    r.RepliedUtc = doc.RootElement.TryGetProperty("timestamp", out var ts) && ts.TryGetDateTime(out var when)
                        ? when.ToUniversalTime() : info.LastWriteTimeUtc;
                    if (payload.TryGetProperty("last_agent_message", out var msg) && msg.ValueKind == JsonValueKind.String)
                        r.LastMessage = msg.GetString()!.Trim();
                    break;
                }
                if (type == "turn_aborted") { r.Activity = "idle"; r.RepliedUtc = null; break; }
            }
            r.WriteUtc = info.LastWriteTimeUtc;
            r.Length = info.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Debug.WriteLine("Codex rollout " + info.Name + ": " + ex.Message);
        }
    }

    private static string DirectoryFromMeta(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("payload", out var p) && p.TryGetProperty("cwd", out var cwd))
                return (cwd.GetString() ?? "").TrimEnd('\\', '/');
        }
        catch (JsonException) { }
        return "";
    }

    public static readonly string DefaultExecutable =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Codex\app\ChatGPT.exe");

    /// <summary>Opens the thread in Codex Desktop through its codex:// deep link (forwarded to the running instance).</summary>
    public static bool Open(string threadId, string? codexExe)
    {
        var exe = codexExe is { Length: > 0 } && File.Exists(codexExe)
            ? codexExe
            : DefaultExecutable;
        if (!File.Exists(exe)) return false;
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(exe, "codex://threads/" + Uri.EscapeDataString(threadId))
            {
                UseShellExecute = false, CreateNoWindow = true
            });
            return true;
        }
        catch (Exception ex) { Debug.WriteLine("Open Codex thread: " + ex.Message); return false; }
    }
}
