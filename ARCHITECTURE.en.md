# How IptvPlayer Works

Technical description of how the application is structured internally: how playback, rendering, EPG, archive, and video library work. Class/method names are real, taken directly from the code. The user-facing feature list is in README.md; here we only cover mechanisms and non-obvious design decisions.

## General Scheme

```
PlaylistSource (m3u | portal)
        │  M3UParserService / VideoPortalService
        ▼
ChannelRepository ──► MainPage / ViewModels ──► StreamService ──► FFmpegInteropX/FFmpeg ──► MediaPlayerElement
        │                    │                                                        ▲
        │                    ├── EpgViewModel ◄── EPGService ◄── XmlTvService            │
        │                    │        (EPG, reminders)             (XMLTV + cache)        │
        └── PlaylistDatabaseService (SQLite channel/catalog cache)                                 │
                                                                                          │
MediaPlayer.StartPlaybackAsync(channel, url, ...): live / archive (timeshift) / portal VOD ┘
```

Key classes:
- `HubPage` — launch screen with "Playlists", "Portal", "Settings" cards.
- `MainPage` (+ partial files `MainPage.FullScreen/Hotkeys/LocalVideo/Navigation/Overlays/Portal/Seek/Settings/StatsOverlay/VideoControls.cs`) — all UI and overlays.
- `MainPageViewModel` (+ partial files `MainPageViewModel.PortalFilters/Recording/VodResume.cs`) — channel list logic, filtering, recording, VOD resume.
- `EpgViewModel` — EPG: loading, lazy per-channel loading, current program.
- `PlayerViewModel` — player management (FFmpegInteropX), archive, VOD.
- `ChannelViewModel` — channel model with nullable CurrentProgramTitle/Description properties.
- `Services/StreamService` — single point of player creation (FFmpegInteropX) + diagnostics.
- `Services/VideoPortalService` — video portal client (catalog + streams).
- `Services/EPGService` + `XmlTvService` — loading and matching the TV guide.

---

## 1. Application Startup

First, `App.OnLaunched` registers a single instance (`AppInstance.FindOrRegisterForKey`): a second launch does not create another process — activation is redirected to the running one, which restores its window from the tray (`ShowFromTray`). This removes concurrent `settings.json` writes by parallel instances.

Next, the license is checked (`LicenseService.CheckLicense`): personal use is unrestricted; commercial use is a 30-day trial (DPAPI token in HKLM) or a purchased offline license. The license is an `IPL1.{payload}.{RSA-2048 signature}` string; the signature is verified against an embedded public key, and the payload is bound to the HWID (volume serial + MachineGuid). It is stored in HKCU and re-verified on every launch. Clock rollback protection: `LastSeenUtc` (DPAPI + HKCU) — rolling the system clock back does not extend the trial/license. On trial expiry a dialog shows the HWID, a key field, and `.lic` import — successful activation continues the launch.

`App` → `MainWindow` → `HubPage` (if `ShowHubOnStartup`) or `MainPage`:

**Hub Page**: launch screen with dark gradient (`#0D1117→#161B22`), time-based greeting with DropShadow glow, animated accent line, 3 cards (Playlists/Portal/Settings) with spin-in animation. Custom flyout menus with screen-edge positioning.

**MainPage** → `InitializeAsync()` (the page is shown immediately, nothing is blocked):

1. Settings are loaded (`SettingsService`, `%LocalAppData%\IptvPlayer\settings.json`); volume is restored from them.
2. Channels of the active playlist (`PlaylistSource.Type`: `m3u` — parser, `portal` — portal catalog, both use the `PlaylistDatabaseService` cache (SQLite)) are placed into `ChannelRepository`, assigned sequential `Id` values, and populate `ViewModel.Channels`.
3. `SelectedChannel` is assigned immediately, `Task.Yield()` lets the UI render the list; then EPG is loaded in the background and auto-resume of the last channel is triggered.
4. After EPG loads — `LoadEPGForChannelAsync` for the selected channel (full program list in the EPG panel).

## 2. Rendering and Input

Layers of the right area are set by `Canvas.ZIndex`: video (1) → header/control panel (2) → EPG overlay (3).

**Channel list** (`ChannelItemTemplate`, shared between windowed and fullscreen overlay): logo (28x28, `StringToImageSourceConverter` with system cache) → archive indicator dot (`HasArchive`) → name; tooltip with description (for portal items). The template root is a `Grid` (not a horizontal `StackPanel`): a StackPanel measures children with infinite width and breaks text wrapping; the text block occupies a star-sized column, so the current program title wraps up to two lines (`TextWrapping="Wrap"` + `MaxLines="2"`) and is ellipsized beyond that.

