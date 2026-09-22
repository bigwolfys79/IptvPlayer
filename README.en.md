# IptvPlayer

## License

This project is licensed under the **Prosperity Public License 3.0.0**.

It is free for noncommercial and personal use, but commercial use is limited to a 30-day trial period. For more details, please see the [LICENSE](LICENSE) file.

**A purchased license is activated offline, with no server:** the "License" menu item (before "About", also in the hub settings menu) shows the current status — personal use, a commercial license (who it is registered to and until when), or the remaining trial period. The activation button is right there: copy your Hardware ID (HWID), send it to the developer, and paste the received key into the dialog (or import a `.lic` file). Activation is available at any time — no need to wait for the trial to end. The key is RSA-2048 signed and bound to the HWID — it cannot be forged without the developer's private key.

IPTV player for M3U/M3U8 playlists with timeshift archive and full HEVC/AC-3 playback powered by FFmpeg. WinUI 3 / .NET 8 / Windows App SDK.

- **Version:** 1.21.7
- **Repository and releases:** https://github.com/bigwolfys79/IptvPlayer (update checking is built into "About")
- **Settings and cache:** `%LocalAppData%\IptvPlayer`
- **Log (Serilog):** `%LocalAppData%\IptvPlayer\logs` (daily rolling, toggleable in settings)
- **Debug build:** MSIX (F5 in Visual Studio, Platform=x64)
- **Release build + installer:**
  ```
  dotnet publish IptvPlayer.csproj -c Release -p:PublishProfile=win-x64
  "%LOCALAPPDATA%\Programs\Inno Setup 7\ISCC.exe" installer\IptvPlayer.iss
  ```
  Output file: `installer\output\IptvPlayer-Setup-<version>-x64.exe`

---

## Features

### Playback
- FFmpegInteropX + FFmpeg — HEVC, AC-3 and other codec decoding
- Hardware (GPU) and software (CPU) decoding modes
- Predictive buffering (15 s / 32 MB)
- Video display modes: fit / stretch / crop
- Render upscale (experimental): D3D11 Video Processor (RTX VSR / Intel VSR when supported by the driver), FSR 1.0, bicubic — with automatic fallback
- Audio normalization: Dynamic (boost quiet channels) / Loudness (EBU R128)
- Volume boost up to 200% (FFmpeg volume on top of normalization — for very quiet channels)
- Action confirmation toasts at the bottom center of the video: volume, mute, sleep timer
- Real stream speed measurement via the diagnostics proxy (playback settings)
- Statistics overlay (Ctrl+J): codecs, resolution, bitrates, decoder
- Hotkeys: digits (channel number), Enter (confirm), Backspace (previous channel), arrows/PgUp/PgDn (adjacent channel), Space (pause for archive and VOD), M (mute), V (display mode), F/F11 (fullscreen), Esc (back/exit), Ctrl+F (search), Ctrl+J (stream stats), Ctrl+M (mini-player), Ctrl+T (always on top); full help — F1
- Archive (timeshift) with seeking and program progress
- Archive/VOD pause freezes the last frame (no gray screen); a popup "Paused / Playing" indicator appears on pause state changes
- Stream diagnostics on playback error

### Local video files
- "Video" card on the Hub — open a video file from disk (FileOpenPicker: mp4, mkv, avi, ts, mpg, etc.)
- Same playback pipeline as VOD: pause, seeking, fullscreen, audio normalization
- Cyrillic and spaces in paths supported; launching with a video file as a command-line argument

### Video library (video portal)
- Hub Page with greeting, animations, and custom flyout menus
- Portal source with category catalog, movies and series
- VOD playback with pause and seeking
- Seasons and episodes with instant switching
- Poster grid with search and filtering (genre, year, content type)
- Genre/year filters respect the selected category: "Movies + action" no longer shows series; "All types" searches the whole portal
- Quality selection (480p/720p/1080p/Auto; portal episodes take renditions from the HLS master playlist, with an "Auto" entry)
- "Audio" button on the player: shown when the stream has labeled audio tracks
- Preferred audio language auto-selection (Settings → Playback)
- Fast movie start (instant from catalog link)
- VOD resume (position saving)
- Portal key cache invalidation (SHA-256)

### Interface
- Hub Page — launch screen with "Playlists", "Portal", "Settings" cards
- Borderless fullscreen mode
- System tray (minimize without stopping playback)
- Themes (Light/Dark/System): light theme is fully consistent — settings dialogs, player overlays (channel header, control bar, EPG panel) and the hub all follow the selected theme
- Interface language (Russian/English), localized via resw
- Live panel splitter (240–640 px)
- Window state memory (position and size restored)
- Mini-player (Ctrl+M)
- Always-on-top mode without resizing (Ctrl+T)
- Auto-resume last channel
- View toggle "List/Posters" (portal only)

### EPG and extras
- EPG (XMLTV) with lazy loading (current program at startup, full list on click); current program title in the channel list wraps up to two lines
- Multi-level EPG cache: downloaded feed, merged result and positions — startup takes a fraction of a second, network is touched once per refresh period (1/3/7 days); when the refresh period is due, the program guide is shown immediately from the stale cache while the re-download runs in the background
- EPG settings: reminders, refresh period and archive depth (1/3/7 days back); EPG sources are configured per playlist
- EPG source validation on add (head-of-feed download, gzip, XMLTV checks) and per-source status: last successful load or error text
- Learned channel aliases: unambiguous "XMLTV name → playlist channel" matches are memorized and reused on subsequent EPG refreshes, even when the channel cannot be matched by tvg-id or name tables
- Program reminders
- Favorite channels
- Move a channel to another group and remove it from the list (right-click on a channel); edits survive playlist refresh, restoration lives in playlist settings; actions on channels of blocked groups require the PIN
- Parental control (PIN, daily watch limit, per-channel unlock via Enter until channel switch)
- Program recording (up to 3 parallel)
- Scheduled recordings
- Settings export/import (with encryption)
- Adaptive layout when the window is resized smaller: player toolbars scale down automatically, the channel list collapses on narrow windows and comes back when widened; the hub cards scale as a whole

### Infrastructure
- Release: unpackaged build for classic installer
- Inno Setup installer with language selection and Dolby decoders
- Serilog + Dependency Injection
- MVVM refactoring (ViewModels, partial files)
- Unit tests (xunit)
- Memory optimization (nullable EPG descriptions, lazy loading)

---

## Known limitations

- Live broadcast delay ~10–15 s (trade-off for predictive buffering)
- MSIX version (Debug) and Inno version are "different apps" with shared settings
- File logging enabled by default, toggleable in settings
