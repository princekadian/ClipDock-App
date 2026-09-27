"use strict";

const BUTTON_ID = "clipdock-youtube-button";
const PANEL_ID = "clipdock-quality-panel";
const VIDEO_PATH = /^\/shorts\/[A-Za-z0-9_-]{6,32}\/?$/;
const ACTION_TARGETS = [
  "ytd-watch-metadata #top-level-buttons-computed",
  "ytd-watch-metadata #actions #menu",
  "#above-the-fold #top-level-buttons-computed",
  "ytd-reel-video-renderer[is-active] #actions"
];

let observer;
let ensureTimer;
let requestSequence = 0;
let preloadState;
let preloadPromise;

initialize();

function initialize() {
  scheduleButtonCheck(0);
  window.addEventListener("yt-navigate-finish", handleNavigation, { passive: true });
  window.addEventListener("yt-page-data-updated", () => scheduleButtonCheck(150), { passive: true });
  window.addEventListener("popstate", handleNavigation, { passive: true });

  observer = new MutationObserver(() => {
    if (!document.getElementById(BUTTON_ID) && getSupportedVideoUrl()) {
      scheduleButtonCheck(350);
    }
  });
  observer.observe(document.body, { childList: true, subtree: true });
  chrome.storage.onChanged.addListener((changes, area) => {
    if (area !== "local" || !changes.defaultContainer) return;
    preloadState = undefined;
    preloadPromise = undefined;
    const videoUrl = getSupportedVideoUrl();
    if (videoUrl) startPreload(videoUrl);
  });
}

function handleNavigation() {
  requestSequence += 1;
  preloadState = undefined;
  preloadPromise = undefined;
  closePanel();
  document.getElementById(BUTTON_ID)?.remove();
  scheduleButtonCheck(120);
}

function scheduleButtonCheck(delay) {
  clearTimeout(ensureTimer);
  ensureTimer = setTimeout(ensureButton, delay);
}

function ensureButton() {
  const videoUrl = getSupportedVideoUrl();
  if (!videoUrl) {
    document.getElementById(BUTTON_ID)?.remove();
    closePanel();
    return;
  }

  startPreload(videoUrl);

  if (document.getElementById(BUTTON_ID)?.isConnected) return;
  const target = ACTION_TARGETS.map((selector) => document.querySelector(selector)).find(Boolean);
  if (!target) return;

  const wrapper = document.createElement("div");
  wrapper.id = BUTTON_ID;
  wrapper.className = "clipdock-youtube-button";
  const button = document.createElement("button");
  button.type = "button";
  button.className = "clipdock-action-button";
  button.textContent = "Download";
  button.setAttribute("aria-haspopup", "dialog");
  button.setAttribute("aria-expanded", "false");
  button.addEventListener("click", (event) => {
    event.preventDefault();
    event.stopPropagation();
    openPanel(button);
  });
  wrapper.append(button);
  target.append(wrapper);
}

async function openPanel(anchor) {
  const videoUrl = getSupportedVideoUrl();
  if (!videoUrl) return;
  closePanel();

  const panel = createPanel();
  document.body.append(panel);
  positionPanel(panel, anchor);
  anchor.setAttribute("aria-expanded", "true");
  panel.querySelector(".clipdock-close").addEventListener("click", closePanel);
  document.addEventListener("keydown", closeOnEscape, true);
  document.addEventListener("pointerdown", closeOnOutsideClick, true);
  window.addEventListener("resize", closePanel, { once: true });

  const sequence = ++requestSequence;
  if (preloadState?.url === videoUrl && preloadState.video) {
    renderQualities(panel, preloadState.video, videoUrl, preloadState.container);
    return;
  }
  if (preloadState?.url === videoUrl && preloadPromise) {
    renderLoading(panel, "Preparing download options...");
    await preloadPromise;
    if (!isCurrent(sequence, panel)) return;
    if (preloadState?.video) {
      renderQualities(panel, preloadState.video, videoUrl, preloadState.container);
      return;
    }
  }

  renderLoading(panel, "Connecting to ClipDock...");
  const health = await sendMessage({ type: "health" });
  if (!isCurrent(sequence, panel)) return;
  if (!health.ok) {
    renderUnavailable(panel);
    return;
  }

  const settings = await sendMessage({ type: "getSettings" });
  if (!isCurrent(sequence, panel)) return;
  if (!settings.ok || !settings.data.bridgeToken) {
    renderPairing(panel);
    return;
  }

  renderLoading(panel, "Analyzing video...");
  const result = await sendMessage({
    type: "analyze",
    url: videoUrl,
    container: settings.data.defaultContainer || "mp4"
  });
  if (!isCurrent(sequence, panel)) return;
  if (!result.ok) {
    renderError(panel, result.error.message, result.error.code);
    return;
  }

  renderQualities(panel, result.data, videoUrl, settings.data.defaultContainer || "mp4");
}