**Fullscreen mode** (`MainPage.FullScreen.cs` → `MainWindow.SetOsFullScreen`): OS presenter `AppWindowPresenterKind.FullScreen` stretches the window to fill the entire monitor (verified by measurements — the client area is exactly screen-sized); content wrapper is done manually: hiding the TitleBar row, collapsing channel list columns, zeroing out the decorative `Padding` of the video container (`VideoAreaBorder`, 12 px in windowed mode — without zeroing, bands appeared at the edges of the screen), rebuilding the video layout (`ForceVideoRelayout` — after changing the presenter, the DComp island draws using the old coordinates).

**Themes (Light/Dark)**: all UI colors are ThemeResource brushes from the theme dictionaries in `App.xaml` (REC/live/archive accents, overlay scrims and texts `OverlayScrim*/OverlayFg*`) and from `ThemeDictionaries` in `HubPage.xaml`. The ContentDialog popup layer does not inherit the window root's `RequestedTheme` — therefore all dialogs are created only via `Controls/ThemedContentDialog`, which copies the root's `ActualTheme`. Icons built in code (`Controls/AppIcons`) are colored per the effective theme; `ApplyTheme` rebuilds them on theme change.

**Overlays**: top header (channel name, current program + description, progress, archive/sleep timer indicators) and bottom control panel (record, pause, seek, VOD quality, volume, EPG, fullscreen) — in both windowed and fullscreen modes; they appear on mouse movement and hide after 3 seconds (`_overlayHideTimer`); in fullscreen, the cursor is hidden along with them (`CursorHider`, invisible `CursorGrid`).

**Keyboard** (`OnPagePreviewKeyDown`, MainPage): the handler is attached to the **root XamlRoot element** — the tunneling event goes from the root to the focused element, and the window hosts the page inside a Grid+Frame, so subscribing on the page itself missed keys when nothing inside the page had focus. The root catches everything; open ContentDialogs are excluded by a focus check. The full list of hotkeys is in README; here the important thing is this routing rule and that arrows/PgUp/PgDn are not intercepted when navigation elements have focus (`IsNavigationControlFocused`, `VisualTreeHelper` traversal).

## 3. Playlists and Video Library

**Sources** — a `PlaylistSource` list in settings (`Dialogs/PlaylistSettingsDialog`), switching is done by `MainPage.SwitchPlaylistAsync`: stopping the player, reloading channels, favorites/groups/filter, EPG of the new playlist. Each source has its own set of EPG sources and its own auto-resume.

**Hub Page**: launch screen with 3 cards. Playlists — flyout "Load"/"Last". Portal — flyout "Load"/"Unwatched". Settings — flyout with safe settings (Playlists/Interface/Playback). Navigation via `Frame.Navigate(typeof(MainPage), tuple)`.

**M3U** (`M3UParserService`): classic parsing of `#EXTINF` (tvg-logo/tvg-id/tvg-rec).

**Portal** (`Services/VideoPortalService`, sources with `Type == "portal"`):
- Protocol — POST requests `{baseURL}/{command}.json` with JSON body; authentication — `"key"` field in the body of each request; the `flicks` command returns paginated items (server limit — 300, next page marker `{type:"next"}`), `flick` — stream and quality options (480/720/1080/auto as separate links).
- The client is "transparent": request objects from responses are passed to the server as-is, all fields are optional, unknown ones are ignored — new protocol commands do not require client changes. Each request/response is logged (truncated at 8 KB) — the protocol is refined based on logs.
- Portal key cache invalidation: SHA-256 hash of the key is stored in SQLite; when the key changes, the channel cache is re-downloaded.
- The catalog is cached as a playlist (`PlaylistDatabaseService`), category = group, and items store the request object (`PortalRequest`) instead of a link — links are short-lived.
- Seasons are separate catalog cards: `ParsePortalSeasonName` extracts the base name and season number(s) from the title, groups are built lazily and invalidated when channels change (`GetPortalSeasonSiblings`). Series episodes — a flat list from flick ("Episode N"), stored in `PlayerViewModel.VodEpisodes` and survives quality switches; switching episodes — `PlayVodEpisodeAsync` without a portal request, switching seasons — a full `PlayChannelAsync(interactive:false)` of the adjacent card.
- Playback (`MainPageViewModel.PlayChannelAsync`): on click, `flick` is executed (lazily, without caching), starts in VOD mode (`PlayerViewModel.IsVodPlaying`) — pause without restarting the stream, seeking on the fly via `PlaybackSession.Position`, quality selection — restart with a new link and position transfer.
- VOD resume: position is saved in SQLite (`VodResumeStore`) with pruning (max 200 entries). On VOD entry — dialog "continue from saved position?".

