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

  or & "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe" installer\IptvPlayer.iss
  ```
  Output file: `installer\output\IptvPlayer-Setup-<version>-x64.exe`

---

## Implemented

### v1.12.2
- **Localization moved to separate files** — all UI strings (421 entries, Russian + English) now live in `Strings/ru-RU/Resources.resw` and `Strings/en-US/Resources.resw`: texts can be edited in one place instead of hunting through the code. In code — `L.T("Key")`, in XAML — `x:Uid`; loaded via standard MRT Core (Windows App SDK). English translations added for all strings. Language changes apply after an app restart.
- **Fixed EPG cache cleanup** — due to a key mismatch, every EPG load deleted all on-disk source caches and re-downloaded them from the network. The cache now correctly survives restarts within the configured refresh interval.
- **Dead cache code removed** — CacheService/ICacheService (the old EPG JSON cache) and PlaylistCacheService (playlist JSON cache, long replaced by SQLite); orphaned `*.json` files in the cache folder are cleaned up on first launch.
- **Fixed unit test runs** — the test host crashed on Windows App SDK auto-initialization; it is disabled for Debug builds, 64/64 tests green.

### v1.12.1 (follow-up)
- **Fixed settings export/import buttons** — both failed silently (ArgumentException: WinRT pickers reject compound extensions like ".iptvplayer.json"); the export file format is now ".iptvplayer", and any failure before the file dialog opens is shown as a message instead of being swallowed.
- **Movie resume positions moved to SQLite** (`iptvplayer_cache.db`, vod_resume table) — settings.json is no longer rewritten every 5 seconds during playback; existing positions migrate automatically on first start. The "Resume playback" behavior is unchanged.
- **Portal debug dump removed** — the `portal_dump` folder (contained direct movie links with access tokens, hundreds of megabytes) is no longer created, and accumulated dumps are deleted at startup; portal responses in logs are now masked just like requests.
- **Playlist URLs hidden in the list** — the URL line under the playlist name in "Settings → Playlists" is removed entirely (subscription credentials are usually embedded in the URL).
- **Cancel/Save/Done buttons always visible** — in all settings dialogs (Playlists, EPG, Playback, Interface, Recordings) the buttons moved out of the scrollable area.
- **Resilient settings loading** — an empty or non-standard date value (e.g. `LastUpdateCheckUtc: ""` after manually editing settings.json) no longer resets all settings to defaults: the field is simply ignored.

### v1.12.1
- **No more plaintext secrets** — the portal key and playlist/EPG source URLs (which usually embed a username, password or token) are encrypted with Windows DPAPI and written to settings.json as opaque `dpapi:...` strings; existing settings migrate automatically on first save.
- **Secret removed from auxiliary storage** — the "key" field is no longer written to the SQLite channel cache (it is re-injected from settings when a portal request is sent), and keys/passwords are masked in file logs and portal_dump.
- **"Download and install" button in "About"** — when a manual update check finds a new version, the check button becomes "Download and install": the installer is downloaded to the temp folder (SHA256 verified, with progress) and then the same flow as the automatic check runs (consent dialog, postponement during active recordings, silent install). The duplicate GitHub request code in the dialog has been removed.
- **Removed the channel list sort combo box** ("catalog order / by name / by year") — order is always as in the source, favorites stay on top.
- **Playlist URLs masked in settings** — in the playlist list, username/password/token values in the URL are shown as "***" (EPG URLs left as is).
- **Password-protected settings export/import** — "Export..."/"Import..." buttons added to "Settings → Playlists": transfer settings between machines (DPAPI encryption of settings.json makes copying the file useless). The export file is password-protected (AES-GCM + PBKDF2) and contains everything except machine-specific data (resume positions, recordings, timer state); on import you choose "replace all" or "add playlists only" (duplicates by URL are skipped).

### v1.12.0
- **Resume playback for portal movies** — when reopening a movie/episode you stopped at more than 30 seconds in (and not near the end), a dialog appears: "Resume / Start over" with the stop time (e.g. "you stopped at 25:53"). The position is remembered per movie; for serials — per episode; stored in settings (last 200 entries), disk writes are throttled (at most once per 5 seconds), fully watched items are evicted automatically. Resuming seeks the media engine directly, no stream restart.
- **Readable seek bar tooltip** — dragging the VOD seek bar now shows the time as h:mm:ss in the thumb tooltip (previously raw seconds: "3360" instead of "0:56:00").

### v1.11.5
- **EPG as separate overlay** — EPG panel moved to the full window level (previously video area only): scrim covers the entire screen (including channel list), clicking outside the panel closes EPG. In both windowed and fullscreen modes, the fullscreen overlay no longer intercepts mouse when EPG is open. Mouse wheel scrolls the EPG program list only when the panel is visible.

### Playback
- **FFmpegInteropX + FFmpeg** — demuxing and decoding any streams (including HEVC in MPEG-TS and AC-3, which the built-in Windows player cannot handle); rendering is done via the standard MediaPlayerElement.
- Video decoding modes, switchable in settings: hardware (GPU with CPU fallback) and software (CPU, default — smooth even at 1080p60).
- Predictive buffering (15 s / 32 MB) — eliminates stuttering at 10-second HLS segment boundaries.
- Volume: slider in the control panel, mouse wheel over video, persisted between launches; multichannel audio (5.1) is downmixed by the Windows audio engine (not FFmpeg's quiet downmix).
- **Video display modes**: fit (letterbox, default) / stretch / crop (scale preserving aspect ratio, clipping edges) — for content that doesn't match the window aspect ratio (4:3 on 16:9); button in both control panels and the V key, current mode is shown in the button tooltip, selection is saved in settings.
- **Statistics overlay (Ctrl+J, like in VLC)**: video/audio codecs, resolution, fps, bitrates, actual video decoder (FFmpeg D3D11 GPU / FFmpeg CPU / system — as selected by FFmpegInteropX, including hardware mode fallback) and hardware decoder status on the machine, buffer depth, stall count, and channel session time (live line — ticks every second) — for diagnosing playback issues a screenshot of the overlay is sufficient; works in windowed and fullscreen modes (in fullscreen — below the channel name header, to the right of the channel list). Also toggled by the "Stream Statistics" switch in settings (Diagnostics), state persists across restarts.
- **Hotkeys and mute mode**: space — pause/resume archive; up/down and PgUp/PgDn — next/previous channel in a loop within the current filter (in fullscreen mode the overlay is shown so you can see which channel switched to); M — mute with mute buttons in both control panels (sliders show zero, volume is restored when unmuted); F/F11 and double-click on video — fullscreen; Esc — exit fullscreen; Ctrl+F — focus search with text selection; V — video display mode; Ctrl+J — stream statistics; number keys — direct channel number input with a TV-style overlay (Enter or 3 s commits, Backspace — delete, Esc — cancel). Letter keys work in any keyboard layout (VK codes); hotkeys are disabled in input fields and open dialogs; arrow keys/PageUp/PageDown are not intercepted when focus is on the channel list, seek slider, or combobox.
- Archive (timeshift): clicking a program starts it from the beginning; "Live" and pause buttons appear in the control panel during archive playback.
- **Archive seeking**: time-labeled bar in the control panels (windowed and fullscreen) during archive playback — the entire bar represents the full program (zero is its start), releasing the slider restarts the stream from the new position (HLS timeshift is not seeked on the fly); position and pauses are calculated by the clock, after seeking the indicator stays at the selected minute, you cannot go past the live broadcast.
- **Current program progress**: thin bar in the channel row (main list and fullscreen overlay — shared template) and in the fullscreen overlay header — shows how much of the program has elapsed; updates every 30 s.
- Stability: FFmpeg source is tied to the player lifetime (ConditionalWeakTable) — fixed the 0xC00D36B6 crash with audio/video loss; player errors are logged with codes (MediaPlayer.MediaFailed); for HLS keepalive is disabled (`http_persistent=0` — some providers alternate segment servers and connection reuse broke on every segment, causing freezes) and auto-reconnect is enabled (`reconnect`).

### Video library (video portal)
- **Portal source in the playlist list**: "Video Portal" type in the Playlist dialog; accepts a combined portal string (`portal::[key:...]URL`) — key and base API address are extracted automatically, or entered separately. The portal catalog (categories -> movies/series with pagination) becomes a "playlist": categories = groups in the filter, items = channels; cached like a regular playlist.
- **VOD playback**: clicking a movie lazily requests a stream from the API (links are short-lived, not cached); VOD mode — pause with space without restarting the stream, seek bar with position/duration (seeking on the fly, without restart); for series, the first season/episode stream is used (episode selection is planned).
- **Seasons and episodes**: clicking a series opens a dialog with a poster, description, season combobox (seasons are separate catalog cards, grouped by series name) and an episode list. During playback, the bottom panels (windowed and fullscreen) show "Season" and "Episode" comboboxes: switching episodes is instant, without re-requesting the portal. The EPG button is hidden when viewing a portal.
- **Poster grid**: view toggle for the list (rows vs. poster tiles with thumbnails) next to the group filter; posters from the portal catalog, virtualization and thumbnail decoding — catalog with 20k+ items scrolls smoothly; search/filter/sort work in both views, selection is remembered.
- **Quality selection**: menu on the button in the bottom panels (480p/720p/1080p/Auto) — options are provided by the portal; switching restarts the stream from the same position; initial quality is from the "Preferred quality" setting.
- **Descriptions**: portal movie annotation — in the headers above video (windowed and fullscreen, fully under the progress bar) and in the tooltip when hovering over an item in the list.
- **Fast movie start**: movie starts immediately from the catalog link (without waiting for a portal request), quality options are loaded in the background and injected into the playing player; separate configurable video library buffer ("Playback" -> "Video library buffer", 2–15 s, default 4) — a 15 s live buffer on a slow CDN caused multi-second startup delays.
- **Performance on large catalogs**: list rebuilding (search/filter/sort) — single operation instead of tens of thousands of events; fullscreen grouping is only built when entering fullscreen; hidden view (list vs. posters) is detached from data.
- **Catalog sorting**: by catalog (portal order) / by name / by year — combobox next to the group filter; catalog search with 300 ms debounce (22,000+ items don't freeze the UI).
- Client protocol is "transparent": API request objects are passed as-is, all responses are parsed softly (unknown fields are ignored), every request/response is logged — new portal commands don't require client changes.

### Interface
- Video occupies the entire right area.
- Video control panel (volume, archive pause, "Live", settings, fullscreen) appears on mouse movement and hides automatically after 3 s; works in windowed and fullscreen modes; fixed appearance only after mouse stops.
- **System tray**: tray icon is shown only while the window is hidden — when closing to tray (close button, setting) or minimizing to tray ("Minimize" button, separate setting); when the window is visible — only the taskbar icon remains. Left click on the tray icon restores the window, right click — "Show/Exit" menu.
- **Borderless fullscreen**: video occupies the entire screen edge-to-edge (decorative frame padding of the video container, ~3 mm, removed in fullscreen and restored in windowed mode).
- **Fullscreen mode**: current channel is highlighted in the overlay list and scrolled to the top visible row; mouse cursor automatically hides along with the overlay after 3 s of inactivity (including over video) — mouse movement, single and double click, and volume wheel work even when hidden.
- Removed: permanent panel above the player, archive banner over the video.
- Settings — separate dialogs from the gear menu: "Playback" (decoder, buffer, quality, volume normalization), "Interface" (language, theme, sleep timer, tray, file log), "Playlist" (multiple playlists), "Recordings", "Parental Control", "About" (description, paths, update checking from GitHub Releases).
- Channel list is displayed immediately on startup.
- **EPG (XMLTV)**: sources per playlist (own list in the Playlist dialog, fallback — global default source), channel matching by tvg-id and normalized name; current program description — in headers above video (below the full name), all program descriptions — in the EPG list. In v1.9.0 a long-standing XMLTV parser bug was fixed: reading on a subtree reader consumed everything after the first element — desc/category/icons were always lost (now most feed programs have annotations); disk EPG cache version was bumped — on first launch the feed is re-downloaded once.
- Archive indicator in the channel list: green dot for channels with archive (playlist attribute `tvg-rec`/`catchup-days`), gray — without archive; tooltip shows archive depth in days.
- **Program reminders**: bell icon in the future program card -> Windows toast N minutes before start (configurable in settings, default 5; `CommunityToolkit.WinUI.Notifications`). Reminders persist across restarts, expired ones are cleaned up automatically.
- **Favorite channels**: star in the list (filled/outlined), favorites are always at the top of the list and in the "★ Favorites" group of the fullscreen overlay, there's a favorites filter; stored by channel name in settings.
- **Semi-automatic updates** (toggle in "Interface", enabled by default): a couple of minutes after startup — no more than once a day — the app checks GitHub Releases, downloads the installer with SHA256 verification and offers to install; installation is silent (the app closes and returns by itself), won't launch while recordings are in progress (waits for completion then installs), on any error the current version continues working. UAC confirmation still appears (installation to Program Files).
- **Auto-resume**: on startup the last watched channel is selected and tuned; its group is also restored in the list filter (the channel is visible in its subsection), the list is scrolled to it.
- **Dark theme**: Light/Dark/System via FrameworkElement.RequestedTheme — applied on the fly (on startup and immediately when changed in settings).
- **Interface language** (Russian/English): localizer in Services/Localizer.cs, string pairs at usage sites; switching takes effect immediately.
- **Video buffer slider** in settings (5–60 s): controls FFmpeg ReadAheadBuffer — smoothness vs. live delay.
- **Live panel splitter**: channel list width is adjustable by mouse (CommunityToolkit Sizers, 240–640 px) and remembered.
- **Window state**: window position, size and maximized state are restored on startup (native AppWindow.MoveAndResize, with fitting to desktop when a monitor is disconnected).
- **MVVM refactoring (stages 1–3)**: ViewModels (`MainPageViewModel`, `EpgViewModel`, `PlayerViewModel`, `ChannelViewModel`) on `ObservableObject` with properties via `SetProperty` — `[ObservableProperty]` is deliberately not used (the generator doesn't create WinRT projections — MVVMTK0045, important for AOT/ABI). User actions use `[RelayCommand]`. MainPage code-behind is split by zones into partial files (`MainPage.FullScreen.cs`, `MainPage.Overlays.cs`, `MainPage.Hotkeys.cs`, `MainPage.StatsOverlay.cs`). MainPage core — ~1900 lines.
- **Recording programs and channels**: up to 3 parallel recordings (ffmpeg `-c copy`, graceful stop via `q`), REC button in control panels; folder is configurable (Recordings menu), active/scheduled recordings are shown there; recordings interrupted by app close are offered to resume on next launch.
- **Serilog + DI**: replaces custom file logger — Serilog (Debug output + file in `%LocalAppData%\IptvPlayer\logs` with daily rolling, 14 days retention; toggle in settings works on the fly), classes receive `ILogger<T>` via constructor; dependencies assembled by `Microsoft.Extensions.DependencyInjection` (composition root in `App`, `App.Services`) — all `?? new …` fallbacks and scattered `new SettingsService()` removed, services and ViewModels are singletons.
- Recordings are stored in MPEG-TS without transcoding; default folder — "Videos\IptvPlayer", configurable.
- Application icon (window, installer, all MSIX logos) — `Assets/AppIcon.ico`; icon generator: `analysis/gen-icons.ps1`.
- **System tray**: close button minimizes to tray — playback continues (native Shell_NotifyIcon; left click — show, right click — "Show/Exit" menu); full exit is instant (stop player and recordings + immediate process exit). Configurable via toggle in "Interface".
- **Mini-player (Ctrl+M)**: compact always-on-top window 480×300 above all windows, panels hidden.
- **"Previous channel"**: "←" button in both panels and Backspace — return via session watch history.
- **Parental Control**: launching channels from selected groups requires a PIN (PBKDF2 hash); on entry the app offers to disable the request for 15/30/45/60 min or until shutdown.

### Infrastructure
- Release is built unpackaged (`WindowsPackageType=None` + `WindowsAppSDKSelfContained`) for classic installer; Debug remains MSIX for F5.
- Inno Setup installer (`installer/IptvPlayer.iss`): wizard with install language selection (Russian/English — selection dialog on launch), icons, lzma2, built-in Dolby AC-3/AC-4 decoders (silently installed if missing, with verification).
- `installer/Install-DolbyDecoders.ps1` — standalone Dolby decoder installation (Microsoft Store -> on region denial: sideload from mirror).
- Scripts in `analysis/`: icon generator, TS codec probe, volume/speed measurements.
- Settings and cache in `%LocalAppData%\IptvPlayer` (works in both MSIX and unpackaged); file log with rotation.

---

## To do (tasks)

Rough order by benefit/effort; `[x]` — done.

### Playback and UX
- [x] **Top overlay in windowed mode** — header above video with channel name, current program, progress and archive indicator (like the fullscreen overlay top panel, but for the window); lives strictly within the video area (RightPanelGrid, behind the channel list and splitter) and is not obscured by them; currently this information is only in the channel list on the left in windowed mode.
- [x] **Hotkeys** — space (archive pause), up/down and PgUp/PgDn (next/previous channel in a loop), M (mute + mute buttons in panels), F/F11 and double-click on video (fullscreen), Esc (exit fullscreen), Ctrl+F (focus search), number keys — direct channel number input with TV-style overlay. *(1.4.13)*
- [x] **Video display modes** — letterbox/stretch/crop for content that doesn't match the window aspect ratio. *(1.4.14)*
- [x] **Statistics overlay (Ctrl+J)** — codec, resolution, bitrate, actual decoder (GPU/CPU after fallback), buffer stalls. *(1.4.14)*
- [x] **Sleep timer** — stop playback after N minutes.
- [x] **Mini-player on top of windows** — Ctrl+M: compact always-on-top window 480x300 without panels.
- [x] **System tray** — close button minimizes to tray, audio continues playing; native Shell_NotifyIcon (H.NotifyIcon is incompatible with the current WinAppSDK version). Toggleable in interface settings.

### Recordings
- [x] **Recording list in UI** — gear menu -> "Recordings": active (stop) and scheduled (remove), configurable folder with auto-creation.
- [x] **Recordings surviving restart** — active recordings are saved on close; on next launch, if the program is still airing, the app offers to record the remaining portion (new file "... (continuation)", fresh stream URL).
- [x] **Graceful ffmpeg stop** — `q` on stdin, Kill as fallback after 3 s.

### Infrastructure and quality
- [x] **Git repository** — initialized; `bin/`, `obj/`, `.vs/`, `AppPackages*`, heavy binaries `analysis/`/`installer/` in `.gitignore`.
- [x] **Unit tests** — `tests/IptvPlayer.Tests` (xunit): `ArchiveUrlBuilder`, `ChannelHistory`, `ParentalControlService` — 64 tests total. Run: `dotnet test tests/IptvPlayer.Tests -p:Platform=x64 -p:WindowsAppSdkAutoInitialize=false` (flag disables WinAppSDK auto-initialization in the test host).
- [x] **Stage 3 MVVM (partial)** — fullscreen mode/overlays/hotkeys/statistics overlay moved from MainPage into partial files (`MainPage.FullScreen.cs`, `MainPage.Overlays.cs`, `MainPage.Hotkeys.cs`, `MainPage.StatsOverlay.cs`); unused SettingsDialog removed. MainPage core — ~1900 lines.
- [x] **Multiple playlists** — switchable via settings menu, favorites are global by channel name.
- [x] **Auto-update** — "About" -> "Check for updates": default is GitHub Releases of the project repository (tag + installer), custom URL can be set in settings.json (UpdateCheckUrl, JSON version/url); "Download update" button opens the installer.
- [x] **"Open log folder"** — button in "About"; log folder is also shown in interface settings.
- [x] **Parental Control** — channels remain in the list, but launching channels from selected groups (auto-detected "adult" + manual) requires a PIN (PBKDF2); on PIN entry the app offers to disable the request for 15/30/45/60 min or until shutdown; "Parental Control" item in the settings menu.
- [x] **"Previous channel" + recently watched** — session history: "←" button in both panels and Backspace.
- [x] **Configurable recordings folder** — settings menu -> "Recordings" (field + browse + auto-creation; drive root via manual input, system picker doesn't expose it).
- [x] **Multiple parallel recordings** — up to 3 simultaneous (provider session limit); scheduling starts without waiting for a slot.

---

## Known limitations

- Live broadcast delay ~10–15 s (trade-off for predictive buffering against segment boundary stuttering; to reduce set `ReadAheadBufferDuration` in `StreamService`).
- MSIX version (Debug) and Inno version are "different apps" with shared settings; remove one to avoid shortcut confusion.
- File logging is enabled by default and writes to `%LocalAppData%\IptvPlayer\logs` (daily rolling); disabled via "File log" toggle in settings — takes effect immediately, Debug output (Output window) always remains.
