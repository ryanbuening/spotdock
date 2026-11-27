# AGENTS.md

**SpotDock** is a Spotify-only fork of [Priler/now-playing-taskbar-widget](https://github.com/Priler/now-playing-taskbar-widget).
It supports **Spotify Desktop** (classic Win32 + Microsoft Store via Windows SMTC)
and **Spotify Web / browser PWA** (`https://open.spotify.com/*` via the `SpotDock Bridge`
extension), while ignoring all other media apps and websites.

## Architecture overview

- **Spotify Desktop (`MediaService.cs`)**: Filters Windows SMTC sessions strictly
  to `SourceAppUserModelId` containing `"spotify"`, watching only Spotify sessions
  and never falling back to other players or `GetCurrentSession()`.
- **Spotify Web / PWA (`src/spotdock-bridge/` + `BrowserBridge.cs`)**: The MV3
  extension (`SpotDock Bridge`) is scoped to `https://open.spotify.com/*` and
  reads `navigator.mediaSession` plus Spotify Web player controls/slider state.
  `BrowserBridge` runs a local WebSocket server on `127.0.0.1:45219` (accepting
  only `Origin: chrome-extension://`) and validates that incoming state reports
  come from `open.spotify.com` / `*.spotify.com`. Paused Spotify Web tabs stay
  visible indefinitely while the tab/socket stays alive (the old 30s YouTube
  quiet-timeout was removed).
- **Source arbitration (`MediaHub.cs`)**: Combines SMTC (Spotify Desktop) and
  `BrowserBridge` (Spotify Web) behind one shared `MediaHub.Shared` seam so
  additional sources (such as Spotify Connect) can be added later without
  changing `WidgetWindow.cs`.
- **Visibility (`WidgetWindow.cs`)**: The widget is shown whenever Spotify has an
  active track (playing or paused, with a 1.2s grace period across track
  transitions) and hides whenever no track is loaded or Spotify is closed.

## What it consists of

| File | Role |
|---|---|
| `src/spotdock-bridge/` | MV3 extension (`SpotDock Bridge`): scoped to `open.spotify.com`, reads `navigator.mediaSession` and player controls |
| `src/SpotDock/BrowserBridge.cs` | The widget's WebSocket server on `127.0.0.1:45219`, accepts `Origin: chrome-extension://` and `*.spotify.com` hosts |
| `src/SpotDock/MediaHub.cs` | The shared source: Spotify SMTC plus Spotify Web behind a single interface |
| `src/SpotDock/SourceActivator.cs` | A click on the title reveals the Spotify window found by AUMID (or the Spotify Web tab through the extension) |
| `src/SpotDock/WidgetWindow.cs` | Win32 layered taskbar window, positioning, animations, input, and native context menu |
| `src/SpotDock/UiStrings.cs` | English UI strings for menus, idle state, and update prompts |
| `src/SpotDock/D2DRenderer.cs` | Software Direct2D (`ID2D1DCRenderTarget`) + DirectWrite + WIC rendering into a 32-bpp DIB surface |
| `src/SpotDock/UiThread.cs` | STA message-only dispatch window and `UiSynchronizationContext` for async continuations |
| `src/SpotDock/NativeUia.cs` | AOT-safe native COM `IUIAutomation` helpers (`TaskbarAnchors.cs`, `SpotifyUiaService.cs`) |
| `tests/SpotDock.Tests/` | xUnit tests for arbitration, WebSocket bridge, settings, and release parsing |
| `build/setup.iss` | Inno Setup 6 installer script outputting into `artifacts/installer/` |

The window (`WidgetWindow.cs`) knows nothing about the browser: only the type of
its `_media` field changed from `MediaService` to `MediaHub`.

## Pitfalls already stepped on

- **The main window is not found by the absence of an owner alone.** Clicking the
  title did not open AIMP. Minimized to the tray, it has **eighteen** top-level
  windows without an owner, all hidden; several scored the same, the first one won
  — the `TAIMPQuickFileInfoWindow 'QFI'` popup. The widget honestly activated it,
  and from the outside the click simply did not work.
  What tells them apart is their style:

  ```
  TAIMPQuickFileInfoWindow 'QFI'    480x260   TOOLWINDOW
  TAIMPMainForm 'Ice Cube - ...'   1110x626   APPWINDOW CAPTION
  ```

  Service windows are marked `WS_EX_TOOLWINDOW` and never appear on the taskbar —
  they are skipped together with zero-sized ones. Then the score:
  `WS_EX_APPWINDOW` weighs most, then visibility, a title, a frame; on a tie the
  larger window wins.
- **A program that hides its window itself is only helped by a second launch.**
  For AIMP in the tray neither `SW_SHOW`, nor `SW_SHOWNORMAL`, nor `SW_RESTORE`
  works (verified): the window becomes active while staying hidden. Launching the
  same exe makes the already running copy show its window, and the second one
  exits at once — verified, only one process remains. This is done only when the
  window is still hidden, otherwise ordinary programs would get a second copy.
- **Fullscreen mode is detected by Z-order, not by the active window.** The check
  looked at `GetForegroundWindow`, and leaving a fullscreen video for another
  monitor made the window there active, the check stopped firing — and the widget
  popped up over the video (while the taskbar stayed hidden). Now the topmost
  window in Z-order ON ITS OWN monitor is taken, whoever is active:
  `EnumWindows` goes top to bottom, the first suitable window is the topmost one.
  Suitable means visible, not minimized, without an owner, not ours and **not
  cloaked according to DWM**: Store apps have plenty of windows that
  `IsWindowVisible` reports as visible although they are not on screen, and
  without the `DWMWA_CLOAKED` check exactly such a window ended up topmost.
  Measured on two monitors: on the external one the topmost window is a browser
  window exactly matching the monitor bounds — the widget hides.
  **Taskbars and small windows are skipped during the walk rather than stopping
  it.** Otherwise one foreign trinket on top of everything is enough to blind the
  check: on a single monitor a 36x45 window (`TkTopLevel`) was found on top, the
  search stopped at it and decided there was no fullscreen app — the widget popped
  up over the video again. A window smaller than half the monitor area counts as
  small; the first one that actually occupies the monitor decides.
- **The track source is per process, not per window.** The widget has as many
  windows as there are taskbars selected. `MediaHub` was created in every window,
  and with it its own `BrowserBridge` — on the same port 45219. The second window
  did not get the port:

  ```
  Browser bridge could not listen on 127.0.0.1:45219:
  Only one usage of each socket address is normally permitted
  ```

  It looked like "the widget does not work on the second monitor": it showed the
  system session (SMTC is read by every window itself) but not music from the
  browser, and the extension had nowhere to reach. Now it is `MediaHub.Shared`,
  one for all; closing a window only unsubscribes, and the hub is shut down when
  the app exits. As a side effect, half as many SMTC calls and one album-art
  download for everyone.
- **A finished SMTC session stays in the list.** A listened-to voice message in
  Telegram (AyuGram) hung on the widget for hours: the session never goes away,
  and `GetCurrentSession()` keeps returning exactly it. The tell is
  `PlaybackStatus = Stopped` (measured: `status Stopped`, zero duration,
  `LastUpdatedTime` not set at all, so time cannot filter it out). Such sessions
  and `Closed` no longer take part in the selection. `Opened` and `Changing` are
  deliberately considered valid: `Changing` flashes by on every track change, and
  the widget would blink empty. The minute-of-silence rule is unrelated — it lives
  in the extension and only applies to browser tabs.
  The selection is also recomputed on `PlaybackInfoChanged`. It must be subscribed
  **for all sessions, not just the one being shown**: a stopped one does not make
  it into the selection, there is no subscription to it, and pressing Play in it
  goes unnoticed — the session list does not change, so `SessionsChanged` will not
  fire. That is exactly how the widget missed AIMP starting and kept showing the
  browser tab. Plus a check right in the track and timeline reads, in case the
  event never arrives at all.
- **The media session libraries are pinned in memory.** The widget crashed with
  `AccessViolationException` in `WinRT.IObjectReference.Finalize()` — silently,
  with no trace in `errors.log` (an exception on the finalizer thread is caught by
  nothing and kills the process). The reason: a WinRT object's method table is a
  COPY of pointers into the implementing library's code, and COM is free to unload
  it as soon as no references to its objects remain. The .NET-side reference lives
  until garbage collection and is released pointing into the unloaded image.
  Measured: `Windows.Media.MediaControl.dll` really is unloaded while the session
  object is still alive. The fix is `GetModuleHandleEx` with the `PIN` flag for
  `Windows.Media.MediaControl.dll` and `OneCoreUAPCommonProxyStub.dll` (the latter
  holds the method tables of sessions from other processes). Pinning happens on
  every session selection rather than once: the marshaling stub is only loaded
  together with the first foreign session. This is a known CsWinRT defect (issue
  337), fixed in the projections that ship with .NET 9 and newer; the widget now
  targets .NET 11, so the pinning is belt-and-braces. It is kept on purpose: the
  failure is a silent `AccessViolation` in the finalizer, so removing it should
  follow a long soak rather than a build that merely compiles.
- **WinRT is called only from `WinRtThread`.** All SMTC access goes through one
  long-lived thread, and it is deliberately MTA — the same apartment as the
  finalizer, so releasing needs no cross-apartment marshaling. On its own this did
  not fix the crash above (verified), but binding WinRT objects to temporary pool
  threads is wrong, and there is no reason to bring it back.
- **Album art is fetched around the proxy when the connection fails.**
  `HttpClient` reads the system proxy settings ONCE, at creation, while the widget
  lives for weeks: turned on a VPN, started the widget, turned the VPN off — and
  no album art until a restart ("the remote computer refused the connection,
  127.0.0.1:2080"). The retry uses a second client with `UseProxy = false`, and
  only when the connection itself failed: if the proxy does not answer, there is
  no tunnel and nothing to bypass. A failure is also no longer remembered
  forever — previously one failure left the track without art until the end,
  because the address went into the cache with an empty value.
- **The WebSocket is hand-written** on top of `TcpListener`. `HttpListener` would
  require `netsh urlacl` or administrator rights, and the widget runs as an
  ordinary user.
- **The extension is installed unpacked**, so after edits it has to be reloaded
  with the button in `edge://extensions`. The version goes in every message
  (the `v` field) — the log shows which one is really running.
- **The Windows 11 taskbar is drawn in XAML**, so classic `WinEvent`s never arrive
  for its buttons. The Start and weather positions are tracked by polling UIA
  with cached element references.
- **That UIA poll was the largest steady CPU cost.** An A/B with the poll switched
  off halved the process: 10.4% → 4.4% of one core (the same pair 17.0% → 2.2%),
  so the widget now polls once a second when idle (60 ms bursts while the bar is
  moving, driven by the hook) and asks for the properties in one batched call:
  a `CacheRequest` for name/bounds together with `FindAll`, and cached `Cached.*`
  reads instead of one cross-process call per element. The taskbar root element
  is cached too - it used to be created on every pass, even on a centred bar
  where the code never used it.
- **Per-tick checks are cached.** The position timer runs five times a second per
  window, so the fullscreen scan is cached for a second (the foreground hook
  clears it), the natural text width is measured via DirectWrite only when the
  track or DPI changes, the window layout size is computed deterministically in
  DIPs without a layout pass, the topmost re-assert is throttled to once a
  second (the foreground hook covers focus changes), and the theme registry read
  is cached for two seconds. The track poll is a 10 s safety net - SMTC and
  bridge events drive the real updates - and the Spotify presence check runs
  every 10 s.
- **The window is a layered Win32 surface** (`WS_EX_LAYERED` +
  `UpdateLayeredWindow`), drawn with software Direct2D (`ID2D1DCRenderTarget`
  into a 32-bpp top-down DIB): zero D3D/DXGI GPU driver memory overhead, and a
  16 ms animation timer runs *only* while an animation is active. At rest the
  progress bar advances on a 200 ms step.
- **UI thread affinity requires `UiSynchronizationContext`.** Because there is no
  framework dispatcher, `UiThread.Initialize()` installs a custom
  `SynchronizationContext` backed by an `HWND_MESSAGE` window so every `await`
  continuation in `WidgetWindow` resumes on the UI thread instead of touching
  the single-threaded `ID2D1DCRenderTarget` or shared bitmaps from ThreadPool
  workers.
- **`shell:AppsFolder\<AUMID>` starts a NEW instance of a PWA.** Clicking the
  title opened a second Spotify: an installed PWA answers shell activation by
  launching, unlike a desktop app that just raises its window. Now the running
  window is looked up first - by the process name inside the AUMID (desktop
  builds, including ones hidden in the tray), then by the window title, where the
  AUMID carries the package name ("SpotifyAB.SpotifyMusic...!Spotify") and the
  title only the brand ("Spotify"). Browser windows get priority in that title
  search, since a PWA lives in the browser's process. When the bridge is
  connected, the tab is focused through the extension instead - exact window, no
  guessing; the "topmost browser window" fallback is skipped when a title was
  given, because a PWA is titled after the app rather than the tab.
- **The weather button holds both cards at once** - the shown one and the next
  ("32°C Sunny" with a right edge of 107 and "Temps to rise" at 154), at the same
  height and both with `IsOffscreen=False`. Geometry cannot tell them apart; the
  visible one is revealed by the button's own name ("Weather 32°C Sunny"), and the
  texts are filtered by it. While the maximum was taken, the widget parked by the
  invisible card and a fifty-pixel hole gaped on the left.
- **The browser throttles timers in a muted background tab**: on pause
  `setInterval` in content.js fires once a minute (measured: 3-4 s before the
  pause, 60 s after). That is why the service worker pulls the state by polling
  every 4 s — throttling does not touch message delivery — and the widget holds
  the pause while the socket is alive rather than by a timeout. The content
  script itself only starts its one-second timer when the page has media (a
  `<video>`/`<audio>` element or mediaSession metadata) and otherwise waits for
  a `play`/`loadedmetadata` event: it runs in every frame of every http(s) page,
  where an unconditional timer is pure overhead.
- **Measure consumption** only in steady state: the first ~40 seconds after
  startup go into JIT and warm-up, and the numbers are several times too high.
- **A volume slider is indistinguishable from the progress bar**: both are
  `input[type=range]` in the same DOM, the only difference is `max` (1 against the
  track duration). The `max <= 5` cut-off in `sliderProgress()` rests on this.
- **Tab volume is driven by the site slider, not the mixer**: in the system mixer
  a browser is one entry for all tabs, and its volume is not the track volume.
- **Commands are sent to the socket one at a time** (a semaphore in
  `BrowserBridge.Send`): dragging the volume slider sends them in bursts, and two
  writes into one stream interleaved mean a torn WebSocket frame.
- **The context menu uses native `TrackPopupMenuEx`.** Before opening it, the
  widget temporarily clears `WS_EX_NOACTIVATE` and calls `SetForegroundWindow`
  so clicking outside dismisses the menu natively (no watchdog timer needed),
  then restores `WS_EX_NOACTIVATE` on return. Dark/light styling is applied via
  `uxtheme.dll` ordinals `#135`/`#136`.
  **Dismissing by clicking off activates the taskbar, which rises above the
  widget in the topmost band.** `ReassertTopmost` has to stand aside while the
  menu is open (otherwise the widget covers its own menu), and the click that
  raised the bar happened before the close - so no foreground event comes
  afterwards to put the widget back. It stayed behind the bar until the
  once-a-second topmost tick: opening Brightness and clicking off made the
  widget "disappear" for up to a second. The menu-close path now reasserts
  immediately.
- **Hit-testing is kept alive at any brightness.** The layered surface fills its
  rounded bounds with `#01000000` *before* applying `_settings.Opacity` to the
  foreground content, so clicks and hover never fall through even at 20%
  brightness.

## Build

```
build.cmd
```

Or directly:

```
dotnet publish src/SpotDock/SpotDock.csproj -c Release -o artifacts/publish
```

Requires the .NET 11 SDK, Windows, and Visual Studio C++ Build Tools ("Desktop
development with C++") for NativeAOT native linking. For local testing without
C++ Build Tools, `build.cmd local` publishes a framework-dependent build to
`artifacts/local`.

## Installer and releases

`build/setup.iss` builds with Inno Setup 6 into a single exe (`SpotDock-Setup.exe`
under `artifacts/installer/`) containing the standalone NativeAOT `SpotDock.exe`
and the extension next to it in `spotdock-bridge` (no .NET runtime required). It
installs into the user profile (`PrivilegesRequired=lowest`) and does not ask for UAC.

Release: `git tag vX.Y.Z && git push origin vX.Y.Z`. Then
`.github/workflows/release.yml` builds on a windows runner and attaches the exe to
the release itself; a push to `main` builds the same thing into artifacts so a
breakage is visible immediately.

Installer pitfalls:

- **`{app}` is not expanded yet in `InitializeWizard`** — touching it crashes the
  installer silently. Set page texts in `CurPageChanged`.
- **No `taskkill` in `[UninstallRun]`**: removing one copy killed the user's
  running widget. Closing is handled by Restart Manager (`CloseApplications=yes`).
