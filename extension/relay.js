// Isolated-world bridge between the page (main.js) and the service worker.
let active = false;
let dead = false;

window.addEventListener('message', e => {
  const d = e.data;
  if (e.source !== window || !d || d.aapStatus !== 1) return;
  const was = active;
  active = d.hooked > 0;
  if (active && !was) poll();
});

async function poll() {
  if (dead) return;
  try {
    const r = await chrome.runtime.sendMessage({ type: 'tick' });
    if (r) window.postMessage({ aap: 1, pan: r.pan, vol: r.vol }, '*');
  } catch (err) {
    if (String(err).includes('context invalidated')) dead = true;
  }
}

// Polling also keeps the MV3 service worker alive while audio is playing.
setInterval(() => { if (active) poll(); }, 500);
