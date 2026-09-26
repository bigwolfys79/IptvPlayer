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
- `MainPage` (+ partial files `MainPage.ChannelOverrides/FullScreen/Hotkeys/LocalVideo/Navigation/Overlays/Portal/Seek/Settings/StatsOverlay/VideoControls.cs`) — all UI and overlays.
- `MainPageViewModel` (+ partial files `MainPageViewModel.PortalFilters/Recording/VodResume.cs`) — channel list logic, filtering, recording, VOD resume.
- `EpgViewModel` — EPG: loading, lazy per-channel loading, current program.
- `PlayerViewModel` — player management (FFmpegInteropX), archive, VOD.
- `ChannelViewModel` — channel model with nullable CurrentProgramTitle/Description properties.
- `Services/StreamService` — single point of player creation (FFmpegInteropX) + diagnostics.
- `Services/VideoPortalService` — video portal client (catalog + streams).
- `Services/EPGService` + `XmlTvService` — loading and matching the TV guide. Portal and online-cinema items (`IsPortalItem` / `IsVodCatalogItem`) are fully excluded from EPG: a guide lookup for them never triggers source loading or matching (early exits in `GetEPGEntriesAsync`/`GetCurrentProgramAsync`), and switching the playlist to online cinema/portal skips the EPG reload (`LoadEpgAfterPlaylistSwitchAsync`).

---

## 1. Application Startup

First, `App.OnLaunched` registers a single instance (`AppInstance.FindOrRegisterForKey`): a second launch does not create another process — activation is redirected to the running one, which restores its window from the tray (`ShowFromTray`). This removes concurrent `settings.json` writes by parallel instances.

Next, the license is checked (`LicenseService.CheckLicense`): personal use is unrestricted; commercial use is a 30-day trial (DPAPI token in HKLM) or a purchased offline license. The license is an `IPL1.{payload}.{RSA-2048 signature}` string; the signature is verified against an embedded public key, and the payload is bound to the HWID (volume serial + MachineGuid). It is stored in HKCU and re-verified on every launch. Clock rollback protection: `LastSeenUtc` (DPAPI + HKCU) — rolling the system clock back does not extend the trial/license. On trial expiry a dialog shows the HWID, a key field, and `.lic` import — successful activation continues the launch.

`App` → `MainWindow` → `HubPage` (if `ShowHubOnStartup`) or `MainPage`:

**Hub Page**: launch screen with dark gradient (`#0D1117→#161B22`), time-based greeting with DropShadow glow, animated accent line, 3 cards (Playlists/Portal/Settings) with spin-in animation. Custom flyout menus with screen-edge positioning.

**MainPage** → `InitializeAsync()` (the page is shown immediately, nothing is blocked):

1. Settings are loaded (`SettingsService`, `%LocalAppData%\IptvPlayer\settings.json`); volume is restored from them.
2. Channels of the active playlist (`PlaylistSource.Type`: `m3u` — parser, `m3u-vod` — parser with VOD handling, `portal` — portal catalog, all use the `PlaylistDatabaseService` cache (SQLite)) are placed into `ChannelRepository`, assigned sequential `Id` values, and populate `ViewModel.Channels`.
3. `SelectedChannel` is assigned immediately, `Task.Yield()` lets the UI render the list; then EPG is loaded in the background and auto-resume of the last channel is triggered.
4. After EPG loads — `LoadEPGForChannelAsync` for the selected channel (full program list in the EPG panel).

## 2. Rendering and Input

Layers of the right area are set by `Canvas.ZIndex`: video (1) → header/control panel (2) → EPG overlay (3).

**Channel list** (`ChannelItemTemplate`, shared between windowed and fullscreen overlay): logo (28x28, `StringToImageSourceConverter` with system cache) → archive indicator dot (`HasArchive`) → name; tooltip with description (for portal items). The template root is a `Grid` (not a horizontal `StackPanel`): a StackPanel measures children with infinite width and breaks text wrapping; the text block occupies a star-sized column, so the current program title wraps up to two lines (`TextWrapping="Wrap"` + `MaxLines="2"`) and is ellipsized beyond that.

**Fullscreen mode** (`MainPage.FullScreen.cs` → `MainWindow.SetOsFullScreen`): OS presenter `AppWindowPresenterKind.FullScreen` stretches the window to fill the entire monitor (verified by measurements — the client area is exactly screen-sized); content wrapper is done manually: hiding the TitleBar row, collapsing channel list columns, zeroing out the decorative `Padding` of the video container (`VideoAreaBorder`, 12 px in windowed mode — without zeroing, bands appeared at the edges of the screen), rebuilding the video layout (`ForceVideoRelayout` — after changing the presenter, the DComp island draws using the old coordinates).

