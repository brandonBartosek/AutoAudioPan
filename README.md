# AutoAudioPan

Portable Windows 11 app that pans and controls the volume of each app's audio, and automatically pans an app left/center/right based on where its window sits across your monitors.

## Use
- Run `AutoAudioPan.exe` (no installer; single self-contained file). It lives in the system tray; closing the window hides it, use the tray menu to exit. The Chrome extension is the supported way to keep the normal Chrome profile and still get per-window routing; AutoAudioPan never creates a replacement profile.
- Each app that produces sound during the session keeps a row after it becomes idle: **Auto** (follows window position across the whole virtual desktop), **Manual** (slider), **Off** (no panning), plus volume and mute. Known applications may be prepared in the backend before their first sound, but merely known idle apps stay hidden and empty parent groups are removed from the UI. Use **Hide row** on a row to remove it from the normal list; enable **Show hidden apps** to reveal hidden rows and unhide them. Session sound history is runtime-only; the manual hidden choice is stored with that app's existing settings.
- "Start with Windows" adds/removes a per-user Run entry. Settings are saved per app in `settings.json` next to the exe (falls back to `%AppData%\AutoAudioPan`).

## How it works
Per-app WASAPI process-loopback capture of the app's process tree -> pan/volume -> render to the default output. The original session volume is set to 0 while an app is being processed and restored when processing stops or the app exits (a recovery file in `%TEMP%` restores volumes after a crash). Process-loopback capture is taken before session volume, so muting the original does not silence the capture.

## Limits
- Without the browser extension, control is per process tree, not per window: Chrome windows in one profile share one audio process and therefore one pan. Chrome's "Audio playing" tab indicator is used to pick the audible window(s) (several are averaged).
- For independent per-window Chrome panning that keeps your logins and extensions, install the **browser extension** (below).
- ~40 ms added latency. Switching the default output device while apps are playing may require the app to re-pick it (restart the app).
- Apps are pre-registered when a known executable is already running so the pipeline can be ready before its first notification. This uses no driver; a future one-time audio component is not required by the current implementation.
- Experimental head tracking is disabled by default. When enabled, the optional OpenCV Windows runtime captures webcam frames locally, detects one face, and estimates yaw from facial landmarks. The UI reports tracking, temporary face loss, fallback, calibration, yaw, confidence, the configurable spatial pan cap (0.75 by default), and optional focus attenuation. Short detection gaps are held/predicted, including at the left/right bounds; only a genuine timeout starts a smooth return to normal window-position panning. The bundled landmark model is loaded only while tracking is active; camera frames are never saved or transmitted.
- Spatial output is capped below full one-ear routing so a source does not hard-pan to ±1. The cap is stored as `HeadTracking.MaxPanMagnitude` in `settings.json`; the default is `0.75` and it is applied consistently to native and Chrome-extension output. Focus attenuation is opt-in and off by default: sources near the current head direction remain at normal level, farther sources are smoothly reduced toward a nonzero floor, and row volume/mute still take precedence.

## Chrome extension (independent per-window panning)
Chrome's audio for all windows comes out of one process, so the app cannot separate windows. The extension in extension/ pans inside the page instead (Web Audio: media element -> gain -> stereo panner), using each window's position across all monitors, so it runs in your normal profile with your logins and extensions.
1. Open chrome://extensions, enable **Developer mode**, click **Load unpacked**, choose the extension folder.
2. Play audio in two windows on different monitors; each pans by its own position.
- With the app running, the extension polls http://127.0.0.1:47831 (accepts only the extension's origin). The app then stops capturing Chrome and the **Chrome row** (mode, manual pan, volume, mute) controls all windows through the extension. The bridge also publishes the same head-tracking cap and optional focus settings, and the extension applies each effect once. Without the app, use the extension popup.
- The extension requests `storage` and `system.display`, plus localhost access to `127.0.0.1`; it does not request account, cookie, history, or profile-management permissions. The helper owns the localhost bridge and returns only the current Chrome-row settings.
- Fallback: media the extension cannot route (DRM/EME, cross-origin media without CORS) is left unpanned and audible; if the extension is not running, the app's capture pipeline handles Chrome as before.
- Limitations: tabs in the same window share that window's pan; a page's audio is hooked once it is allowed to play (first click/keypress on pages that block autoplay); protected DRM/EME media and inaccessible cross-origin media are intentionally untouched; Developer-mode banner appears on Chrome startup for unpacked extensions. The bridge is a localhost control plane, not a separate Chrome profile or a global per-tab volume API.

## Build
```
dotnet test tests/AutoAudioPan.Tests
./build/Publish.ps1
```

`build\Publish.ps1` publishes a self-contained `win-x64` single-file build to `%USERPROFILE%\Desktop\AutoAudioPan` by default. Pass `-OutputPath C:\somewhere\AutoAudioPan` to override it. The script refuses to write into a non-project folder containing unrelated files.

For a manual build:
```
dotnet publish src/AutoAudioPan -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
dotnet test tests/AutoAudioPan.Tests
```

### Setup and troubleshooting
- Windows 11 x64 is required. No installer or admin rights are required for the helper; start-with-Windows uses the current user's Run key.
- Settings are stored beside the portable executable when writable, otherwise in `%AppData%\AutoAudioPan`. The app and extension keep the existing Chrome profile, cookies, logins, and extensions.
- If Chrome remains a single process row, confirm the extension is loaded in the same profile that is playing audio and that the helper is running. If a site uses DRM or blocks autoplay, leave it to Chrome's normal audio path.
- After replacing extension files, open `chrome://extensions`, click the extension's **Reload** button, then restart the helper so the localhost bridge and head-tracking state are reconnected. The extension's service worker must be running from the same profile that is playing audio; `http://127.0.0.1:47831/chrome` is the only bridge endpoint.
- The helper adds approximately 40 ms of latency. Device changes while audio is active may require restarting the helper.
- Head tracking is opt-in and uses `screen source angle - head yaw`, with neutral calibration, a small dead zone, smoothing, confidence thresholds, bounded velocity prediction, hysteresis/retention through brief loss, and a maximum correction. At an extreme pose, the last reliable correction is retained through the bounded timeout instead of snapping toward the default window-position value. The helper publishes only filtered yaw and settings over the localhost bridge; the extension applies them once relative to each window's screen-derived pan. Camera permission failures, unavailable cameras, and genuinely lost face samples fall back smoothly to normal window-position panning. The first activation may take a few seconds while the local landmark model loads. Headphones are the primary target; ordinary speakers generally need crosstalk cancellation and room-aware processing for comparable screen anchoring, so focus attenuation is less precise on speakers.
- The webcam provider adds a full OpenCV Windows native runtime and a bundled facial-landmark model to the portable build. This increases publish size and CPU use while enabled; no separate server or cloud service is required.
- To build future releases, run `build\Publish.ps1`; inspect the output folder before publishing because the script intentionally refuses to overwrite unrelated files.
