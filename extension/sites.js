"use strict";

// Shared by background.js (importScripts) and content.js (content_scripts).
// Ordered: the first matching rule wins. host matches the domain and its subdomains;
// path (optional) must prefix the URL path.
const AI_SITES = [
  { provider: "Codex", host: "chatgpt.com", path: "/codex" },
  { provider: "ChatGPT", host: "chatgpt.com" },
  { provider: "ChatGPT", host: "chat.openai.com" },
  { provider: "Claude", host: "claude.ai" },
  { provider: "Gemini", host: "gemini.google.com" },
  { provider: "Google AI Studio", host: "aistudio.google.com" },
  { provider: "NotebookLM", host: "notebooklm.google.com" },
  { provider: "Perplexity", host: "perplexity.ai" },
  { provider: "Copilot", host: "copilot.microsoft.com" },
  { provider: "Grok", host: "grok.com" },
  { provider: "Grok", host: "x.com", path: "/i/grok" },
  { provider: "Meta AI", host: "meta.ai" },
  { provider: "Mistral", host: "chat.mistral.ai" },
  { provider: "Poe", host: "poe.com" },
  { provider: "HuggingChat", host: "huggingface.co", path: "/chat" },
  { provider: "LMArena", host: "lmarena.ai" },
  { provider: "OpenRouter", host: "openrouter.ai", path: "/chat" },
  { provider: "Character.AI", host: "character.ai" },
  { provider: "You.com", host: "you.com" },
  { provider: "Phind", host: "phind.com" },
  { provider: "Manus", host: "manus.im" },
  { provider: "Genspark", host: "genspark.ai" },
  { provider: "v0", host: "v0.dev" },
  { provider: "v0", host: "v0.app" },
  { provider: "Bolt", host: "bolt.new" },
  { provider: "Lovable", host: "lovable.dev" },
  { provider: "DeepSeek", host: "chat.deepseek.com" },
  { provider: "Kimi", host: "kimi.com" },
  { provider: "Kimi", host: "kimi.moonshot.cn" },
  { provider: "豆包", host: "doubao.com" },
  { provider: "通义千问", host: "tongyi.com" },
  { provider: "通义千问", host: "tongyi.aliyun.com" },
  { provider: "通义千问", host: "qianwen.com" },
  { provider: "Qwen", host: "chat.qwen.ai" },
  { provider: "文心一言", host: "yiyan.baidu.com" },
  { provider: "腾讯元宝", host: "yuanbao.tencent.com" },
  { provider: "智谱清言", host: "chatglm.cn" },
  { provider: "Z.ai", host: "chat.z.ai" },
  { provider: "MiniMax", host: "hailuoai.com" },
  { provider: "MiniMax", host: "chat.minimax.io" },
  { provider: "秘塔 AI", host: "metaso.cn" },
  { provider: "讯飞星火", host: "xinghuo.xfyun.cn" },
  { provider: "天工 AI", host: "tiangong.cn" },
  { provider: "纳米 AI", host: "bot.n.cn" }
];

function providerOf(url) {
  try {
    const u = new URL(url);
    if (u.protocol !== "https:" && u.protocol !== "http:") return null;
    const host = u.hostname.toLowerCase();
    for (const rule of AI_SITES) {
      if (host !== rule.host && !host.endsWith("." + rule.host)) continue;
      if (rule.path && !u.pathname.startsWith(rule.path)) continue;
      return rule.provider;
    }
    if (host.includes("muse")) return "Muse";
  } catch { /* Not a URL. */ }
  return null;
}
