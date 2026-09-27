"use strict";

const API_BASE = "http://127.0.0.1:38765";
const ZERO_JOB_ID = "00000000-0000-0000-0000-000000000000";
const ANALYSIS_CACHE_PREFIX = "clipdock-analysis:";
const ANALYSIS_CACHE_TTL_MS = 30 * 60 * 1000;
const MAX_CACHED_VIDEOS = 12;
const analysisRequests = new Map();

chrome.runtime.onInstalled.addListener((details) => {
  if (details.reason === "install") {
    chrome.runtime.openOptionsPage();
  }
});

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (sender.id !== chrome.runtime.id || !message || typeof message.type !== "string") {
    sendResponse(failure("INVALID_REQUEST", "ClipDock rejected an invalid extension request."));
    return false;
  }

  handleMessage(message)
    .then(sendResponse)
    .catch((error) => sendResponse(normalizeError(error)));
  return true;
});

async function handleMessage(message) {
  switch (message.type) {
    case "health":
      return success(await apiRequest("/health", { timeoutMs: 2500, authenticated: false }));
    case "analyze":
      return success(await analyzeWithCache(message.url, message.container));
    case "preload": {
      const settings = await chrome.storage.local.get("defaultContainer");
      return success(await analyzeWithCache(message.url, settings.defaultContainer || "mp4"));
    }
    case "getCachedAnalysis":
      return success(await getCachedAnalysis(message.url, message.container));
    case "download":
      return success(await apiRequest("/download", {
        method: "POST",
        body: {
          url: validateYouTubeUrl(message.url),
          height: validateHeight(message.height),
          container: normalizeContainer(message.container)
        },
        timeoutMs: 60000
      }));
    case "status":
      return success(await apiRequest(`/status?id=${encodeURIComponent(message.jobId)}`, { timeoutMs: 5000 }));
    case "verifyToken":
      return verifyStoredToken();
    case "getSettings":
      return success(await chrome.storage.local.get(["bridgeToken", "defaultContainer"]));
    case "openOptions":
      await chrome.runtime.openOptionsPage();
      return success({ opened: true });
    case "openApp":
      await chrome.tabs.create({ url: "clipdock://open" });
      return success({ opened: true });
    default:
      return failure("INVALID_REQUEST", "ClipDock does not support that operation.");
  }
}

async function apiRequest(path, options = {}) {
  const {
    method = "GET",
    body,
    timeoutMs = 10000,
    authenticated = true,
    allowStatuses = []
  } = options;
  const headers = { "Accept": "application/json" };

  if (authenticated) {
    const { bridgeToken } = await chrome.storage.local.get("bridgeToken");
    if (!bridgeToken || typeof bridgeToken !== "string") {
      throw new ExtensionError("NOT_PAIRED", "Pair this extension with ClipDock before downloading.");
    }
    headers["X-ClipDock-Token"] = bridgeToken.trim();
  }

  if (body !== undefined) headers["Content-Type"] = "application/json";
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), timeoutMs);
  try {
    const response = await fetch(`${API_BASE}${path}`, {
      method,
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
      signal: controller.signal,
      cache: "no-store"
    });
    const payload = await readPayload(response);
    if (!response.ok && !allowStatuses.includes(response.status)) {
      if (response.status === 401) {
        throw new ExtensionError("PAIRING_REJECTED", "The pairing token was not accepted. Copy it again from ClipDock settings.");
      }
      if (response.status === 429) {
        throw new ExtensionError("RATE_LIMITED", "Too many requests. Wait a moment and try again.");
      }
      throw new ExtensionError("DESKTOP_ERROR", payload?.error || "ClipDock could not complete the request.", response.status);
    }
    return { payload, status: response.status };
  } catch (error) {
    if (error instanceof ExtensionError) throw error;
    if (error?.name === "AbortError") {
      throw new ExtensionError("TIMEOUT", "ClipDock took too long to respond. Try again.");
    }
    throw new ExtensionError("APP_UNAVAILABLE", "ClipDock is not running. Start the desktop app and try again.");
  } finally {
    clearTimeout(timeout);
  }
}

async function verifyStoredToken() {
  const result = await apiRequest(`/status?id=${ZERO_JOB_ID}`, { timeoutMs: 3000, allowStatuses: [404] });
  return success({ valid: result.status === 404 });
}

