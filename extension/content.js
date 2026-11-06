"use strict";

// Reports whether the AI on this page is still generating, so AI Focus can flag
// "replied, waiting for you" when it stops. Runs on every page but exits at once
// unless the URL is in AI_SITES (sites.js).
(() => {
  if (!providerOf(location.href)) return;

  // Explicit selectors for the sites we know, plus a label-based fallback that covers
  // most chat UIs ("Stop generating", "Stop response", "停止生成" ...).
  const KNOWN = [
    '[data-testid="stop-button"]',          // ChatGPT
    '[data-is-streaming="true"]',           // Claude
    'button[aria-label="Stop response"]',   // Claude, Gemini
    'button[aria-label*="Stop generating" i]',
    'button[aria-label*="Stop streaming" i]'
  ].join(",");
  const STOP_LABEL = /^(stop|停止|中止)$|stop (generating|response|streaming|answer)|停止(生成|回答|输出|响应)|中止(生成|回答)/i;

  function visible(el) {
    const r = el.getBoundingClientRect();
    return r.width > 0 && r.height > 0;
  }

  function isGenerating() {
    const known = document.querySelector(KNOWN);
    if (known && (known.matches("[data-is-streaming]") || visible(known))) return true;
    for (const el of document.querySelectorAll("button[aria-label], button[title], [role='button'][aria-label]")) {
      const label = (el.getAttribute("aria-label") || el.getAttribute("title") || "").trim();
      if (label && STOP_LABEL.test(label) && visible(el)) return true;
    }
    return false;
  }

  let reported = null;
  let lastCheck = 0;
  let trailing = null;
  let alive = true;

  function send(generating) {
    try {
      chrome.runtime.sendMessage({ type: "generation", generating });
    } catch {
      // Extension reloaded: this script is orphaned, stop watching.
      alive = false;
      observer.disconnect();
    }
  }

  function check() {
    if (!alive) return;
    lastCheck = Date.now();
    const now = isGenerating();
    if (now !== reported) {
      reported = now;
      send(now);
    }
  }

  // DOM mutations keep firing in background tabs (timers there are throttled),
  // so they drive the checks; a trailing timer catches the final mutation.
  const observer = new MutationObserver(() => {
    if (Date.now() - lastCheck > 400) check();
    clearTimeout(trailing);
    trailing = setTimeout(check, 600);
  });
  observer.observe(document.documentElement, {
    childList: true, subtree: true, attributes: true,
    attributeFilter: ["data-testid", "data-is-streaming", "aria-label", "disabled"]
  });

  check();
  // Heartbeat: the background service worker may have restarted and lost its state.
  setInterval(() => { if (alive && reported !== null) send(reported); }, 20000);

  // --- Latest turns, read on demand for AI Focus's conversation view. ---
  const MAX_MESSAGES = 8, MAX_CHARS = 2500;

  // Each reader returns [{ el, user }] in page order, or null if the page is not that site's chat UI.
  const READERS = [
    () => { // ChatGPT: every turn carries its author role.
      const nodes = document.querySelectorAll("[data-message-author-role]");
      return nodes.length ? [...nodes].map((el) => ({ el, user: el.getAttribute("data-message-author-role") === "user" })) : null;
    },
    () => { // Claude: user turns are marked; every reply sits in a [data-is-streaming] container.
      if (!location.hostname.endsWith("claude.ai")) return null;
      const nodes = document.querySelectorAll('[data-testid="user-message"], [data-is-streaming]');
      return [...nodes].map((el) => ({ el, user: el.matches('[data-testid="user-message"]') }));
    },
    () => { // Gemini: custom elements per turn.
      const nodes = document.querySelectorAll("user-query, model-response");
      return nodes.length ? [...nodes].map((el) => ({ el, user: el.tagName.toLowerCase() === "user-query" })) : null;
    }
  ];

  function readConversation() {
    for (const reader of READERS) {
      const turns = reader();
      if (!turns) continue;
      const messages = turns
        .map(({ el, user }) => ({ role: user ? "user" : "assistant", text: (el.innerText || "").trim() }))
        .filter((m) => m.text)
        .slice(-MAX_MESSAGES)
        .map((m) => (m.text.length > MAX_CHARS ? { ...m, text: m.text.slice(0, MAX_CHARS) + "\n…" } : m));
      return { supported: true, messages };
    }
    return { supported: false, messages: [] };
  }

  chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
    if (msg?.type !== "read-conversation") return;
    sendResponse(readConversation());
  });
})();
