# AI Cockpit

Windows 本地 AI 会话控制台（MVP 0.1）。

**定位**：AI 专注力面板。同时开着网页版 ChatGPT / Claude / Muse、多个 Claude Code 终端和 Codex 桌面版时，一个面板告诉你哪个在跑、哪个跑完了在等你回复，一键跳过去。它不替代任何 AI 工具，也不代理模型请求。

## 已实现

- 默认 **Alt + Q** 呼出/隐藏控制台；**Alt + W “送过去”**：直接跳到等你最久的会话（先待回复，再“等你确认?”），不用打开面板。两个快捷键都可在面板右上角 ⚙ 或托盘“设置…”中修改。
- **悬浮窗**：置顶的小胶囊，一个状态点加一句话（绿 = 有 AI 等你回复，琥珀 = 手动待处理，蓝 = 都在运行，灰 = 都很安静）。鼠标悬停展开待回复和运行中的会话（显示“等了 N 分钟”），点击直接跳转；单击胶囊打开面板，拖动移动并记住位置，右键可隐藏。不抢焦点、不进 Alt+Tab，可在设置或托盘菜单中开关。
- 卡片和悬浮窗显示真实图标：应用取窗口/程序图标，网页取浏览器 favicon（扩展 v0.3+）；终端里的 Claude Code 显示橙色 ✳。
- 检测常见 AI 桌面应用、编辑器（VS Code / Cursor / Windsurf / Trae 等）、终端，以及标题匹配的浏览器窗口；按 Claude / Codex / 浏览器 · 网页 AI / 编辑器 / AI 桌面应用 / 其他 AI 终端 分组显示。
- 识别 Claude Code 终端会话：根据终端标题前缀判断“运行中 / 空闲”。Windows Terminal 通过 UI Automation 读取**每个标签页**，后台标签里的会话也能识别，点击会切到对应标签页。自动过滤“主机弹出窗口”等辅助窗口。
- **显示所在目录**：Claude Code 按终端标题对应到 `~/.claude/projects` 中的会话，Codex 读取会话记录里的 `cwd`；卡片和悬浮窗显示文件夹名，悬停显示完整路径和 AI 最后一条回复。
- **卡住检测**：Claude Code 显示运行中、但会话记录里最后一步是还没结果的工具调用且超过 90 秒，标为“等你确认?”（多半在等你批准权限）；超过 3 分钟没有任何进展标为“可能卡住”（Codex 同样适用）。会排到运行中之前、弹一次通知，Alt + W 也会跳到它们。长时间运行的命令也可能触发，所以文案带问号。
- **一句话简报**：取 AI 最后一条回复的第一句有内容的话（去掉 Markdown，标题后优先取正文），显示在悬浮窗待回复/卡住的行和面板卡片上，不切窗口就知道它做完了什么。
- 按标题、提供者和项目名搜索；Enter 或双击切换到原窗口。
- 手动标记待处理、收藏、重命名、按项目分组；本地 JSON 持久化。
- Chrome / Edge 可选 MV3 扩展：将常用 AI 网站（ChatGPT、Claude、Gemini、Perplexity、DeepSeek、Kimi、豆包、通义千问、文心一言、腾讯元宝、智谱清言、Grok、Copilot 等 40+ 站点）的标签分别列出，并定位到对应原标签。识别规则见 `extension/background.js` 的 `AI_SITES`。
- **待回复提醒**：Claude Code 终端、Codex 会话或网页版 AI（扩展 v0.3+）从“运行中”变为停止时，自动标成“待回复”并排到最前，弹出系统通知（点击通知直接跳转，可在设置中关闭）。
- **Codex 会话**：读取 `~/.codex` 中的会话记录，只显示正在运行或 AI 已回复、你还没看的会话（8 小时内），显示项目名与最后一条回复预览，点击通过 `codex://threads/<id>` 直接打开该会话。其他历史会话不显示。
- 普通终端窗口（未运行 AI CLI 的 shell）不显示。不需要 API Key、管理员权限或远程服务。

## 一、运行原生主程序

本机使用 .NET 9 SDK（WPF + WinForms 托盘）。在 PowerShell 中执行：

```powershell
cd D:\git-project\ai-cockpit
dotnet build AiCockpit.sln -c Debug
.\src\AiCockpit\bin\Debug\net9.0-windows10.0.17763.0\AiCockpit.exe
```

也可以双击项目根目录的 `启动 AI Cockpit.bat`。主程序默认驻留系统托盘，按 **Alt + Q**（可在设置中修改）唤出。退出时右键托盘图标选择“退出”。

## 二、可选：开启浏览器标签级管理

不用扩展时，浏览器最多被识别为单个窗口；不能保证自动列出它内部的每个对话。

