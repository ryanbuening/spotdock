# Troubleshooting

## The widget stays hidden while Spotify is playing

SpotDock shows up whenever **Spotify Desktop** (Windows SMTC) or **Spotify Web / PWA**
(`open.spotify.com` via `SpotDock Bridge`) has an active track loaded. If nothing appears:

1. **Update Spotify Desktop.** Old versions of the desktop app didn't publish to
   the Windows media session at all. Install the latest from spotify.com or the
   Microsoft Store.
2. **Enable Spotify's media-key overlay.** In Spotify: **Settings → Display →
   turn on "Show desktop overlay when using media keys."** On some versions
   Spotify only publishes the current track to the Windows media session when
   this is on.
3. **Check the music is playing on this PC** — if you're casting to a phone or
   speaker via Spotify Connect, there's no local session to read.
4. **Using Spotify Web or the browser PWA?** Load the unpacked `spotdock-bridge`
   folder (`SpotDock Bridge`) in `edge://extensions` or `chrome://extensions`
   with **Developer mode** enabled, and reload it after updates.
5. **Check Windows itself:** play a song in Spotify Desktop and press a volume
   key. If the Windows volume popup does *not* show the track name either, your
   Windows is missing media integration — common on Windows 11 **"N" editions**.
   Fix: Settings → Apps → Optional features → *Add a feature* → install
   **Media Feature Pack**, then reboot.

## Antivirus / Smart App Control flags the installer or executable

Unsigned single-file .NET executables and installers can trigger heuristic
warnings in some antivirus engines or Smart App Control. The full source code is
public in this repository if you prefer to inspect and build it yourself with
`dotnet publish SpotDock.csproj -c Release -o publish`.

## The widget overlaps something / sits in the wrong place

- Drag and hold (~250 ms) on the artwork or title/artist area to reposition manually.
- Found a layout the auto-positioning handles badly? Please
  [open an issue](https://github.com/ryanbuening/spotdock/issues)
  with a screenshot of your full taskbar.

## The favorites (+) checkmark doesn't always turn green (Spotify Desktop)

There is **no public way** to reliably know whether the current track is saved
from the Spotify Desktop app alone:

- **Spotify's Web API** requires OAuth and app approval.
- So SpotDock reads the saved state from the Spotify Desktop app's accessibility
  tree (`IUIAutomation`). **Spotify freezes that information while its window is
  minimized**, so SpotDock can't read it reliably in the background.

Because of that, SpotDock only shows the green checkmark when it can confirm the
track is saved; otherwise it stays neutral (`+`) instead of guessing. When using
**Spotify Web / PWA** through `SpotDock Bridge`, the favorite state is read
directly from the web player bar.
