# AutoAudioPan

Portable Windows 11 app that pans and controls the volume of each app's audio, and automatically pans an app left/center/right based on where its window sits across your monitors.

## Use
- Run `AutoAudioPan.exe` (no installer; single self-contained file). It lives in the system tray; closing the window hides it, use the tray menu to exit.
- Each app playing audio gets a row: **Auto** (follows window position across the whole virtual desktop), **Manual** (slider), **Off** (no panning), plus volume and mute.
- "Start with Windows" adds/removes a per-user Run entry. Settings are saved per app in `settings.json` next to the exe (falls back to `%AppData%\AutoAudioPan`).

## How it works
Per-app WASAPI process-loopback capture of the app's process tree -> pan/volume -> render to the default output. The original session volume is set to 0 while an app is being processed and restored when processing stops or the app exits (a recovery file in `%TEMP%` restores volumes after a crash). Process-loopback capture is taken before session volume, so muting the original does not silence the capture.

## Limits
- Control is per process tree, not per window: Chrome windows in one profile share one audio process and therefore one pan (the foreground/last-active window decides). Separate Chrome profiles or `--user-data-dir` instances are independent.
- ~40 ms added latency. Switching the default output device while apps are playing may require the app to re-pick it (restart the app).
- Apps are only processed once audio is active; this uses no driver.

## Build
```
dotnet publish src/AutoAudioPan -c Release -o publish
dotnet test tests/AutoAudioPan.Tests
```
