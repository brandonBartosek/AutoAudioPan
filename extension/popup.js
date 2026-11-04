const $ = id => document.getElementById(id);

function render(cfg, app) {
  $('status').innerHTML = app
    ? '<span id="app">Controlled by the AutoAudioPan app</span>'
    : '<span id="local">Standalone (AutoAudioPan app not running)</span>';
  $('controls').style.opacity = app ? 0.4 : 1;
  $('controls').style.pointerEvents = app ? 'none' : 'auto';
  $('mode').value = cfg.mode;
  $('pan').value = Math.round(cfg.manualPan * 100);
  $('vol').value = cfg.volume;
  $('mute').checked = cfg.muted;
  $('panv').textContent = $('pan').value;
  $('volv').textContent = cfg.volume + '%';
}

function save() {
  const cfg = {
    mode: $('mode').value,
    manualPan: +$('pan').value / 100,
    volume: +$('vol').value,
    muted: $('mute').checked
  };
  $('panv').textContent = $('pan').value;
  $('volv').textContent = cfg.volume + '%';
  chrome.runtime.sendMessage({ type: 'setConfig', cfg });
}

chrome.runtime.sendMessage({ type: 'status' }).then(s => render(s.cfg, s.app));
for (const id of ['mode', 'pan', 'vol', 'mute']) $(id).addEventListener('input', save);
