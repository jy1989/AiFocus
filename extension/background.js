"use strict";

importScripts("sites.js");

const HOST = "com.aifocus.bridge";
let port = null;
let profileId = null;
let snapshotTimer = null;
let reconnectTimer = null;
// tabId -> true/false while a content script reports generation state; missing = unknown.
const generating = new Map();

async function getProfileId() {
  const data = await chrome.storage.local.get("profileId");
  if (data.profileId) return data.profileId;
  const id = crypto.randomUUID();
  await chrome.storage.local.set({ profileId: id });
  return id;
}

function scheduleReconnect() {
  if (reconnectTimer) return;
  reconnectTimer = setTimeout(() => {
    reconnectTimer = null;
    connect();
  }, 4500);
}

function connect() {
  if (port || !profileId) return;
  try {
    const p = chrome.runtime.connectNative(HOST);
    port = p;
    p.onMessage.addListener(async (msg) => {
      if (!Number.isInteger(msg?.tabId)) return;
      if (msg.type === "conversation") {
        // Relay to the tab's content script, which reads the latest turns from the page.
        let reply = { supported: false, messages: [] };
        try {
          reply = await chrome.tabs.sendMessage(msg.tabId, { type: "read-conversation" }) || reply;
        } catch (error) {
          console.info("AI Focus: no conversation reader in tab", msg.tabId, error?.message);
        }
        if (port === p) p.postMessage({ type: "conversation", requestId: msg.requestId, ...reply });
        return;
      }
      if (msg.type !== "activate") return;
      try {
        const tab = await chrome.tabs.get(msg.tabId);
        if (!tab) return;
        await chrome.windows.update(tab.windowId, { focused: true });
        await chrome.tabs.update(tab.id, { active: true });
      } catch (error) {
        console.warn("AI Focus: tab is no longer available", error?.message);
      }
    });
    p.onDisconnect.addListener(() => {
      // Consume the browser error; otherwise Chrome reports Unchecked runtime.lastError.
      const disconnectReason = chrome.runtime.lastError?.message;
      if (disconnectReason) console.info('AI Focus native bridge:', disconnectReason);
      if (port === p) port = null;
      scheduleReconnect();
    });
    scheduleSnapshot();
  } catch {
    port = null;
    scheduleReconnect();
  }
}

// Favicons as PNG data URLs from Chrome's own favicon cache (no network, handles SVG icons).
// Keyed by the tab's favicon URL so a late-loading icon replaces the default globe.
const faviconCache = new Map();
async function faviconOf(tab) {
  let key;
  try { key = tab.favIconUrl || new URL(tab.url).origin; } catch { return ""; }
  if (faviconCache.has(key)) return faviconCache.get(key);
  let data = "";
  try {
    const url = new URL(chrome.runtime.getURL("/_favicon/"));
    url.searchParams.set("pageUrl", tab.url);
    url.searchParams.set("size", "32");
    const blob = await (await fetch(url)).blob();
    const bytes = new Uint8Array(await blob.arrayBuffer());
    let binary = "";
    for (const b of bytes) binary += String.fromCharCode(b);
    data = `data:${blob.type || "image/png"};base64,${btoa(binary)}`;
  } catch (error) {
    console.info("AI Focus: favicon unavailable", error?.message);
  }
  if (faviconCache.size > 200) faviconCache.clear();
  faviconCache.set(key, data);
  return data;
}

async function sendSnapshot() {
  if (!port || !profileId) return;
  try {
    // Chrome reports a window as focused only while it is the OS foreground window.
    const lastFocused = await chrome.windows.getLastFocused().catch(() => null);
    const focusedWindowId = lastFocused?.focused ? lastFocused.id : -1;
    const matches = (await chrome.tabs.query({}))
      .map((tab) => ({ tab, provider: providerOf(tab.url || "") }))
      .filter(({ tab, provider }) => tab.id !== undefined && provider);
    const tabs = await Promise.all(matches.map(async ({ tab, provider }) => ({
      id: tab.id,
      provider,
      windowId: tab.windowId,
      title: (tab.title || "").slice(0, 240),
      url: (tab.url || "").slice(0, 4096),
      active: !!tab.active,
      focused: !!tab.active && tab.windowId === focusedWindowId,
      generating: generating.has(tab.id) ? generating.get(tab.id) : null,
      icon: await faviconOf(tab)
    })));
    port.postMessage({
      type: "snapshot", profileId,
      browser: navigator.userAgent.includes("Edg/") ? "Edge" : "Chrome",
      tabs
    });
  } catch (error) {
    console.warn("AI Focus: snapshot failed", error?.message);
  }
}

function scheduleSnapshot() {
  if (snapshotTimer) clearTimeout(snapshotTimer);
  snapshotTimer = setTimeout(() => {
    snapshotTimer = null;
    if (!port) connect();
    else sendSnapshot();
  }, 250);
}

chrome.tabs.onCreated.addListener(scheduleSnapshot);
chrome.tabs.onRemoved.addListener((tabId) => { generating.delete(tabId); scheduleSnapshot(); });
chrome.runtime.onMessage.addListener((msg, sender) => {
  const tabId = sender.tab?.id;
  if (msg?.type !== "generation" || tabId === undefined) return;
  const value = !!msg.generating;
  const changed = generating.get(tabId) !== value;
  generating.set(tabId, value);
  if (changed) scheduleSnapshot();
});
chrome.tabs.onUpdated.addListener(scheduleSnapshot);
chrome.tabs.onActivated.addListener(scheduleSnapshot);
chrome.windows.onFocusChanged.addListener(scheduleSnapshot);
chrome.runtime.onStartup.addListener(() => { connect(); });
chrome.runtime.onInstalled.addListener(() => { connect(); injectIntoOpenTabs(); });

// content_scripts only reach pages loaded after install/reload; cover tabs that are already open.
async function injectIntoOpenTabs() {
  for (const tab of await chrome.tabs.query({})) {
    if (tab.id === undefined || !providerOf(tab.url || "")) continue;
    chrome.scripting.executeScript({ target: { tabId: tab.id }, files: ["sites.js", "content.js"] })
      .catch((error) => console.info("AI Focus: cannot inject into tab", tab.id, error?.message));
  }
}
chrome.alarms.onAlarm.addListener(() => {
  if (!port) connect();
  else sendSnapshot();
});

(async () => {
  profileId = await getProfileId();
  chrome.alarms.create("focus-sync", { periodInMinutes: 1 });
  connect();
})();
