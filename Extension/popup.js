"use strict";

const content = document.getElementById("content");
const connection = document.getElementById("connection");
const settingsButton = document.getElementById("settings-button");
let currentUrl;
let currentContainer = "mp4";
let selectedQuality;
let videoData;

settingsButton.addEventListener("click", () => chrome.runtime.openOptionsPage());
initialize();

async function initialize() {
  setConnection("checking", "Checking");
  renderLoading("Connecting to ClipDock...");
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  currentUrl = getSupportedVideoUrl(tab?.url);
  if (!currentUrl) {
    setConnection("checking", "Idle");
    renderState("Open a YouTube video", "ClipDock works on YouTube watch pages and Shorts.");
    return;
  }

  const settings = await send({ type: "getSettings" });
  currentContainer = settings.ok ? settings.data.defaultContainer || "mp4" : "mp4";
  if (settings.ok && settings.data.bridgeToken) {
    const cached = await send({ type: "getCachedAnalysis", url: currentUrl, container: currentContainer });
    if (cached.ok && cached.data.cached && cached.data.video) {
      useVideoData(cached.data.video);
      setConnection("connected", "Ready");
      send({ type: "health" }).then((health) => {
        setConnection(health.ok ? "connected" : "disconnected", health.ok ? "Connected" : "App not running");
      });
      return;
    }
  }

  const health = await send({ type: "health" });
  if (!health.ok) {
    setConnection("disconnected", "Not connected");
    renderState("ClipDock isn't running", "Start the Windows app and try again.", [
      action("Try again", "primary", initialize),
      action("Open ClipDock", "secondary", () => send({ type: "openApp" }))
    ]);
    return;
  }

  setConnection("connected", "Connected");
  if (!settings.ok || !settings.data.bridgeToken) {
    renderState("Pair the extension", "Copy the pairing token from ClipDock Settings, then add it to the extension.", [
      action("Pair ClipDock", "primary", () => chrome.runtime.openOptionsPage())
    ]);
    return;
  }

  await analyze();
}

async function analyze() {
  selectedQuality = undefined;
  renderLoading("Analyzing video...");
  const result = await send({ type: "analyze", url: currentUrl, container: currentContainer });
  if (!result.ok) {
    if (result.error.code === "NOT_PAIRED" || result.error.code === "PAIRING_REJECTED") {
      renderState("Pairing required", result.error.message, [action("Extension settings", "primary", () => chrome.runtime.openOptionsPage())]);
    } else {
      renderState("Unable to analyze video", result.error.message, [action("Try again", "primary", analyze)]);
    }
    return;
  }
  useVideoData(result.data);
}

function useVideoData(video) {
  videoData = video;
  selectedQuality = videoData.qualities.find((item) => item.height === 1080 && item.available)
    || videoData.qualities.find((item) => item.available);
  renderVideo();
}

function renderVideo() {
  content.replaceChildren();
  const summary = node("section", "video");
  if (videoData.thumbnailUrl) {
    const image = document.createElement("img");
    image.src = videoData.thumbnailUrl;
    image.alt = "";
    summary.append(image);
  }
  const details = node("div");
  details.append(textNode("h2", videoData.title), textNode("p", `${videoData.channel}  |  ${formatDuration(videoData.durationSeconds)}`));
  summary.append(details);

  const toolbar = node("div", "quality-toolbar");
  toolbar.append(textNode("strong", "Quality"));
  const containerSelect = document.createElement("select");
  containerSelect.setAttribute("aria-label", "Output format");
  for (const value of ["mp4", "mkv", "webm"]) {
    const option = document.createElement("option");
    option.value = value;
    option.textContent = value.toUpperCase();
    option.selected = value === currentContainer;
    containerSelect.append(option);
  }
  containerSelect.addEventListener("change", async () => {
    currentContainer = containerSelect.value;
    await chrome.storage.local.set({ defaultContainer: currentContainer });
    await analyze();
  });
  toolbar.append(containerSelect);

  const list = node("div", "quality-list");
  for (const quality of videoData.qualities) {
    const row = document.createElement("button");
    row.type = "button";
    row.className = `quality-row${selectedQuality?.height === quality.height ? " selected" : ""}`;
    row.disabled = !quality.available;
    const radio = node("span", "radio");
    radio.setAttribute("aria-hidden", "true");
    row.append(radio, textNode("span", quality.label, "quality-label"));
    row.append(textNode("span", quality.available ? formatBytes(quality.estimatedBytes, quality.exactSize) : "Unavailable", "quality-size"));
    if (quality.available) {
      row.setAttribute("aria-label", `Select ${quality.label}`);
      row.addEventListener("click", () => {
        selectedQuality = quality;
        renderVideo();
      });
    }
    list.append(row);
  }

  const bar = node("div", "download-bar");
  const downloadButton = textNode("button", "Download", "");
  downloadButton.id = "download-button";
  downloadButton.type = "button";
  downloadButton.disabled = !selectedQuality;
  downloadButton.addEventListener("click", startDownload);
  bar.append(downloadButton);
  content.append(summary, toolbar, list, bar);
}

async function startDownload() {
  const button = document.getElementById("download-button");
  button.disabled = true;
  button.textContent = "Starting...";
  const result = await send({
    type: "download",
    url: currentUrl,
    height: selectedQuality.height,
    container: currentContainer
  });
  if (!result.ok) {
    renderState("Download not started", result.error.message, [action("Try again", "primary", renderVideo)]);
    return;
  }

  content.replaceChildren();
  const success = node("section", "success-state");
  success.append(textNode("span", "\u2713", "success-mark"), textNode("h2", "Download added"),
    textNode("p", "Track progress in the ClipDock desktop app."));
  content.append(success);
}

function renderLoading(label) {
  content.replaceChildren();
  const wrapper = node("div", "loading-state");
  wrapper.append(node("span", "spinner"), textNode("span", label));
  content.append(wrapper);
}

function renderState(title, message, actions = []) {
  content.replaceChildren();
  const wrapper = node("section", "state");
  wrapper.append(textNode("h2", title), textNode("p", message));
  if (actions.length) {
    const actionRow = node("div", "actions");
    for (const item of actions) actionRow.append(item);
    wrapper.append(actionRow);
  }
  content.append(wrapper);
}

function action(label, className, handler) {
  const button = textNode("button", label, className);
  button.type = "button";
  button.addEventListener("click", handler);
  return button;
}

function setConnection(state, label) {
  connection.className = `connection ${state}`;
  connection.querySelector("b").textContent = label;
}

function getSupportedVideoUrl(value) {
  if (!value) return null;
  try {
    const url = new URL(value);
    if (!["youtube.com", "www.youtube.com", "m.youtube.com"].includes(url.hostname)) return null;
    const watchId = url.pathname === "/watch" ? url.searchParams.get("v") : null;
    const shortMatch = url.pathname.match(/^\/shorts\/([A-Za-z0-9_-]{6,32})\/?$/);
    const id = /^[A-Za-z0-9_-]{6,32}$/.test(watchId || "") ? watchId : shortMatch?.[1];
    return id ? `https://www.youtube.com/watch?v=${id}` : null;
  } catch {
    return null;
  }
}

function send(message) {
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

function node(tagName, className) {
  const element = document.createElement(tagName);
  if (className) element.className = className;
  return element;
}

function textNode(tagName, text, className) {
  const element = node(tagName, className);
  element.textContent = text;
  return element;
}