1. 启动 AI Cockpit。
2. Chrome 打开 `chrome://extensions`，或 Edge 打开 `edge://extensions`。
3. 打开“开发者模式”，选择“加载已解压的扩展程序”，目录选择 `D:\git-project\ai-cockpit\extension`。
4. 复制浏览器扩展详情页中的 **32 字符扩展 ID**。
5. PowerShell 执行（将 32 字符 ID 替换成自己的）：

```powershell
.\scripts\register-native-host.ps1 -Browser Chrome -ExtensionId <扩展ID>
# Edge 浏览器则改成 -Browser Edge
```

6. 在扩展管理页点击“重新加载”该扩展。刷新 AI Cockpit 面板后，即可看到网页版会话。

上述注册脚本只写入当前用户的 Chrome/Edge Native Messaging Host 注册项，不需要管理员权限。要同时支持 Chrome 和 Edge，分别加载扩展并使用各自扩展 ID 执行两次注册。

**权限说明**：扩展需要 `tabs`、`scripting` 和所有网站的访问权限。内容脚本会注入每个页面，但只在 `AI_SITES` 列表中的 AI 网站（以及域名含 muse 的网站）上运行，检测页面上“停止生成”按钮是否存在，以判断 AI 是否仍在生成；发送给本机的只有标题、URL、tabId、窗口 ID、是否正在生成和是否正在查看。源代码见 `extension/`。**升级到 v0.3 后需在扩展管理页点“重新加载”。**

## 三、使用

- `Alt + Q`（默认，可在设置中修改）：显示或隐藏
- `Alt + W`（默认）：送过去，跳到下一个等你的会话
- `Esc`：隐藏并尝试恢复前一个窗口
- `↑/↓ + Enter`：选择会话并跳转
- 双击一行：切换到原窗口/标签页
- 点击 ★ / ◷：收藏 / 待处理
- 右键会话：改名、设置项目、收藏、待处理
- 搜索框：搜索会话标题、AI 产品、项目名称
- 分类：全部 / 待处理（含手动标记和自动“待回复”） / 已收藏

“待处理”是用户手动标记；“待回复”是自动判断：Claude Code 依据终端标题前缀（✳ 空闲、旋转符号运行中），Codex 依据会话记录中的 `task_started` / `task_complete` 事件。网页版依据扩展内容脚本检测到的“停止生成”按钮（ChatGPT、Claude、Gemini 有专门规则，其他站点按按钮文字 Stop / 停止 匹配）。

## 四、数据存储

用户备注、待处理状态和 Codex 会话的已读时间保存在（设置保存在同目录 `settings.json`）：

`%LOCALAPPDATA%\AiCockpit\sessions.json`

窗口句柄/PID 和浏览器 tabId 仅用于当前运行时定位，不作为跨启动的持久唯一身份。窗口改名、多个窗口重名等情况下，手动备注关联可能失效或共享，这是 MVP 的已知限制。

## 五、技术架构

- `src/AiCockpit`：.NET 9 WPF 原生控制台、Win32 窗口发现、会话状态与本地 JSON。
- `src/AiCockpit.Bridge`：Chrome/Edge Native Messaging stdio 协议与 Windows Named Pipe 转发。
- `extension`：Manifest V3 浏览器扩展。
- `scripts`：构建、运行、按浏览器注册原生消息桥接。

扩展先把标签快照发给原生桥接器，桥接器转发给本地 WPF 进程。点击会话时指令沿相反方向返回，激活原标签页。

## 六、当前限制与后续

- **不支持**从 Win32 API 直接识别 Windows Terminal 同一窗口内的每个标签/窗格。
- **不支持**Codex Desktop 内部多个对话线程的枚举；只能切换到桌面窗口。
- **不支持**自动判断 AI 正在运行或已结束，避免虚假的状态提示。Claude Code Hooks、Codex CLI 通知事件是下一阶段。
- 浏览器扩展只识别 `AI_SITES` 列表中的网站和域名含 muse 的网站；其他网站需在列表中添加一行规则。
- 浏览器扩展是手动安装，未发布商店；也未做代码签名。
- 已在本机验证：Debug / Release 编译、后台驻留、快捷键消息唤出/隐藏窗口，以及 Native Messaging 桥接到本地进程的连接测试。尚未完成浏览器实际安装后的多配置文件人工端到端验证。
- 偏好持久化暂用 JSON，后续会话规模扩大或要保存事件历史时再迁移到 SQLite。

## 隐私与退出

不需要登录 AI Cockpit，不使用外网服务。退出主程序后不会关闭任何已打开的 AI 网页/终端。要停止扩展连接，可以在浏览器的扩展管理页禁用该扩展；要删除原生消息注册，可删除当前用户注册表对应项。