async function analyzeWithCache(urlValue, containerValue) {
  const url = validateYouTubeUrl(urlValue);
  const container = normalizeContainer(containerValue);
  const cached = await getCachedAnalysis(url, container);
  if (cached.cached) return cached.video;

  const key = analysisCacheKey(url, container);
  if (analysisRequests.has(key)) return analysisRequests.get(key);
  const request = (async () => {
    const result = await apiRequest("/analyze", {
      method: "POST",
      body: { url, container },
      timeoutMs: 60000
    });
    await chrome.storage.session.set({
      [key]: { storedAt: Date.now(), video: result.payload }
    });
    await pruneAnalysisCache();
    return result.payload;
  })();
  analysisRequests.set(key, request);
  try {
    return await request;
  } finally {
    analysisRequests.delete(key);
  }
}

async function getCachedAnalysis(urlValue, containerValue) {
  const url = validateYouTubeUrl(urlValue);
  const container = normalizeContainer(containerValue);
  const key = analysisCacheKey(url, container);
  const stored = (await chrome.storage.session.get(key))[key];
  if (!stored || !stored.video || Date.now() - stored.storedAt > ANALYSIS_CACHE_TTL_MS) {
    if (stored) await chrome.storage.session.remove(key);
    return { cached: false, video: null };
  }
  return { cached: true, video: stored.video };
}

async function pruneAnalysisCache() {
  const all = await chrome.storage.session.get(null);
  const entries = Object.entries(all)
    .filter(([key]) => key.startsWith(ANALYSIS_CACHE_PREFIX))
    .sort((left, right) => (right[1]?.storedAt || 0) - (left[1]?.storedAt || 0));
  const expired = entries
    .filter(([, value]) => Date.now() - (value?.storedAt || 0) > ANALYSIS_CACHE_TTL_MS)
    .map(([key]) => key);
  const overflow = entries.slice(MAX_CACHED_VIDEOS).map(([key]) => key);
  const remove = [...new Set([...expired, ...overflow])];
  if (remove.length) await chrome.storage.session.remove(remove);
}

function analysisCacheKey(urlValue, container) {
  const url = new URL(urlValue);
  const id = url.hostname === "youtu.be" ? url.pathname.slice(1) : url.searchParams.get("v") || url.pathname.split("/")[2];
  return `${ANALYSIS_CACHE_PREFIX}${container}:${id}`;
}

async function readPayload(response) {
  const text = await response.text();
  if (!text) return null;
  try {
    return JSON.parse(text);
  } catch {
    throw new ExtensionError("INVALID_RESPONSE", "ClipDock returned an unreadable response.");
  }
}

function validateYouTubeUrl(value) {
  let url;
  try {
    url = new URL(value);
  } catch {
    throw new ExtensionError("UNSUPPORTED_PAGE", "Open a supported YouTube video first.");
  }

  const allowedHosts = new Set(["youtube.com", "www.youtube.com", "m.youtube.com", "youtu.be"]);
  const isWatch = url.pathname === "/watch" && /^[A-Za-z0-9_-]{6,32}$/.test(url.searchParams.get("v") || "");
  const isShort = /^\/shorts\/[A-Za-z0-9_-]{6,32}\/?$/.test(url.pathname);
  const isShortLink = url.hostname === "youtu.be" && /^\/[A-Za-z0-9_-]{6,32}\/?$/.test(url.pathname);
  if (url.protocol !== "https:" || !allowedHosts.has(url.hostname) || (!isWatch && !isShort && !isShortLink)) {
    throw new ExtensionError("UNSUPPORTED_PAGE", "Open a supported YouTube video first.");
  }
  return url.href;
}

function validateHeight(value) {
  const height = Number(value);
  if (![2160, 1440, 1080, 720, 480, 360, 240, 144].includes(height)) {
    throw new ExtensionError("INVALID_QUALITY", "Choose an available video quality.");
  }
  return height;
}

function normalizeContainer(value) {
  const normalized = String(value || "mp4").toLowerCase();
  if (!new Set(["mp4", "mkv", "webm"]).has(normalized)) return "mp4";
  return normalized;
}

function success(data) {
  return { ok: true, data: data?.payload ?? data };
}

function failure(code, message, detail) {
  return { ok: false, error: { code, message, detail } };
}

function normalizeError(error) {
  if (error instanceof ExtensionError) return failure(error.code, error.message, error.status);
  return failure("UNEXPECTED", "The extension encountered an unexpected problem.");
}

class ExtensionError extends Error {
  constructor(code, message, status) {
    super(message);
    this.name = "ExtensionError";
    this.code = code;
    this.status = status;
  }
}