function startPreload(videoUrl) {
  if (preloadState?.url === videoUrl || preloadPromise) return;
  preloadState = { url: videoUrl, container: "mp4", video: null };
  const operation = (async () => {
    const settings = await sendMessage({ type: "getSettings" });
    if (!settings.ok || !settings.data.bridgeToken || getSupportedVideoUrl() !== videoUrl) return;
    preloadState.container = settings.data.defaultContainer || "mp4";
    const result = await sendMessage({ type: "preload", url: videoUrl });
    if (result.ok && getSupportedVideoUrl() === videoUrl) preloadState.video = result.data;
  })();
  preloadPromise = operation;
  operation.finally(() => {
    if (preloadPromise === operation) preloadPromise = undefined;
  });
}

function createPanel() {
  const panel = document.createElement("section");
  panel.id = PANEL_ID;
  panel.className = "clipdock-panel";
  panel.setAttribute("role", "dialog");
  panel.setAttribute("aria-modal", "false");
  panel.setAttribute("aria-label", "Download video with ClipDock");
  panel.innerHTML = `
    <header class="clipdock-panel-header">
      <div>
        <strong>ClipDock</strong>
        <span class="clipdock-connection"><i></i> Desktop app</span>
      </div>
      <button type="button" class="clipdock-close" aria-label="Close">&#215;</button>
    </header>
    <div class="clipdock-panel-body"></div>`;
  return panel;
}

function renderLoading(panel, label) {
  const body = getBody(panel);
  body.replaceChildren();
  const status = element("div", "clipdock-loading");
  status.append(element("span", "clipdock-spinner"), textElement("span", label));
  const rows = element("div", "clipdock-skeleton-list");
  for (let index = 0; index < 4; index += 1) rows.append(element("div", "clipdock-skeleton-row"));
  body.append(status, rows);
}

function renderUnavailable(panel) {
  renderNotice(panel, {
    title: "ClipDock isn't running",
    message: "Start the Windows app, then try the connection again.",
    primaryLabel: "Try again",
    primaryAction: () => document.getElementById(BUTTON_ID)?.querySelector("button")?.click(),
    secondaryLabel: "Open ClipDock",
    secondaryAction: () => sendMessage({ type: "openApp" })
  });
}

function renderPairing(panel) {
  renderNotice(panel, {
    title: "Pair the extension",
    message: "Copy the pairing token from ClipDock Settings, then paste it into the extension settings.",
    primaryLabel: "Extension settings",
    primaryAction: () => sendMessage({ type: "openOptions" })
  });
}

function renderError(panel, message, code) {
  if (code === "NOT_PAIRED" || code === "PAIRING_REJECTED") {
    renderPairing(panel);
    return;
  }
  renderNotice(panel, {
    title: "Unable to analyze video",
    message,
    primaryLabel: "Try again",
    primaryAction: () => document.getElementById(BUTTON_ID)?.querySelector("button")?.click()
  });
}

function renderNotice(panel, options) {
  const body = getBody(panel);
  body.replaceChildren();
  const notice = element("div", "clipdock-notice");
  notice.append(textElement("h3", options.title), textElement("p", options.message));
  const actions = element("div", "clipdock-notice-actions");
  const primary = textElement("button", options.primaryLabel, "clipdock-primary");
  primary.type = "button";
  primary.addEventListener("click", () => {
    closePanel();
    options.primaryAction();
  });
  actions.append(primary);
  if (options.secondaryLabel) {
    const secondary = textElement("button", options.secondaryLabel, "clipdock-secondary");
    secondary.type = "button";
    secondary.addEventListener("click", options.secondaryAction);
    actions.append(secondary);
  }
  notice.append(actions);
  body.append(notice);
}

function renderQualities(panel, video, videoUrl, container) {
  const body = getBody(panel);
  body.replaceChildren();
  const summary = element("div", "clipdock-video-summary");
  if (video.thumbnailUrl) {
    const image = document.createElement("img");
    image.src = video.thumbnailUrl;
    image.alt = "";
    summary.append(image);
  }
  const copy = element("div");
  copy.append(textElement("h3", video.title), textElement("p", `${video.channel}  |  ${formatDuration(video.durationSeconds)}`));
  summary.append(copy);

  const heading = element("div", "clipdock-quality-heading");
  heading.append(textElement("strong", "Choose quality"), textElement("span", container.toUpperCase()));
  const list = element("div", "clipdock-quality-list");
  for (const quality of video.qualities) {
    const row = document.createElement("button");
    row.type = "button";
    row.className = "clipdock-quality-row";
    row.disabled = !quality.available;
    row.append(textElement("strong", quality.label));
    const size = quality.available ? formatBytes(quality.estimatedBytes, quality.exactSize) : "Unavailable";
    row.append(textElement("span", size));
    if (quality.available) {
      row.setAttribute("aria-label", `Download ${quality.label}, ${size}`);
      row.addEventListener("click", () => startDownload(panel, row, videoUrl, quality.height, container));
    }
    list.append(row);
  }
  body.append(summary, heading, list);
}

