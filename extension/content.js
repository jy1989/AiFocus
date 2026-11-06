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
})();
