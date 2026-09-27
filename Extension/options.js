"use strict";

const tokenInput = document.getElementById("token");
const showToken = document.getElementById("show-token");
const saveButton = document.getElementById("save");
const formatSelect = document.getElementById("format");
const status = document.getElementById("status");
const message = document.getElementById("message");

document.getElementById("extension-id").textContent = chrome.runtime.id;
showToken.addEventListener("change", () => { tokenInput.type = showToken.checked ? "text" : "password"; });
saveButton.addEventListener("click", saveAndTest);
formatSelect.addEventListener("change", () => chrome.storage.local.set({ defaultContainer: formatSelect.value }));
load();

async function load() {
  const settings = await chrome.storage.local.get(["bridgeToken", "defaultContainer"]);
  tokenInput.value = settings.bridgeToken || "";
  formatSelect.value = settings.defaultContainer || "mp4";
  await checkHealth();
}

async function checkHealth() {
  setStatus("checking", "Checking");
  const result = await send({ type: "health" });
  setStatus(result.ok ? "connected" : "disconnected", result.ok ? "App connected" : "App not running");
}

async function saveAndTest() {
  const token = tokenInput.value.trim();
  clearMessage();
  if (!/^[a-fA-F0-9]{64}$/.test(token)) {
    showMessage("The pairing token must be the 64-character value shown in ClipDock.", false);
    return;
  }

  saveButton.disabled = true;
  saveButton.textContent = "Testing...";
  await chrome.storage.local.set({ bridgeToken: token, defaultContainer: formatSelect.value });
  const result = await send({ type: "verifyToken" });
  saveButton.disabled = false;
  saveButton.textContent = "Save and test";
  if (result.ok && result.data.valid) {
    setStatus("connected", "Paired");
    showMessage("ClipDock is connected and the token was accepted.", true);
    return;
  }

  if (result.error?.code === "APP_UNAVAILABLE") setStatus("disconnected", "App not running");
  showMessage(result.error?.message || "The pairing token could not be verified.", false);
}

function send(payload) {
  return chrome.runtime.sendMessage(payload).catch(() => ({
    ok: false,
    error: { code: "EXTENSION_ERROR", message: "The ClipDock extension could not complete the request." }
  }));
}

function setStatus(state, label) {
  status.className = `status ${state}`;
  status.querySelector("b").textContent = label;
}

function clearMessage() {
  message.className = "message";
  message.textContent = "";
}

function showMessage(text, succeeded) {
  message.className = `message ${succeeded ? "success" : "error"}`;
  message.textContent = text;
}
