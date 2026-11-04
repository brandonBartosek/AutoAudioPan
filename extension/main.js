// Runs in the page's MAIN world: routes every playing <audio>/<video> through Gain -> StereoPanner.
(() => {
  if (window.__aapInstalled) return;
  Object.defineProperty(window, '__aapInstalled', { value: true });

  const state = { pan: 0, vol: 1 };
  const nodes = [];
  const deferred = new Set();
  let ctx = null;

  const getCtx = () => ctx || (ctx = new AudioContext());

  // createMediaElementSource on cross-origin media without CORS outputs silence, and EME (DRM) media
  // cannot be routed at all. Leave those elements untouched so they stay audible (just not panned).
  function routable(el) {
    if (el.mediaKeys) return false;
    const src = el.currentSrc || el.src;
    if (!src || src.startsWith('blob:') || src.startsWith('data:') || src.startsWith('mediastream:')) return true;
    try {
      const u = new URL(src, location.href);
      return u.origin === location.origin || el.crossOrigin !== null;
    } catch { return false; }
  }

  function post(extra) {
    window.postMessage({ aapStatus: 1, hooked: nodes.length, ...extra }, '*');
  }

  function apply() {
    for (const n of nodes) {
      n.pan.pan.setTargetAtTime(state.pan, getCtx().currentTime, 0.03);
      n.gain.gain.setTargetAtTime(state.vol, getCtx().currentTime, 0.03);
    }
  }

  function hook(el) {
    if (!(el instanceof HTMLMediaElement) || el.__aap) return;
    if (!routable(el)) { el.__aap = 'skipped'; return; }
    const c = getCtx();
    // A suspended context silences a hooked element, so wait until the page is allowed to run audio.
    if (c.state !== 'running') {
      c.resume().catch(() => {});
      if (c.state !== 'running') { deferred.add(el); return; }
    }
    try {
      const src = c.createMediaElementSource(el);
      const gain = c.createGain();
      const pan = c.createStereoPanner();
      src.connect(gain).connect(pan).connect(c.destination);
      nodes.push({ gain, pan, el });
      el.__aap = 'hooked';
    } catch { el.__aap = 'failed'; return; }
    deferred.delete(el);
    apply();
    post();
  }

  function retry() {
    if (!ctx) return;
    ctx.resume().then(() => { for (const el of [...deferred]) hook(el); }).catch(() => {});
  }

  function scan(root) {
    root.querySelectorAll && root.querySelectorAll('audio,video').forEach(el => { if (!el.paused) hook(el); });
  }

  const origPlay = HTMLMediaElement.prototype.play;
  HTMLMediaElement.prototype.play = function () {
    const p = origPlay.apply(this, arguments);
    hook(this);
    return p;
  };

  addEventListener('playing', e => hook(e.target), true);
  addEventListener('pointerdown', retry, true);
  addEventListener('keydown', retry, true);
  new MutationObserver(() => scan(document)).observe(document, { childList: true, subtree: true });

  window.addEventListener('message', e => {
    const d = e.data;
    if (e.source !== window || !d || d.aap !== 1) return;
    const pan = +d.pan;
    state.pan = Number.isFinite(pan) ? Math.max(-0.75, Math.min(0.75, pan)) : 0;
    state.vol = Math.max(0, Math.min(4, d.vol == null ? 1 : +d.vol));
    apply();
  });

  // Test/diagnostic helper: measures per-channel level of the first hooked element.
  Object.defineProperty(window, '__aap', {
    value: {
      get state() { return { ...state, hooked: nodes.length, deferred: deferred.size, ctx: ctx && ctx.state }; },
      levels() {
        const n = nodes[0];
        if (!n) return null;
        if (!n.aL) {
          const c = getCtx();
          const sp = c.createChannelSplitter(2);
          n.aL = c.createAnalyser(); n.aR = c.createAnalyser();
          n.pan.connect(sp); sp.connect(n.aL, 0); sp.connect(n.aR, 1);
          return null;
        }
        const rms = a => { const b = new Float32Array(a.fftSize); a.getFloatTimeDomainData(b); let s = 0; for (const v of b) s += v * v; return Math.sqrt(s / b.length); };
        return { L: rms(n.aL), R: rms(n.aR) };
      }
    }
  });
})();
