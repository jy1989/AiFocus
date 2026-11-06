namespace AiFocus;

/// <summary>
/// "--demo": a self-contained instance filled with made-up sessions, for README screenshots.
/// It reads no real windows or transcripts, registers no hotkeys, has no tray icon and keeps its
/// data in a separate folder, so it can run next to the real AI Focus.
/// Options: --view=list|widget, --lang=zh|en.
/// </summary>
public static class Demo
{
    public static bool Enabled { get; private set; }
    public static string View { get; private set; } = "list";
    public static string? Language { get; private set; }

    public static void Parse(string[] args)
    {
        foreach (var arg in args)
        {
            if (arg == "--demo") Enabled = true;
            else if (arg.StartsWith("--view=", StringComparison.Ordinal)) View = arg[7..];
            else if (arg.StartsWith("--lang=", StringComparison.Ordinal)) Language = arg[7..];
        }
    }

    private static string T(string zh, string en) => Loc.T(zh, en);

    public static List<SessionEntry> Sessions()
    {
        var now = DateTime.UtcNow;
        var list = new List<SessionEntry>();

        SessionEntry Add(string key, string provider, string kind, string title, string project, string source,
            string reply, Action<SessionEntry>? state = null)
        {
            var s = new SessionEntry
            {
                RuntimeKey = key, PersistenceKey = key, Provider = provider, Kind = kind, OriginalTitle = title,
                ProjectPath = project, SourceName = source, Preview = reply, LastFocusedUtc = now.AddMinutes(-40)
            };
            state?.Invoke(s);
            list.Add(s);
            return s;
        }

        Add("demo:login", "Claude Code", "terminal-tab", T("重构登录模块", "Refactor the login module"), @"D:\work\shop-api",
            "Windows Terminal",
            T("## 完成\n\n登录流程已拆成三个服务，`npm test` 全部通过（142 个用例）。\n\n- 新增 `AuthService`、`TokenService`\n- 旧的 session 中间件已移除\n- 接口行为保持不变\n\n要我顺便把刷新 token 的逻辑也迁过去吗？",
              "## Done\n\nThe login flow is now three services and `npm test` passes (142 tests).\n\n- Added `AuthService` and `TokenService`\n- Removed the old session middleware\n- API behaviour is unchanged\n\nShould I move the token refresh logic over as well?"),
            s => { s.Completed = true; s.CompletedUtc = now.AddMinutes(-12); s.LastFocusedUtc = now.AddMinutes(-25); });

        Add("demo:payment", "Claude Code", "terminal-tab", T("修复支付回调重复入账", "Fix duplicate payment callbacks"), @"D:\work\shop-api",
            "Windows Terminal",
            T("找到原因了：回调没有做幂等校验，网关重试时会再次入账。\n\n我准备运行迁移脚本，给 `payments` 表加上唯一索引：\n\n```bash\nnpm run migrate -- add-payment-idempotency\n```",
              "Found it: the callback isn't idempotent, so gateway retries book the payment again.\n\nI'm about to run a migration that adds a unique index to `payments`:\n\n```bash\nnpm run migrate -- add-payment-idempotency\n```"),
            s => { s.Activity = "running"; s.Stall = "approval"; s.StallSinceUtc = now.AddMinutes(-3); s.LastFocusedUtc = now.AddMinutes(-8); });

        Add("demo:report", "Claude Code", "terminal-tab", T("生成季度报表脚本", "Quarterly report script"), @"D:\work\data-tools",
            "Windows Terminal",
            T("正在读取 `sales` 和 `refunds` 两张表的结构，然后生成按区域汇总的 SQL。", "Reading the `sales` and `refunds` schemas, then generating SQL grouped by region."),
            s => { s.Activity = "running"; s.LastFocusedUtc = now.AddMinutes(-2); });

        Add("demo:tests", "Codex", "codex-thread", T("补充库存服务单元测试", "Add inventory service unit tests"), @"D:\work\inventory-service",
            "Codex",
            T("已新增 18 个测试，覆盖出库超卖、并发扣减和退货回补；全部通过。覆盖率从 61% 提到 84%。",
              "Added 18 tests covering overselling, concurrent deductions and returns; all passing. Coverage went from 61% to 84%."),
            s => { s.Completed = true; s.CompletedUtc = now.AddMinutes(-3); s.LastFocusedUtc = now.AddMinutes(-15); });

        Add("demo:postgres", "Codex", "codex-thread", T("迁移到 PostgreSQL", "Migrate to PostgreSQL"), @"D:\work\inventory-service",
            "Codex",
            T("正在改写 12 个依赖 MySQL 语法的查询，目前完成 7 个。", "Rewriting the 12 queries that rely on MySQL syntax; 7 done so far."),
            s => { s.Activity = "running"; s.LastFocusedUtc = now.AddMinutes(-2); });

        Add("demo:trip", "ChatGPT", "web", T("五天京都行程规划", "Five days in Kyoto"), "", "Chrome",
            T("好的，按区域安排，每天不超过三个点：\n\n1. **东山**：清水寺、二年坂、八坂神社\n2. **岚山**：竹林、天龙寺、小火车\n3. **伏见稻荷** + 东福寺",
                   "Sure, grouped by area with at most three stops a day:\n\n1. **Higashiyama**: Kiyomizu-dera, Ninenzaka, Yasaka Shrine\n2. **Arashiyama**: bamboo grove, Tenryu-ji, the scenic train\n3. **Fushimi Inari** + Tofuku-ji"),
            s => { s.LastFocusedUtc = now.AddMinutes(-20); s.Favorite = true; });

        Add("demo:paper", "Claude", "web", T("论文摘要润色", "Polish the paper abstract"), "", "Chrome",
            T("改好了，从 286 词压到 172 词，保留了三个核心贡献，并把方法描述合并成一句。",
                   "Done: 286 words down to 172, keeping the three core contributions and merging the method description into one sentence."),
            s => { s.Completed = true; s.CompletedUtc = now.AddMinutes(-1); s.LastFocusedUtc = now.AddMinutes(-6); });

        Add("demo:compare", "Gemini", "web", T("竞品功能对比", "Competitor feature comparison"), "", "Chrome",
            T("| | 免费版 | 协作 |\n|---|---|---|\n| A | 3 个项目 | 实时 |\n| B | 无限 | 评论 |\n| C | 1 个项目 | 实时 + 评论 |",
                   "| | Free tier | Collaboration |\n|---|---|---|\n| A | 3 projects | Real-time |\n| B | Unlimited | Comments |\n| C | 1 project | Real-time + comments |"),
            s => { s.LastFocusedUtc = now.AddHours(-2); });

        return list;
    }
}
