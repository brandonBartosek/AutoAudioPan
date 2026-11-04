// Isolated-world bridge between the page (main.js) and the service worker.
let hooked = false;
let dead = false;

window.addEventListener('message', e => {
  const d = e.data;
  if (e.source !== window || !d || d.aapStatus !== 1) return;
  const was = hooked;
  hooked = d.hooked > 0;
  if (hooked && !was) poll();
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

// Visible pages poll even before any audio plays so the app sees the extension as connected
// (and the service worker stays alive); hidden pages poll only while they have hooked audio.
setInterval(() => { if (hooked || document.visibilityState === 'visible') poll(); }, 1000);
