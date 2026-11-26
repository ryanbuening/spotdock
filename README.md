# SpotDock

A Spotify-only Windows taskbar widget that uses ~30 MB of RAM.
SpotDock displays whatever is playing (or paused) in **Spotify Desktop** (classic installer & Microsoft Store via Windows SMTC) or **Spotify Web / browser PWA** (`open.spotify.com` via the optional `SpotDock Bridge` extension). When no Spotify track is loaded or Spotify is closed, the widget stays hidden.

![SpotDock demo](docs/demo.png)

## Install

1. Download `SpotDock-Setup.exe` and run it. No administrator rights are
   needed; it installs into the user profile as a standalone native executable (`SpotDock.exe`).
2. **Optional (for Spotify Web / PWA):** install the `SpotDock Bridge` browser extension:
   - open `edge://extensions` (`chrome://extensions` in Chrome);
   - turn on **Developer mode**;
   - click **Load unpacked** and choose the `spotdock-bridge` folder inside the
     installation folder (the installer offers to open it).

## Highlights

- **Spotify-only:** ignores other Windows media players, YouTube, SoundCloud, and non-Spotify browser tabs.
- **Desktop & Web/PWA:** reads Spotify Desktop through Windows SMTC (with UI Automation favorites support) and Spotify Web (`open.spotify.com`) through the `SpotDock Bridge` extension.
- **Stay-visible paused state, hidden when idle:** keeps paused tracks visible for both Desktop and Web, and hides the widget when no track is loaded or Spotify is closed.
- **Click to reveal:** clicking the track title raises the Spotify Desktop window or focuses the Spotify Web tab.
- **Quick controls:** play/pause, previous/next, middle-click anywhere to skip next, seekable progress bar, and favorites (+/✓).
- **Taskbar integration:** anchors cleanly to the Windows 11 taskbar (including weather button content), smooth width changes, brightness presets, and auto-hides for fullscreen apps.
- Settings and error log live in `%APPDATA%\SpotDock\`.

Technical details and pitfalls are in [AGENTS.md](AGENTS.md).

## Building

Requires the .NET 11 SDK and Visual Studio C++ Build Tools on Windows:

```
dotnet publish SpotDock.csproj -c Release -o publish
```

Produces a standalone NativeAOT `SpotDock.exe` with no .NET runtime dependency.

## How it works

> Independent project, **not affiliated with, sponsored or endorsed by Spotify AB**.
> "Spotify" is a trademark of Spotify AB.

- **Spotify Desktop:** Track data comes from the **Windows media session API (SMTC)**
  filtered strictly to Spotify desktop sessions (classic Win32 and Microsoft Store).
  Saved/liked state is read and toggled through Spotify's accessibility tree via
  native COM UI Automation.
- **Spotify Web / PWA:** The optional **SpotDock Bridge** extension reads
  `navigator.mediaSession` and Spotify Web player controls/slider state on
  `https://open.spotify.com/*` and sends updates over loopback WebSocket
  (`127.0.0.1:45219`).
- **Taskbar integration:** Renders as a borderless layered Win32 window (`Direct2D`
  / `DirectWrite`) docked over the Windows 11 taskbar, adapting to centered or
  left-aligned taskbars, auto-hide, DPI changes, and hiding automatically when an
  app goes fullscreen or when no Spotify track is loaded.

## Usage

- **Position:** Automatic by default. Press and hold on the artwork or
  title/artist area and drag to position manually.
- **Reveal Spotify:** Click the title/artist area to focus the Spotify Desktop
  window or Spotify Web tab.
- **Controls:** Play/pause, previous/next, middle-click anywhere to skip to the
  next track, click/drag the progress bar to seek, scroll wheel to adjust system
  volume, and `+` / green check to save to favorites.
- **Settings menu:** Right-click the widget for Brightness presets (20%–100%),
  Show cover art, Progress bar, Start with Windows, and Quit.
- Settings and error log live in `%APPDATA%\SpotDock\`.

## Structure

| File | Role |
|---|---|
| `WidgetWindow.cs` | Win32 widget window, positioning, responsive layout, menu |
| `D2DRenderer.cs` | Direct2D/DirectWrite/WIC rendering and layered window presentation |
| `ScrubPillWindow.cs` | Floating Direct2D scrub timestamp pill |
| `UiThread.cs` | Win32 message loop and `SynchronizationContext` |
| `MediaHub.cs` | Shared source combining Spotify Desktop (SMTC) and Spotify Web (`BrowserBridge`) |
| `MediaService.cs` | Windows SMTC reader filtered to Spotify Desktop sessions |
| `BrowserBridge.cs` | Loopback WebSocket server (`127.0.0.1:45219`) for `open.spotify.com` |
| `spotdock-bridge/` | MV3 extension (`SpotDock Bridge`) scoped to `https://open.spotify.com/*` |
| `SpotifyUiaService.cs` | Spotify Desktop accessibility (native COM `IUIAutomation`) |
| `SourceActivator.cs` | Focuses Spotify Desktop window or Spotify Web browser window |
| `Interop.cs` | Win32 (taskbar position, topmost, fullscreen detection, input) |
| `WidgetSettings.cs` | Position, opacity, and UI options stored as JSON |