**Window modes** (`MainWindow`): "always on top" (Ctrl+T, `SetAlwaysOnTop`) — `OverlappedPresenter.IsAlwaysOnTop` without resizing or hiding panels, unlike the mini-player. In fullscreen and mini-player the flag is not touched (the window is already on top); the state is session-only and not persisted to settings. Exiting fullscreen switches the presenter, creating a new `OverlappedPresenter` — the enabled flag is restored manually. The mini-player (`ToggleMiniPlayer`) remembers `_alwaysOnTopBeforeMini`: if the mode was enabled before minimizing, it stays on after returning to the regular window.

**Themes (Light/Dark)**: all UI colors are ThemeResource brushes from the theme dictionaries in `App.xaml` (REC/live/archive accents, overlay scrims and texts `OverlayScrim*/OverlayFg*`) and from `ThemeDictionaries` in `HubPage.xaml`. The ContentDialog popup layer does not inherit the window root's `RequestedTheme` — therefore all dialogs are created only via `Controls/ThemedContentDialog`, which copies the root's `ActualTheme`. Modal display is serialized: every `ShowAsync` goes through the static `Controls/DialogQueue`, which keeps at most one dialog open (semaphore-backed queue) and, on the "only one ContentDialog" collision (0x80070489), logs and returns `None` instead of an unhandled exception. Icons built in code (`Controls/AppIcons`) are colored per the effective theme; `ApplyTheme` rebuilds them on theme change.

**Overlays**: top header (channel name, current program + description, progress, archive/sleep timer indicators) and bottom control panel (record, pause, seek, VOD quality, volume, EPG, fullscreen) — in both windowed and fullscreen modes; they appear on mouse movement and hide after 3 seconds (`_overlayHideTimer`); in fullscreen, the cursor is hidden along with them (`CursorHider`, invisible `CursorGrid`).

**Frame server render upscale** (experimental, `FrameServerRenderer`): when enabled, MediaPlayer runs with `IsVideoFrameServerEnabled` and draws nothing itself — the frame is copied into a BGRA texture of the stream resolution (`Direct3DInterop.CreateBgraSurface`), and drawing into the `CanvasSwapChainPanel` follows the priority chain: **D3D11 Video Processor** (`VideoProcessorInterop` + `FrameServerVideoProcessor`, hardware scaling by the driver — on NVIDIA/Intel this is RTX VSR / Intel VSR when enabled in the driver control panel) → **FSR 1.0** (EASU+RCAS shaders, `Assets/Shaders`) → Catmull-Rom bicubic + CAS → engine linear scaling. Any error in a path falls back once to the next one without crashing the player; the actual path is logged by the "Render upscale" line.

**Keyboard** (`OnPagePreviewKeyDown`, MainPage): the handler is attached to the **root XamlRoot element** — the tunneling event goes from the root to the focused element, and the window hosts the page inside a Grid+Frame, so subscribing on the page itself missed keys when nothing inside the page had focus. The root catches everything; open ContentDialogs are excluded by a focus check. The full list of hotkeys is in README; here the important thing is this routing rule and that arrows/PgUp/PgDn are not intercepted when navigation elements have focus (`IsNavigationControlFocused`, `VisualTreeHelper` traversal).

