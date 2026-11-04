# AutoAudioPan

Portable Windows 11 app that pans and controls the volume of each app's audio, and automatically pans an app left/center/right based on where its window sits across your monitors.

## Use
- Run `AutoAudioPan.exe` (no installer; single self-contained file). It lives in the system tray; closing the window hides it, use the tray menu to exit.
- Each app playing audio gets a row: **Auto** (follows window position across the whole virtual desktop), **Manual** (slider), **Off** (no panning), plus volume and mute.
- "Start with Windows" adds/removes a per-user Run entry. Settings are saved per app in `settings.json` next to the exe (falls back to `%AppData%\AutoAudioPan`).

## How it works
Per-app WASAPI process-loopback capture of the app's process tree -> pan/volume -> render to the default output. The original session volume is set to 0 while an app is being processed and restored when processing stops or the app exits (a recovery file in `%TEMP%` restores volumes after a crash). Process-loopback capture is taken before session volume, so muting the original does not silence the capture.

## Limits
- Without the browser extension, control is per process tree, not per window: Chrome windows in one profile share one audio process and therefore one pan. Chrome's "Audio playing" tab indicator is used to pick the audible window(s) (several are averaged).
- For independent per-window Chrome panning that keeps your logins and extensions, install the **browser extension** (below). The "New Chrome window (separate audio)" button (separate profile, signed out) remains as a fallback.
- ~40 ms added latency. Switching the default output device while apps are playing may require the app to re-pick it (restart the app).
- Apps are only processed once audio is active; this uses no driver.

## Chrome extension (independent per-window panning)
Chrome's audio for all windows comes out of one process, so the app cannot separate windows. The extension in extension/ pans inside the page instead (Web Audio: media element -> gain -> stereo panner), using each window's position across all monitors, so it runs in your normal profile with your logins and extensions.
1. Open chrome://extensions, enable **Developer mode**, click **Load unpacked**, choose the extension folder.
2. Play audio in two windows on different monitors; each pans by its own position.
- With the app running, the extension polls http://127.0.0.1:47831 (accepts only the extension's origin). The app then stops capturing Chrome and the **Chrome row** (mode, manual pan, volume, mute) controls all windows through the extension. Without the app, use the extension popup.
- Fallback: media the extension cannot route (DRM/EME, cross-origin media without CORS) is left unpanned and audible; if the extension is not running, the app's capture pipeline handles Chrome as before.
- Limitations: tabs in the same window share that window's pan; a page's audio is hooked once it is allowed to play (first click/keypress on pages that block autoplay); Developer-mode banner appears on Chrome startup for unpacked extensions.

## Build
```
dotnet publish src/AutoAudioPan -c Release -o publish
dotnet test tests/AutoAudioPan.Tests
```
