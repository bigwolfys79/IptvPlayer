# IptvPlayer

## License

This project is licensed under the **Prosperity Public License 3.0.0**.

It is free for noncommercial and personal use, but commercial use is limited to a 30-day trial period. For more details, please see the [LICENSE](LICENSE) file.

**A purchased license is activated offline, with no server:** in the expired-trial dialog, copy your Hardware ID (HWID) and send it to the developer; paste the received key into the same window (or import a `.lic` file). The key is RSA-2048 signed and bound to the HWID — it cannot be forged without the developer's private key.

IPTV player for M3U/M3U8 playlists with timeshift archive and full HEVC/AC-3 playback powered by FFmpeg. WinUI 3 / .NET 8 / Windows App SDK.

- **Version:** 1.16.1
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
- Audio normalization: Dynamic (boost quiet channels) / Loudness (EBU R128)
- Real stream speed measurement via the diagnostics proxy (playback settings)
- Statistics overlay (Ctrl+J): codecs, resolution, bitrates, decoder
- Hotkeys: digits (channel number), Enter (confirm), Backspace (previous channel), arrows/PgUp/PgDn (adjacent channel), Space (pause for archive and VOD), M (mute), V (display mode), F/F11 (fullscreen), Esc (back/exit), Ctrl+F (search), Ctrl+J (stream stats), Ctrl+M (mini-player); full help — F1
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
- Quality selection (480p/720p/1080p/Auto)
- Fast movie start (instant from catalog link)
- VOD resume (position saving)
- Portal key cache invalidation (SHA-256)

### Interface
- Hub Page — launch screen with "Playlists", "Portal", "Settings" cards
- Borderless fullscreen mode
- System tray (minimize without stopping playback)
- Dark theme (Light/Dark/System)
- Interface language (Russian/English), localized via resw
- Live panel splitter (240–640 px)
- Window state memory (position and size restored)
- Mini-player (Ctrl+M)
- Auto-resume last channel
- View toggle "List/Posters" (portal only)

### EPG and extras
- EPG (XMLTV) with lazy loading (current program at startup, full list on click)
- Multi-level EPG cache: downloaded feed, merged result and positions — startup takes a fraction of a second, network is touched once per refresh period (1/3/7 days)
- Program reminders
- Favorite channels
- Parental control (PIN)
- Program recording (up to 3 parallel)
- Scheduled recordings
- Settings export/import (with encryption)

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
