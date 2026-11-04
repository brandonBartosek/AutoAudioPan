const APP_URL = 'http://127.0.0.1:47831/chrome';
const DEFAULTS = {
  mode: 'auto',
  manualPan: 0,
  volume: 100,
  muted: false,
  headTracking: {
    enabled: false, available: false, yawDegrees: 0, maxCorrectionDegrees: 45,
    maxPanMagnitude: 0.75, focusAttenuationEnabled: false, focusFloor: 0.55, focusWidth: 0.55
  }
};

const bridge = { ok: false, cfg: null, at: 0 };
let displayCache = { at: 0, left: 0, width: 1 };

async function desktop() {
  if (Date.now() - displayCache.at < 5000) return displayCache;
  const ds = await chrome.system.display.getInfo();
  const left = Math.min(...ds.map(d => d.bounds.left));
  const right = Math.max(...ds.map(d => d.bounds.left + d.bounds.width));
  displayCache = { at: Date.now(), left, width: Math.max(1, right - left) };
  return displayCache;
}

// Same mapping as the app: window center across the whole virtual desktop -> [-1, 1].
function panFor(win, d) {
  const center = win.left + win.width / 2;
  return Math.max(-1, Math.min(1, ((center - d.left) / d.width) * 2 - 1));
}

function correctPan(sourcePan, headTracking) {
  if (!headTracking || !headTracking.enabled || !headTracking.available)
    return Math.max(-0.75, Math.min(0.75, sourcePan));
  const limit = Math.max(0.001, Math.abs(+headTracking.maxCorrectionDegrees || 45));
  const correction = Math.max(-1, Math.min(1, (+headTracking.yawDegrees || 0) / limit));
  const pan = sourcePan - correction;
  const configuredCap = +headTracking.maxPanMagnitude;
  const cap = Number.isFinite(configuredCap)
    ? Math.max(0.01, Math.min(1, Math.abs(configuredCap))) : 0.75;
  return Math.max(-cap, Math.min(cap, pan));
}

function focusGain(sourcePan, headTracking) {
  if (!headTracking || !headTracking.enabled || !headTracking.available ||
      !headTracking.focusAttenuationEnabled) return 1;
  const limit = Math.max(0.001, Math.abs(+headTracking.maxCorrectionDegrees || 45));
  const direction = Math.max(-1, Math.min(1, (+headTracking.yawDegrees || 0) / limit));
  const distance = Math.abs(Math.max(-1, Math.min(1, sourcePan)) - direction);
  const configuredWidth = +headTracking.focusWidth;
  const configuredFloor = +headTracking.focusFloor;
  const width = Number.isFinite(configuredWidth) ? Math.max(0.05, Math.abs(configuredWidth)) : 0.55;
  const floor = Number.isFinite(configuredFloor) ? Math.max(0, Math.min(1, configuredFloor)) : 0.55;
  return floor + (1 - floor) * Math.exp(-(distance * distance) / (2 * width * width));
}

async function localConfig() {
  const { cfg } = await chrome.storage.local.get('cfg');
  return { ...DEFAULTS, ...(cfg || {}) };
}

async function refreshBridge(pan) {
  bridge.at = Date.now();
  try {
    const r = await fetch(APP_URL, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ pan }),
      signal: AbortSignal.timeout(800)
    });
    if (!r.ok) throw new Error(String(r.status));
    bridge.cfg = await r.json();
    bridge.ok = true;
  } catch {
    bridge.ok = false;
  }
}

async function handleTick(sender) {
  const winId = sender.tab && sender.tab.windowId;
  if (winId == null) return null;
  const [win, d] = await Promise.all([chrome.windows.get(winId), desktop()]);
  const windowPan = win.state === 'minimized' ? 0 : panFor(win, d);

  if (Date.now() - bridge.at > 1000) refreshBridge(windowPan);
  const cfg = bridge.ok ? bridge.cfg : await localConfig();

  const mode = String(cfg.mode || 'auto').toLowerCase();
  const sourcePan = mode === 'manual'
    ? +cfg.manualPan || 0
    : mode === 'off'
      ? 0
      : windowPan;
  const pan = mode === 'auto' ? correctPan(sourcePan, cfg.headTracking)
    : Math.max(-0.75, Math.min(0.75, sourcePan));
  const focus = mode === 'auto' ? focusGain(windowPan, cfg.headTracking) : 1;
  const vol = cfg.muted ? 0 : Math.max(0, +cfg.volume) / 100 * focus;
  return { pan, vol };
}

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (msg && msg.type === 'tick') {
    handleTick(sender).then(sendResponse, () => sendResponse(null));
    return true;
  }
  if (msg && msg.type === 'status') {
    localConfig().then(cfg => sendResponse({ app: bridge.ok && Date.now() - bridge.at < 5000, cfg }));
    return true;
  }
  if (msg && msg.type === 'setConfig') {
    chrome.storage.local.set({ cfg: msg.cfg }).then(() => sendResponse(true));
    return true;
  }
});
