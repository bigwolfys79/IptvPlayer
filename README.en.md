# IptvPlayer

## License

This project is licensed under the **Prosperity Public License 3.0.0**.

It is free for noncommercial and personal use, but commercial use is limited to a 30-day trial period. For more details, please see the [LICENSE](LICENSE) file.

IPTV player for M3U/M3U8 playlists with timeshift archive and full HEVC/AC-3 playback powered by FFmpeg. WinUI 3 / .NET 8 / Windows App SDK.

- **Version:** 1.12.2
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
- Statistics overlay (Ctrl+J): codecs, resolution, bitrates, decoder
- Hotkeys: space, arrows, M (mute), F (fullscreen), number keys (channel)
- Archive (timeshift) with seeking and program progress

### Video library (video portal)
- Portal source with category catalog, movies and series
- VOD playback with pause and seeking
- Seasons and episodes with instant switching
- Poster grid with search and filtering
- Quality selection (480p/720p/1080p/Auto)
- Fast movie start

### Interface
- Borderless fullscreen mode
- System tray (minimize without stopping playback)
- Dark theme (Light/Dark/System)
- Interface language (Russian/English)
- Live panel splitter (240–640 px)
- Window state memory (position and size restored)
- Mini-player (Ctrl+M)
- Auto-resume last channel

### EPG and extras
- EPG (XMLTV) with channel matching
- Program reminders
- Favorite channels
- Parental control (PIN)
- Program recording (up to 3 parallel)
- Settings export/import (with encryption)

### Infrastructure
- Release: unpackaged build for classic installer
- Inno Setup installer with language selection and Dolby decoders
- Serilog + Dependency Injection
- MVVM refactoring (ViewModels, partial files)
- Unit tests (xunit, 64 tests)

---

## Known limitations

- Live broadcast delay ~10–15 s (trade-off for predictive buffering)
- MSIX version (Debug) and Inno version are "different apps" with shared settings
- File logging enabled by default, toggleable in settings