**Adaptive layout**: on window resize (`RootGrid.SizeChanged` plus the toolbars' own `SizeChanged`, logic in `MainPage.Overlays.cs`) the player toolbars — windowed `WindowedVideoOverlay` and fullscreen `FullScreenBottomBar` — are scaled with a `ScaleTransform` (anchor bottom-center, floor 0.6): a toolbar's `DesiredSize` is unaffected by both RenderTransform and constraint clipping (unlike `ActualWidth`), so the "desired" width is measured without resetting the transform and recalculations do not accumulate; seek panels are accounted for automatically (they change the toolbar width). The windowed overlay's bottom bar is single-row: "previous channel" and "record" sit in the main row, while the second row (`VideoOverlayTopRow`) holds only portal context controls (seasons/episodes/quality/audio) and archive controls (pause, back to live, seeking) and collapses when empty (`UpdateWindowedOverlayTopRow` is called from the visibility toggles in `MainPage.Seek.cs`). The channel list auto-collapses when the root width drops below 640 px (same column mechanics as fullscreen mode) and comes back when widened; the width stored in settings (`AppSettings.ChannelListWidth`) is not overwritten by auto-collapse — it lives in `_channelListExpandedWidth`. Auto-collapse does not apply in fullscreen or in the no-playlist mode (their own logic drives the columns). The same technique on HubPage, plus two details: the cards sit in `Controls/HubWrapPanel` (a custom line-wrapping panel — neither WinUI nor the CommunityToolkit ships one), on narrow windows they flow into 2 rows or a column; the "desired" width for scaling is measured with an explicit `Measure(PositiveInfinity)` — both `ActualWidth` and `DesiredSize` are clipped by the constraint when overflowing. The hub's custom flyout (`ShowCustomFlyout`) is centered in the window: anchor positioning mixed the visual coordinates of the scaled panel with the layout width of the button, so menus poked past the edge.

## 3. Playlists and Video Library

**Sources** — a `PlaylistSource` list in settings (`Dialogs/PlaylistSettingsDialog`), switching is done by `MainPage.SwitchPlaylistAsync`: stopping the player, reloading channels, favorites/groups/filter, EPG of the new playlist. Each source has its own set of EPG sources and its own auto-resume.

**Hub Page**: launch screen with 3 cards. Playlists — flyout "Load"/"Last". Portal — flyout "Load"/"Unwatched". Settings — flyout with safe settings (Playlists/Interface/Playback). Navigation via `Frame.Navigate(typeof(MainPage), tuple)`.

**M3U** (`M3UParserService`): classic parsing of `#EXTINF` (tvg-logo/tvg-id/tvg-rec/tvg-year/tvg-genre) and `#EXTDESC:` (description of the entry sitting between `#EXTINF` and its URL; the trailing "[page: …]" marker is stripped). For `m3u-vod` sources `deriveGenreFromGroup` is enabled: when `tvg-genre` is absent, the genres are the `group-title` segments from the 4th one on (the first three are the structural prefix like "Cinema / all movies / Movies"), filtered against the closed `KnownGenres` whitelist (non-genre segments — series titles, "Season N" — are dropped; last-segment fallback only applies when it is in the list). The same list filters `tvg-genre` tokens.

**m3u video catalog** (`PlaylistSource.Type == "m3u-vod"`, `IsVodCatalog`): same m3u parser, but `MainPage.Portal` sets `ViewModel.SetVodSource(true)` after loading. The flag enables: the "List/Posters" toggle (`IsPosterViewAvailable`, shared with portals), hiding the group combo (`IsGroupFilterVisible` — category selection, like the portal, is done by the genre filter) and the VOD playback mode in `PlayChannelAsync` — `StartPlaybackAsync(isVod: true)` with the "Resume playback" dialog (`OfferVodResumeAsync`) instead of `PlayLiveAsync`; pause, seeking and quality selection from the master playlist work like the portal (the overlay is gated on `Player.IsVodPlaying`, nothing portal-specific). `Genre` stores a comma-separated list — the genre filter matches individual tokens (`FilterChannels`), a multi-genre movie appears under each of its genres; `RefreshGroups` builds the genre list as the union of tokens. Genre/year filters are local (no server filters). The `PlaylistCache` stores `Description`/`Year`/`Genre`; format version 6 forces old caches to reload with the new fields. For local m3u playlists (regular and catalogs) the cache keeps a source snapshot (`SourceLastWriteTimeUtc`/`SourceLength`/SHA-256 `ContentHash`): on load a fast path compares mtime+size and confirms with the hash on mismatch — a file changed between runs is reparsed regardless of the refresh period, while a content-preserving touch does not trigger a re-download. For URL playlists the detection is unavailable (a change cannot be checked without downloading) — only the settings period applies.

**Online cinema** (`Services/OnlineCinema/`, sources with `Type == "online-cinema"`, `IsOnlineCinema`): the movie catalog is taken directly from the website (v1) via a hidden WebView2 — the site sits behind Cloudflare, a plain HttpClient gets 403; the persistent WebView2 profile (`%LocalAppData%\IptvPlayer\WebView2`) keeps `cf_clearance`, and if a challenge cannot be cleared automatically the browser window surfaces for a manual pass. Components (`Services/OnlineCinema/`):
- `KinogoSite` — site adapter: category map (`name → /path/`), JS snippets for card parsing (`article.shortStory`, selectors taken from the external reference parser) and pagination (`a[href*='/page/']`), plus the session year filter (POST `xsort=1&xs_field=year&xs_value=N`, same as the parser). A new site = a new class of the same shape.
- `OnlineCinemaBrowserService` — the single app-wide WebView2: off-screen window, `ExecuteScriptAsync` for JS, an `api/playlist/load` interception bridge (a `fetch`/`XMLHttpRequest` wrapper injected into every document/frame, responses arrive via `postMessage`). All public methods require the UI thread.
- `OnlineCinemaCatalogService` — orchestration: the first sync loads 1 page per category; on later opens first pages older than 6 hours refresh in the background; `LoadNextPageAsync` fetches the next page of a category while it does not exceed the pagination total; `SyncYearAsync` applies the session year filter and stores page 1 under the "year N" pseudo-category (the year filter changes the site's first page, so the year is re-requested rather than only filtered locally). 2–3 s pauses between pages — anti-ban.
- `CatalogDatabaseService` — a separate DB `%LocalAppData%\IptvPlayer\online_cinema.db` (`catalog_items` keyed `site_id+page_url`, `catalog_pages` keyed `site_id+category+page_number`): the source opens instantly from the DB, with no network. The m3u playlist cache is not used for this type.
- `OnlineCinemaStreamResolver` — the stream is resolved when a movie starts (`PlayChannelAsync`, the `PageUrl` branch): film page → trusted clicks on the embed iframes via the DevTools protocol (`Input.dispatchMouseEvent`, same mechanism as Playwright — the site hosts several providers; ads and YouTube are excluded; each iframe is scrolled into view first, coordinates are CSS viewport pixels) → the intercepted m3u8 response. Interception is browser-level (`WebResourceResponseReceived` + `GetContentAsync`, catches all frames): the in-page `fetch`/`XHR` bridge does not fire inside cross-origin iframes (verified live). Extraction is provider-agnostic: JSON `{"file": …}` (provider `api/playlist/load`) or the response URL when the body is a provider playlist. CDN links carry a date-bound token and expire — never cached; fallback to the catalog `StreamUrl`, fallback to a YouTube trailer. The master playlist is parsed before playback starts: the maximum-resolution rendition is played by default (FFmpeg's own master choice is the lowest one), `PreferredQuality` is honored; the quality picker gets all renditions + "Авто". The whole process is invisible: the WebView2 window always stays off-screen, without a taskbar button (`WS_EX_TOOLWINDOW`) and Alt-Tab presence; there is no window surfacing — an unsolvable Cloudflare challenge fails the resolution with an error message in the UI. Tracks form an `OnlineCinemaPlaylist` tree (series: seasons → episodes → voiceovers; films: a flat voiceover list) stored in `PlayerViewModel.OnlineCinemaPlaylist`; the season/episode/voiceover combos in the overlay (`MainPage.Seek.UpdateVodSeasonEpisodeCombos`) are built from it; switching a leaf goes through `PlayOnlineCinemaLeafAsync` (lazy `api/playlist/load` POST, URL and quality renditions cached on the leaf, so a switch plays a media playlist rather than the master — FFmpeg would otherwise pick the default lowest program; position preserved when only the voiceover changes); on switch the quality is carried over, or the maximum is picked when unavailable.
- UI: `MainPageViewModel.OnlineCinema.cs` — `SetOnlineCinemaSource` shows the group filter (category = group) and the "Load more" button (`LoadMoreOnlineCinemaCommand`); a selected year syncs with the site in the background (`SyncOnlineCinemaYearAsync`). Loading lives in `MainPage.OnlineCinema.cs` (`LoadPlaylistChannelsAsync` early-exits into `LoadOnlineCinemaAsync`). Background collection during playback (`OnlineCinemaBackgroundCollectAsync`, timer in `MainPage.OnlineCinema.cs` with the `AppSettings.OnlineCinemaCollectIntervalSeconds` interval, 120 by default, 30–7200; the interval is re-read on every tick) re-reads the visible list from the DB at most once per `AppSettings.OnlineCinemaListRefreshMinutes` (60 by default, 0 — never); `ReloadOnlineCinemaChannelsAsync` keeps the selected group/genre/year (`RefreshGroups(group, keepFilters: true)`) and moves the selection to the fresh instance of the playing item (by `PageUrl`, with the `IsPlaying` flag) — rebuilding the list neither resets the filters nor kicks back to "All groups".

**Portal** (`Services/VideoPortalService`, sources with `Type == "portal"`):
- Protocol — POST requests `{baseURL}/{command}.json` with JSON body; authentication — `"key"` field in the body of each request; the `flicks` command returns paginated items (server limit — 300, next page marker `{type:"next"}`), `flick` — stream and quality options (480/720/1080/auto as separate links).
- The client is "transparent": request objects from responses are passed to the server as-is, all fields are optional, unknown ones are ignored — new protocol commands do not require client changes. Each request/response is logged (truncated at 8 KB) — the protocol is refined based on logs.
- Portal key cache invalidation: SHA-256 hash of the key is stored in SQLite; when the key changes, the channel cache is re-downloaded.
- The catalog is cached as a playlist (`PlaylistDatabaseService`), category = group, and items store the request object (`PortalRequest`) instead of a link — links are short-lived.
- Portal filters: the server ignores `fid` when `filter`/`genre`/`years` are present (and vice versa), so "category + genre" cannot be combined in a single request. With a category selected plus a genre/year, `LoadFilteredAsync` fetches the portal-wide filter result and intersects it with the category's id set (`FilterByCategoryIds`). The id set is built from the in-memory full-catalog snapshot (`MainPageViewModel.PortalFilters`, parsing item `PortalRequest` objects) — zero network requests; without a snapshot the service collects it over the network (30 min cache). Filter pages load in parallel (4 at a time) — sequential pagination took about a minute on a large genre. "All types" without filters restores the full catalog from a snapshot captured before the first server-side filter load.
- Seasons are separate catalog cards: `ParsePortalSeasonName` extracts the base name and season number(s) from the title, groups are built lazily and invalidated when channels change (`GetPortalSeasonSiblings`). Series episodes — a flat list from flick ("Episode N"), stored in `PlayerViewModel.VodEpisodes` and survives quality switches; switching episodes — `PlayVodEpisodeAsync` without a portal request, switching seasons — a full `PlayChannelAsync(interactive:false)` of the adjacent card.
- Playback (`MainPageViewModel.PlayChannelAsync`): on click, `flick` is executed (lazily, without caching), starts in VOD mode (`PlayerViewModel.IsVodPlaying`) — pause without restarting the stream, seeking on the fly via `PlaybackSession.Position`, quality selection — restart with a new link and position transfer. Episodes often lack a variants dict — then `StreamService.ProbeVodMasterVariantsAsync` parses the m3u8 master playlist (RESOLUTION → rendition, the master itself = "Auto") and the renditions feed the quality switcher (`HlsMasterVariantsTests`). The overlay "Audio" button switches tracks on the fly (`StreamService.TrySelectAudioTrack`); when tracks carry no language metadata (typically one audio duplicated per quality rendition), the button is hidden.
- VOD resume: position is saved in SQLite (`VodResumeStore`) with pruning (max 200 entries). On VOD entry — dialog "continue from saved position?".

**Local video files** (`Services/LocalVideoFileService`, the "Video" card on the Hub): a `FileOpenPicker` (hwnd via `IInitializeWithWindow` — unpackaged too) builds a pseudo-channel with `Id = -1` (`IsLocalFile = true`): it is absent from the lists and the repository, UI lookups by Id must fall back to `SelectedChannel`, and `GetEPGEntriesAsync` returns empty for `channelId < 0`. `StreamUrl` is the raw disk path (not a file:/// URI: FFmpeg does not URL-decode percent-escapes — Cyrillic/spaces in the URI broke opening; the system fallback builds a correct URI itself). Everything after that is the regular VOD pipeline (`CreatePlayerAsync(isVod: true)`): pause/seek/fullscreen for free. Launching the app with a video file as a command-line argument — `App.GetCommandLineVideoFile`.

## 4. EPG (XMLTV)

`EPGService` loads sources of the active playlist (own list, fallback — global), merges them (`EpgSourceMerger`: the first source in the list wins when programs overlap in time) and matches them to channels: by `tvg-id` from the playlist → by learned aliases (the `epg_aliases` table of the cache DB) → by a "name → tvg-id" lookup table (`Assets/epg-name-map.json`) → by normalized name (`EpgNameNormalizer`, timeshift suffixes are taken into account). For channels with a "+N" suffix ("Первый канал +2", including "+N (city)" and "+N HD" shapes) the cascade changes: aliases are skipped (they memorized the fuzzy base match), after the strict name table a second timeshift-preserving name index is checked (`BuildNameIndex(preserveTimeshift: true)` — the source's own "+N" variant with exact times), then — inheritance: the base channel's schedule is shifted by N hours (`CreateTimeshiftedEntries` — copies of programmes with changed StartTime/EndTime and their own EventId); on failure the channel stays without a programme instead of showing the unshifted schedule. On every merge, programmes that repeat the previous load one-to-one (channel + start/end times + title/description/category/channel name) reuse the previous `EPGEntry` instances (`EpgSourceMerger.ApplyDeltaReuse`) — programme UI state (`IsCurrent`, reminders) survives the update; only changed objects are replaced.

**Learned aliases** (`epg_aliases` in `iptvplayer_cache.db`, `PlaylistDatabaseService.GetEpgAliasesAsync/UpsertEpgAliasesAsync`): after every EPG load, unambiguous "XMLTV channel name → playlist channel" matches (via the name table or a unique normalized-name hit, name ≥ 2 chars, http(s) channel) are memorized as tuples of (leniently normalized XMLTV display-name, xmltv-id, channel StreamUrl). On subsequent refreshes the alias is applied before the name tables — a channel with a renaming or without tvg-id keeps its program guide. On load, aliases whose xmltv-id is absent from the current sources are filtered out; a re-learned name overwrites the previous mapping (INSERT OR REPLACE).

**Source statuses**: a real network load of a source (not from cache) is recorded in `EPGSource.LastError/LastSuccessAt` (`XmlTvService.SourceLoadFinished` → `EPGService.OnSourceLoadFinished`, serialized by a semaphore) and persisted to settings.json — the "Playlists" dialog shows the last successful load or the error text under each URL. When a source is added it is validated by `XmlTvService.ValidateEpgSourceAsync`: the head of the response (up to 64 KB) is downloaded, gzip is detected by the `0x1F 0x8B` magic bytes and decompressed, and the document is checked for XMLTV markers (`<tv`, `<channel`, `<!DOCTYPE tv`) — a broken source cannot be added.

`XmlTvService` parses XMLTV with a ±3 day window (programs outside the window are not parsed at all — this is the main savings for feeds with hundreds of thousands of programs). **Important**: iterating over `programme`/`channel` children is done via the main reader with exit exactly at the closing tag — `ReadElementContentAsString()` on a reader from `ReadSubtree()` in .NET "eats" subsequent siblings, which for a long time caused only title to be read (desc/category/icons were lost). Cache of parsed feeds — MemoryPack+Brotli (`EpgCacheStore`, format version is invalidated when serializable fields change).

**Lazy loading**: on startup, `RecalculateCurrentProgramsAsync` loads only the current program for each channel (`GetCurrentProgramAsync`) — saving ~20MB. The full program list (`EPGEntries`) is loaded only on channel click (`LoadEPGForChannelAsync`). The EPG panel shows the program list of the selected channel at startup (for which `LoadEPGForChannelAsync` is called after `LoadEPGAsync`).

**Merged EPG cache** (`MergedEpgCache`): the merge result (program index by tvg-id + logos) is serialized with MemoryPack+Brotli next to the per-source caches. `TryLoadMergedCacheAsync` hits when the set of enabled sources matches (URLs and order) and the refresh period (`EpgRefreshDays`) has not expired for any source's download timestamp — skipping both the per-source cache reads and the `Merge` (seconds of CPU on hundreds of thousands of programs); the name index is not stored — it is rebuilt from ByChannel in milliseconds (`EpgSourceMerger.BuildNameIndex`). Dictionaries are rebuilt with `OrdinalIgnoreCase` comparers after reading (MemoryPack restores Dictionary with the default comparer). The cache is written after a full merge, only if all sources succeeded; "Refresh EPG" (`ClearAll`) wipes it entirely. The XMLTV parsing window is set by the archive depth setting (`EpgArchiveDaysBack`, 1/3/7 days back + 3 days forward) and is part of the per-source disk cache key — changing the setting re-downloads the source. Orphan cache cleanup (`CleanupOrphans`) treats all playlists' sources plus global ones and the merge keys of every source set as live — previously cleanup by active playlist deleted other playlists' EPG caches, making every launch/switch re-download and re-parse their XMLTV. An expired source cache no longer blocks loading when a refresh is due: `XmlTvService.LoadAsync` returns it immediately (stale-while-revalidate — the parsing window still covers the current hours) while the re-download runs in the background (`StartBackgroundRefresh`: per-key dedup, its own CTS instead of the caller's token, errors only logged); without this, at a due refresh time the channel list stayed without the program guide until the download finished, and a network failure left EPG empty until a manual "Refresh EPG".

**EPG for portals and m3u catalogs**: a portal playlist or m3u video catalog without its own EPG sources no longer falls back to the global list (`AppSettings.GetActiveEpgSources`) — a VOD catalog does not need a TV schedule, and the fallback made it download and parse XMLTV uselessly on every open. EPG appears only if sources are assigned to the playlist itself.

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
5. **Audio normalization and boost** — FFmpeg audio filters per setting: `Dynamic` (dynaudnorm, boosts quiet channels, default) or `Loudness` (loudnorm, EBU R128 target), plus an optional `volume=<gain>` (`AudioVolumeBoost`, 125/150/200% — normalization evens out loudness but never raises it above the source level; the gain is appended to the end of the chain). Filters change live via `ApplyAudioFilters` (no player rebuild needed); heavy filters may affect smoothness — the mode is logged on stream start.
6. **Preferred audio language auto-selection** — the `PreferredAudioLanguage` setting (default `rus`; comma-separated language codes in priority order): when opening a stream with multiple audio tracks, `StreamService.SelectPreferredAudioIndex` picks the first track whose FFmpeg metadata language matches the priority (rus/ru/russian etc. are equivalent); without language metadata or a match the stream's default track is kept. Selection happens before the stream is handed to `MediaPlayer` and skips "empty" tracks; each track's language is logged on stream open.
7. Player errors are logged with codes (`MediaPlayer.MediaFailed`); `OnMediaFailed` is async with diagnostics.

**Pause** — only archive and VOD (portal and m3u catalog; spacebar, `ToggleArchivePause`): live broadcast cannot be paused, this is a deliberate limitation. For VOD, the same toggle works without archive clocks. `MediaPlayerElement` visibility is not bound to `IsPlaying` (collapsing the element on pause blanked the last frame to a gray screen) — the frame stays frozen and playback resumes from it. A pause state change shows a popup "Paused / Playing" indicator: `PlaybackStateBadge` (inside the video area grid — centered on the video in both windowed and fullscreen modes) is fed from `UpdateArchivePauseButton` (MainPage.Seek.cs), the single point that knows the state; the first calculation after playback start does not raise the badge, stopping playback resets the remembered state.

**Application shutdown**: a subscription to `MainWindow.Closed` stops/releases the player and recordings and calls `Environment.Exit(0)` — otherwise the media pipeline would keep the process alive for several seconds.

## 7. Parental Control

`Services/ParentalControlService` — pure static logic (covered by unit tests): when control is enabled (`ParentalControlEnabled`), channels of the selected groups (`ParentalControlBlockedGroups`, case-insensitive) are hidden from the list. The PIN is stored as PBKDF2-SHA256 (100,000 iterations, "salt:hash" in base64) — never plaintext; with no PIN set, control works as plain group hiding ("hide from guests").

There are three unlock kinds: via the PIN dialog buttons (15/30/45/60 min or "until shutdown" — `ParentalControlUnlockedUntilUtc`, persisted to settings) and via **Enter** — unlocking only the requested channel "until switch" (`ParentalTempUnlockedChannel/Group`, not written to settings.json). The PIN dialog (`ShowParentalPinDialogAsync`, MainPage.Seek.cs, `ThemedContentDialog`) guards against double opening (`_pinDialogInProgress`); Enter with a correct PIN in the input field unlocks only that channel, durations remain on the buttons.

Channel launch goes through `MainPageViewModel.EnsureChannelAllowedAsync`: the one-time "until switch" unlock is cleared on **any** subsequent channel launch (including auto-resume of the same channel — the PIN is asked again). The daily limit (`ParentalDailyLimitMinutes`) is checked first and is independent of the PIN: once exhausted, no channel starts until midnight. Watched time is accumulated by a one-second tick (`CheckDailyWatchLimit`, MainPage.Seek.cs) while the player is actually playing (not paused/stopped): `ParentalWatchedSeconds` with the date `ParentalWatchedDate` (local, reset at midnight via `DailyDateKey`). When the limit is exhausted during playback, playback stops and a dialog shows the time until reset (`TimeUntilReset`).

## 8. Channel overrides (move/remove)

User channel overrides on top of the playlist: moving to another group and removing from the list (right-click context menu, the `ChannelItemTemplate` and poster view; handlers in MainPage.ChannelOverrides.cs). Stored in the `channel_overrides` table of the cache DB (`PlaylistDatabaseService`, key `playlist_id + stream_url`, with `tvg_id`/name snapshots for fallback) — they survive an m3u re-download since the channels table is fully rewritten on cache save. Applied after every playlist load (`ChannelOverrideMatcher.Apply`, called from `InitializeAsync`/`ApplyPlaylistAsync`): matching by `stream_url` → `tvg-id` → normalized name (`EpgNameNormalizer`); a non-match leaves an orphan entry until restored. Restoration — a button in the playlist settings dialog (list of overrides with checkboxes, restore selected or clear all, then reloading the active playlist via `ReloadActivePlaylistAsync`). Actions on channels of blocked groups (move, remove, opening the restoration screen containing such entries) require the PIN — `ParentalControlService.IsPinRequiredForGroup` (control enabled + PIN set + group blocked + no active unlock).

## 9. On-Disk Data

| What | Where |
|---|---|
| Settings (sources, portals, frequencies, volume, decoder, favorites) | `%LocalAppData%\IptvPlayer\settings.json` (atomic writes via `.tmp`; previous version in `settings.json.prev`, corrupted ones as `*.corrupt-*`) |
| Channel/catalog cache, learned EPG aliases and channel overrides (SQLite, single DB for all playlists; legacy `playlist_cache_{id}.json` files are migrated into it once) | `%LocalAppData%\IptvPlayer\iptvplayer_cache.db` |
| Parsed XMLTV source cache and merged EPG cache (MemoryPack+Brotli) | `%LocalAppData%\IptvPlayer\cache\` |
| Recordings (ffmpeg, MPEG-TS without transcoding) | "Videos\IptvPlayer" or configured folder |
| Log (Serilog, daily rolling, 14 days) | `%LocalAppData%\IptvPlayer\logs\` |
| VOD resume positions (SQLite) | `%LocalAppData%\IptvPlayer\` |

In MSIX mode (Debug), `%LocalAppData%` paths are virtualized into the package; in unpackaged mode (Release/Inno), they are used directly — the code works identically in both.

## 10. Large Catalog Performance

Portal catalogs have 20k+ items; key decisions:
- `FilterChannels` replaces `DisplayedChannels` entirely (one ItemsSource change instead of thousands of CollectionChanged events). Selection in lists uses **OneWay + SelectionChanged**: TwoWay binding was overwriting `SelectedChannel` to null when clearing the hidden view's ItemsSource (video is bound to `SelectedChannel.IsPlaying` and would disappear); after rebuilding, selection is restored to controls by MainPage via the FilterChanged event.
- The grouped source for the fullscreen overlay (`RefreshOverlayChannelGroups`) is built only when the overlay is visible; on entering fullscreen — explicitly.
- The hidden list/poster grid view is disconnected from data (ItemsSource = null).
- Movie start: instantly from the catalog link, quality options are loaded by a background flick and applied (`PlayerViewModel.SetVodVariants`); series wait for flick (need the episode list).
- Buffer: live — `ReadAheadSeconds` (15s / 32+ MB), VOD — separate `VodReadAheadSeconds` (4s / 8+ MB): a large buffer on slow CDN was keeping VOD stream startup at several seconds.
- Memory optimization: `EPGEntry.Description` and `ChannelViewModel.CurrentProgram*` are nullable (~46 MB savings with 2000+ channels + 400k programs).

## 11. Application Updates

Semi-automatic update (`Services/UpdateService` + `MainPage.RunAutoUpdateCheckAsync`): background check 2 minutes after startup (no more than once per day — `AppSettings.LastUpdateCheckUtc`), GitHub API parsing is the same as the manual button in "About". The downloaded installer is verified by SHA256 (`assets[].digest`, if the source provided it). User consent — ContentDialog; installation — `setup.exe /VERYSILENT /NORESTART /SUPPRESSMSGBOXES` run from the shell (UAC: Program Files), the application closes normally, and after the silent install it is relaunched (a separate `[Run]` entry with `Check: WizardSilent` in .iss — does not affect interactive installs). While recordings are in progress, installation is deferred until the `RecordingsChanged` event. "Later" in the update dialog defers installation until the app closes: the downloaded installer path is kept in `App.PendingUpdateSetupPath`, and on real exit (not to tray) `MainWindow` launches the silent install via `App.TryStartPendingUpdateInstall`. Any error is silent: the old version continues to work (Inno installs over it).

## 12. Logging and DI

**Serilog.** The static logger is configured first thing in the `App` constructor (before `InitializeComponent` — global exception handlers must already be able to write to the log): Debug output (always) + file sink with daily rolling. Classes receive `ILogger<T>` via constructor (source in the log = class name); the file log is toggled off at runtime via a `LoggingLevelSwitch` in settings.

**DI.** `App` — composition root: `ServiceCollection` is assembled in the constructor, the provider is available as `App.Services`. All services and ViewModels are singletons (one session, one window). Pages resolve dependencies via `App.Services.GetRequiredService` in their constructors — WinUI does not allow injecting into XAML element constructors.

**MVVM conventions.** Properties use manual `SetProperty` instead of `[ObservableProperty]` (the generator does not create WinRT projectors — MVVMTK0045, important for AOT/ABI); actions use `[RelayCommand]`; MainPage code-behind is split into partial files by zones.

## 13. Partial File Split

**MainPage** (4960 lines total):

| File | Lines | Content |
|---|---|---|
| `MainPage.xaml.cs` | 1098 | Fields, constructor, InitializeAsync, OnNavigatedTo, Overlays, ToggleFullScreen |
| `MainPage.Portal.cs` | 262 | Portal API methods |
| `MainPage.Settings.cs` | 112 | Settings dialogs |
| `MainPage.Navigation.cs` | 387 | Playlist switching, navigation |
| `MainPage.VideoControls.cs` | 525 | Volume/Mute, Stretch, Sleep timer, Mini player, Always-on-top, Favorite/Reminder/Record |
| `MainPage.ChannelOverrides.cs` | 300 | Move/remove channel, overrides apply, PIN approval dialog |
| `MainPage.Seek.cs` | 825 | VOD seek/quality/season/episode, Archive seek, pause indicator, EPG, Fullscreen, parental control (PIN dialog, daily watch limit) |
| `MainPage.LocalVideo.cs` | 32 | Local video files: file picking, playback start |
| `MainPage.FullScreen.cs` | 231 | Fullscreen mode |
| `MainPage.Hotkeys.cs` | 351 | Hotkeys (descriptions — F1 help, see HOTKEYS-SYNC) |
| `MainPage.Overlays.cs` | 567 | Overlays |
| `MainPage.StatsOverlay.cs` | 178 | Statistics |
| `MainPage.Localization.cs` | 92 | x:Uid localization via L.T (PrimaryLanguageOverride unavailable in unpackaged builds) |

**HubPage** (1025 lines total):

| File | Lines | Content |
|---|---|---|
| `HubPage.xaml.cs` | 1005 | Launch screen: greeting, cards, custom flyout menus, hotkey help (F1) |
| `HubPage.Localization.cs` | 20 | x:Uid localization via L.T |

**MainPageViewModel** (1882 lines total):

| File | Lines | Content |
|---|---|---|
| `MainPageViewModel.cs` | 930 | Initialization, filters, categories, EPG, SaveSettings, parental control (EnsureChannelAllowedAsync) |
| `MainPageViewModel.PortalFilters.cs` | 339 | Portal API + portal filters |
| `MainPageViewModel.Recording.cs` | 307 | Recording, reminders, favorites, archive |
| `MainPageViewModel.VodResume.cs` | 306 | VOD resume, PlayChannelAsync (interactive) |
