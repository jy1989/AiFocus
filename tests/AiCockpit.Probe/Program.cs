using AiCockpit;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// "claude": list Claude Code sessions matched by title (directory, pending tool, brief).
if (args.FirstOrDefault() == "claude")
{
    foreach (var (title, s) in new ClaudeSessions().Scan().OrderByDescending(p => p.Value.UpdatedUtc).Take(15))
        Console.WriteLine($"TITLE={title} | DIR={s.Directory} | PENDING_TOOL={s.PendingTool} | QUIET={(DateTime.UtcNow - s.UpdatedUtc).TotalSeconds:F0}s | BRIEF={SessionEntry.MakeBrief(s.LastMessage)}");
    return;
}

var found = WindowsAdapter.Discover();
foreach (var s in found)
    Console.WriteLine($"FOUND={s.Provider} | PROCESS={s.Process} | TITLE={s.Title} | HWND={s.Handle}");
Console.WriteLine($"CODEX_COUNT={found.Count(s => s.Provider == "Codex")}");
if (!found.Any(s => s.Provider == "Codex")) Environment.ExitCode = 2;