**Local video files** (`Services/LocalVideoFileService`, the "Video" card on the Hub): a `FileOpenPicker` (hwnd via `IInitializeWithWindow` — unpackaged too) builds a pseudo-channel with `Id = -1` (`IsLocalFile = true`): it is absent from the lists and the repository, UI lookups by Id must fall back to `SelectedChannel`, and `GetEPGEntriesAsync` returns empty for `channelId < 0`. `StreamUrl` is the raw disk path (not a file:/// URI: FFmpeg does not URL-decode percent-escapes — Cyrillic/spaces in the URI broke opening; the system fallback builds a correct URI itself). Everything after that is the regular VOD pipeline (`CreatePlayerAsync(isVod: true)`): pause/seek/fullscreen for free. Launching the app with a video file as a command-line argument — `App.GetCommandLineVideoFile`.

## 4. EPG (XMLTV)

`EPGService` loads sources of the active playlist (own list, fallback — global), merges them (`EpgSourceMerger`: the first source in the list wins when programs overlap in time) and matches them to channels: by `tvg-id` from the playlist → by a "name → tvg-id" lookup table (`Assets/epg-name-map.json`) → by normalized name (`EpgNameNormalizer`, timeshift suffixes are taken into account).

`XmlTvService` parses XMLTV with a ±3 day window (programs outside the window are not parsed at all — this is the main savings for feeds with hundreds of thousands of programs). **Important**: iterating over `programme`/`channel` children is done via the main reader with exit exactly at the closing tag — `ReadElementContentAsString()` on a reader from `ReadSubtree()` in .NET "eats" subsequent siblings, which for a long time caused only title to be read (desc/category/icons were lost). Cache of parsed feeds — MemoryPack+Brotli (`EpgCacheStore`, format version is invalidated when serializable fields change).

**Lazy loading**: on startup, `RecalculateCurrentProgramsAsync` loads only the current program for each channel (`GetCurrentProgramAsync`) — saving ~20MB. The full program list (`EPGEntries`) is loaded only on channel click (`LoadEPGForChannelAsync`). The EPG panel shows the program list of the selected channel at startup (for which `LoadEPGForChannelAsync` is called after `LoadEPGAsync`).

**Merged EPG cache** (`MergedEpgCache`): the merge result (program index by tvg-id + logos) is serialized with MemoryPack+Brotli next to the per-source caches. `TryLoadMergedCacheAsync` hits when the set of enabled sources matches (URLs and order) and the refresh period (`EpgRefreshDays`) has not expired for any source's download timestamp — skipping both the per-source cache reads and the `Merge` (seconds of CPU on hundreds of thousands of programs); the name index is not stored — it is rebuilt from ByChannel in milliseconds (`EpgSourceMerger.BuildNameIndex`). Dictionaries are rebuilt with `OrdinalIgnoreCase` comparers after reading (MemoryPack restores Dictionary with the default comparer). The cache is written after a full merge, only if all sources succeeded; "Refresh EPG" (`ClearAll`) wipes it entirely. Orphan cache cleanup (`CleanupOrphans`) treats all playlists' sources plus global ones and the merge keys of every source set as live — previously cleanup by active playlist deleted other playlists' EPG caches, making every launch/switch re-download and re-parse their XMLTV.

**EPG for portals**: a portal playlist without its own EPG sources no longer falls back to the global list (`AppSettings.GetActiveEpgSources`) — a VOD catalog does not need a TV schedule, and the fallback made it download and parse XMLTV uselessly on every portal open. Portal EPG appears only if sources are assigned to the playlist itself.

The current program of a channel (`CurrentProgramTitle/CurrentProgramDescription`) is recalculated by a timer (30 s); clicking a program that has started launches the archive.

## 5. Archive (timeshift)

Indicator — `tvg-rec`/`catchup-days` from the playlist (`ChannelViewModel.CatchupDays`). Launch: `ArchiveUrlBuilder.BuildUrl` adds `utc`/`lutc` parameters to the live URL — the provider returns a shifted HLS playlist.

HLS-timeshift is not searched on the fly, so seeking is a stream restart with a new starting point; position is calculated in `PlayerViewModel` based on wall clock time from the start of playback minus total pause time, not beyond the live edge. (Portal VOD, unlike archive, is seeked by the engine on the fly — see §3.) The user mechanics of the seek bar are described in the README.

## 6. Playback

`StreamService.CreatePlayerAsync` — single point of player creation (a new `MediaPlayer` for each channel):

1. **FFmpegInteropX + FFmpeg** — demuxing and decoding (the built-in Windows HLS stack does not decode HEVC in MPEG-TS, and AC-3 was removed from the system starting with 24H2). Configuration: decoder mode from settings (`VideoDecoderMode.Automatic` = GPU with fallback / `ForceFFmpegSoftwareDecoder` by default), `DownmixAudioStreamsToStereo = false` (multichannel sound is downmixed by the Windows audio engine — FFmpeg downmix is quieter), lookahead buffering 15s / 32 MB.
2. **Source lifetime**: `FFmpegMediaSource` is tied to the player via `ConditionalWeakTable` — without this, GC would collect the source mid-playback (stutters → audio loss → crash 0xC00D36B6).
3. **Fallback**: if FFmpeg couldn't open the URL — system `MediaSource.CreateFromUri`.
4. **Diagnostics**: a snapshot of stream parameters is placed into `CurrentDiagnostics`, the stats overlay (Ctrl+J) adds live metrics on a one-second tick. `StreamService.DiagnoseStreamUrl` checks the URL on error (HTTP status, timeout, availability). To measure the real stream speed, `LocalStreamProxy` routes FFmpeg through a local TCP proxy on 127.0.0.1 (HLS playlists are rewritten to proxy routes) and counts bytes; enabled by a toggle in playback settings, off by default.
5. **Audio normalization** — an FFmpeg audio filter per setting: `Dynamic` (dynaudnorm, boosts quiet channels, default) or `Loudness` (loudnorm, EBU R128 target); heavy filters may affect smoothness — the mode is logged on stream start.
5. Player errors are logged with codes (`MediaPlayer.MediaFailed`); `OnMediaFailed` is async with diagnostics.

**Pause** — only archive and portal VOD (spacebar, `ToggleArchivePause`): live broadcast cannot be paused, this is a deliberate limitation. For VOD, the same toggle works without archive clocks. `MediaPlayerElement` visibility is not bound to `IsPlaying` (collapsing the element on pause blanked the last frame to a gray screen) — the frame stays frozen and playback resumes from it. A pause state change shows a popup "Paused / Playing" indicator: `PlaybackStateBadge` (inside the video area grid — centered on the video in both windowed and fullscreen modes) is fed from `UpdateArchivePauseButton` (MainPage.Seek.cs), the single point that knows the state; the first calculation after playback start does not raise the badge, stopping playback resets the remembered state.

**Application shutdown**: a subscription to `MainWindow.Closed` stops/releases the player and recordings and calls `Environment.Exit(0)` — otherwise the media pipeline would keep the process alive for several seconds.

## 7. On-Disk Data

| What | Where |
|---|---|
| Settings (sources, portals, frequencies, volume, decoder, favorites) | `%LocalAppData%\IptvPlayer\settings.json` (atomic writes via `.tmp`; previous version in `settings.json.prev`, corrupted ones as `*.corrupt-*`) |
| Channel/catalog cache (SQLite, single DB for all playlists; legacy `playlist_cache_{id}.json` files are migrated into it once) | `%LocalAppData%\IptvPlayer\iptvplayer_cache.db` |
| Parsed XMLTV source cache and merged EPG cache (MemoryPack+Brotli) | `%LocalAppData%\IptvPlayer\cache\` |
| Recordings (ffmpeg, MPEG-TS without transcoding) | "Videos\IptvPlayer" or configured folder |
| Log (Serilog, daily rolling, 14 days) | `%LocalAppData%\IptvPlayer\logs\` |
| VOD resume positions (SQLite) | `%LocalAppData%\IptvPlayer\` |

In MSIX mode (Debug), `%LocalAppData%` paths are virtualized into the package; in unpackaged mode (Release/Inno), they are used directly — the code works identically in both.

## 8. Large Catalog Performance

Portal catalogs have 20k+ items; key decisions:
- `FilterChannels` replaces `DisplayedChannels` entirely (one ItemsSource change instead of thousands of CollectionChanged events). Selection in lists uses **OneWay + SelectionChanged**: TwoWay binding was overwriting `SelectedChannel` to null when clearing the hidden view's ItemsSource (video is bound to `SelectedChannel.IsPlaying` and would disappear); after rebuilding, selection is restored to controls by MainPage via the FilterChanged event.
- The grouped source for the fullscreen overlay (`RefreshOverlayChannelGroups`) is built only when the overlay is visible; on entering fullscreen — explicitly.
- The hidden list/poster grid view is disconnected from data (ItemsSource = null).
- Movie start: instantly from the catalog link, quality options are loaded by a background flick and applied (`PlayerViewModel.SetVodVariants`); series wait for flick (need the episode list).
- Buffer: live — `ReadAheadSeconds` (15s / 32+ MB), VOD — separate `VodReadAheadSeconds` (4s / 8+ MB): a large buffer on slow CDN was keeping VOD stream startup at several seconds.
- Memory optimization: `EPGEntry.Description` and `ChannelViewModel.CurrentProgram*` are nullable (~46 MB savings with 2000+ channels + 400k programs).

## 9. Application Updates

Semi-automatic update (`Services/UpdateService` + `MainPage.RunAutoUpdateCheckAsync`): background check 2 minutes after startup (no more than once per day — `AppSettings.LastUpdateCheckUtc`), GitHub API parsing is the same as the manual button in "About". The downloaded installer is verified by SHA256 (`assets[].digest`, if the source provided it). User consent — ContentDialog; installation — `setup.exe /VERYSILENT /NORESTART /SUPPRESSMSGBOXES` run from the shell (UAC: Program Files), the application closes normally, and after the silent install it is relaunched (a separate `[Run]` entry with `Check: WizardSilent` in .iss — does not affect interactive installs). While recordings are in progress, installation is deferred until the `RecordingsChanged` event. "Later" in the update dialog defers installation until the app closes: the downloaded installer path is kept in `App.PendingUpdateSetupPath`, and on real exit (not to tray) `MainWindow` launches the silent install via `App.TryStartPendingUpdateInstall`. Any error is silent: the old version continues to work (Inno installs over it).

## 10. Logging and DI

**Serilog.** The static logger is configured first thing in the `App` constructor (before `InitializeComponent` — global exception handlers must already be able to write to the log): Debug output (always) + file sink with daily rolling. Classes receive `ILogger<T>` via constructor (source in the log = class name); the file log is toggled off at runtime via a `LoggingLevelSwitch` in settings.

**DI.** `App` — composition root: `ServiceCollection` is assembled in the constructor, the provider is available as `App.Services`. All services and ViewModels are singletons (one session, one window). Pages resolve dependencies via `App.Services.GetRequiredService` in their constructors — WinUI does not allow injecting into XAML element constructors.

**MVVM conventions.** Properties use manual `SetProperty` instead of `[ObservableProperty]` (the generator does not create WinRT projectors — MVVMTK0045, important for AOT/ABI); actions use `[RelayCommand]`; MainPage code-behind is split into partial files by zones.

## 11. Partial File Split

**MainPage** (3133 → 1328 lines):

| File | Lines | Content |
|---|---|---|
| `MainPage.xaml.cs` | 1328 | Fields, constructor, InitializeAsync, OnNavigatedTo, Overlays, ToggleFullScreen |
| `MainPage.Portal.cs` | 264 | Portal API methods |
| `MainPage.Settings.cs` | 104 | Settings dialogs |
| `MainPage.Navigation.cs` | 375 | Playlist switching, navigation |
| `MainPage.VideoControls.cs` | 511 | Volume/Mute, Stretch, Sleep timer, Mini player, Always-on-top, Favorite/Reminder/Record |
| `MainPage.Seek.cs` | 660 | VOD seek/quality/season/episode, Archive seek, pause indicator, EPG, Fullscreen, PIN |
| `MainPage.LocalVideo.cs` | 41 | Local video files: file picking, playback start |
| `MainPage.FullScreen.cs` | 303 | Fullscreen mode |
| `MainPage.Hotkeys.cs` | 388 | Hotkeys (descriptions — F1 help, see HOTKEYS-SYNC) |
| `MainPage.Overlays.cs` | 450 | Overlays |
| `MainPage.StatsOverlay.cs` | 213 | Statistics |

**HubPage** (961 lines):

| File | Lines | Content |
|---|---|---|
| `HubPage.xaml.cs` | 961 | Launch screen: greeting, cards, custom flyout menus, hotkey help (F1) |

**MainPageViewModel** (1787 lines):

| File | Lines | Content |
|---|---|---|
| `MainPageViewModel.cs` | 941 | Initialization, filters, categories, EPG, SaveSettings |
| `MainPageViewModel.PortalFilters.cs` | 275 | Portal API + portal filters |
| `MainPageViewModel.Recording.cs` | 284 | Recording, reminders, favorites, archive |
| `MainPageViewModel.VodResume.cs` | 287 | VOD resume, PlayChannelAsync (interactive) |
