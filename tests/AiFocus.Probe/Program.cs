using AiFocus;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// "claude": list Claude Code sessions matched by title (directory, pending tool, brief).
if (args.FirstOrDefault() == "claude")
{
    foreach (var (title, s) in new ClaudeSessions().Scan().OrderByDescending(p => p.Value.UpdatedUtc).Take(15))
        Console.WriteLine($"TITLE={title} | DIR={s.Directory} | PENDING_TOOL={s.PendingTool} | QUIET={(DateTime.UtcNow - s.UpdatedUtc).TotalSeconds:F0}s | BRIEF={SessionEntry.MakeBrief(s.LastMessage)}");
    return;
}

// "convo <transcript.jsonl>": the conversation view's messages for a Claude Code or Codex transcript.
if (args.FirstOrDefault() == "convo" && args.Length > 1)
{
    var messages = Path.GetFileName(args[1]).StartsWith("rollout-") ? Conversation.ReadCodex(args[1]) : Conversation.ReadClaude(args[1]);
    foreach (var m in messages)
        Console.WriteLine($"[{m.RoleLabel}] {m.Text.Replace('\n', ' ')[..Math.Min(110, m.Text.Replace('\n', ' ').Length)]}");
    return;
}

// "perf": how long window discovery takes, cold and with the per-process cache warm.
if (args.FirstOrDefault() == "perf")
{
    for (int run = 1; run <= 3; run++)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var count = WindowsAdapter.Discover().Count;
        Console.WriteLine($"run {run}: {watch.ElapsedMilliseconds} ms, {count} windows");
    }
    return;
}

var found = WindowsAdapter.Discover();
foreach (var s in found)
    Console.WriteLine($"FOUND={s.Provider} | PROCESS={s.Process} | TITLE={s.Title} | HWND={s.Handle}");
Console.WriteLine($"CODEX_COUNT={found.Count(s => s.Provider == "Codex")}");
if (!found.Any(s => s.Provider == "Codex")) Environment.ExitCode = 2;