async function startDownload(panel, row, url, height, container) {
  const rows = panel.querySelectorAll(".clipdock-quality-row");
  rows.forEach((item) => { item.disabled = true; });
  const original = row.lastElementChild.textContent;
  row.lastElementChild.textContent = "Starting...";
  const result = await sendMessage({ type: "download", url, height, container });
  if (!panel.isConnected) return;
  if (!result.ok) {
    rows.forEach((item) => {
      if (!item.querySelector("span")?.textContent.includes("Unavailable")) item.disabled = false;
    });
    row.lastElementChild.textContent = original;
    renderError(panel, result.error.message, result.error.code);
    return;
  }

  const body = getBody(panel);
  body.replaceChildren();
  const confirmation = element("div", "clipdock-confirmation");
  confirmation.append(textElement("span", "\u2713", "clipdock-check"), textElement("h3", "Download added"),
    textElement("p", "ClipDock will handle the download in the background."));
  body.append(confirmation);
  setTimeout(() => {
    if (panel.isConnected) closePanel();
  }, 1800);
}

function positionPanel(panel, anchor) {
  const rect = anchor.getBoundingClientRect();
  const width = Math.min(356, window.innerWidth - 24);
  const left = Math.max(12, Math.min(window.innerWidth - width - 12, rect.right - width));
  const below = rect.bottom + 8;
  const estimatedHeight = 520;
  const top = below + estimatedHeight <= window.innerHeight ? below : Math.max(12, rect.top - estimatedHeight - 8);
  panel.style.width = `${width}px`;
  panel.style.left = `${left}px`;
  panel.style.top = `${top}px`;
}

function closePanel() {
  document.getElementById(PANEL_ID)?.remove();
  document.getElementById(BUTTON_ID)?.querySelector("button")?.setAttribute("aria-expanded", "false");
  document.removeEventListener("keydown", closeOnEscape, true);
  document.removeEventListener("pointerdown", closeOnOutsideClick, true);
}

function closeOnEscape(event) {
  if (event.key === "Escape") closePanel();
}

function closeOnOutsideClick(event) {
  const panel = document.getElementById(PANEL_ID);
  const button = document.getElementById(BUTTON_ID);
  if (panel && !panel.contains(event.target) && !button?.contains(event.target)) closePanel();
}

function getSupportedVideoUrl() {
  const url = new URL(window.location.href);
  if (url.pathname === "/watch" && /^[A-Za-z0-9_-]{6,32}$/.test(url.searchParams.get("v") || "")) {
    return `https://www.youtube.com/watch?v=${url.searchParams.get("v")}`;
  }
  if (VIDEO_PATH.test(url.pathname)) {
    return `https://www.youtube.com/watch?v=${url.pathname.split("/")[2]}`;
  }
  return null;
}

function isCurrent(sequence, panel) {
  return sequence === requestSequence && panel.isConnected;
}

function getBody(panel) {
  return panel.querySelector(".clipdock-panel-body");
}

function sendMessage(message) {
  return chrome.runtime.sendMessage(message).catch(() => ({
    ok: false,
    error: { code: "EXTENSION_ERROR", message: "The ClipDock extension could not complete the request." }
  }));
}

function formatBytes(bytes, exact) {
  if (!Number.isFinite(bytes) || bytes <= 0) return "Size unknown";
  const units = ["B", "KB", "MB", "GB", "TB"];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit += 1;
  }
  const digits = value >= 100 ? 0 : value >= 10 ? 1 : 2;
  return `${exact ? "" : "~"}${value.toFixed(digits)} ${units[unit]}`;
}

function formatDuration(seconds) {
  const total = Math.max(0, Math.floor(seconds || 0));
  const hours = Math.floor(total / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  const remainder = total % 60;
  return hours > 0
    ? `${hours}:${String(minutes).padStart(2, "0")}:${String(remainder).padStart(2, "0")}`
    : `${minutes}:${String(remainder).padStart(2, "0")}`;
}

function element(tagName, className) {
  const node = document.createElement(tagName);
  if (className) node.className = className;
  return node;
}

function textElement(tagName, text, className) {
  const node = element(tagName, className);
  node.textContent = text;
  return node;
}
