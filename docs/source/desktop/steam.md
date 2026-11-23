# Game artwork and Steam metadata

[Desktop source index](README.md)

## `avalonia/LGLauncher.Desktop/Launcher.Artwork.cs`

Owns the artwork lifecycle in the main window. It selects custom or Steam backdrops, decodes and samples images off the UI thread, calculates framing, and maps a sampled bitmap onto the title, sidebar, status, and search glass. It also owns artwork drag/zoom editing and pointer parallax, card/header caching, and sidebar icon loading, including the Windows executable icon fallback. Game cards prefer their saved custom portrait and fall back to the Steam portrait and header.

The artwork sample supplies an optional accent alongside framing and glass. On Game Details the main window applies it to the accent and selection resources when the preference is enabled. A game-to-game switch holds the previous palette until the next artwork supplies an accent or its load finishes without one. The configured default accent applies everywhere else and returns immediately when leaving Game Details. Both custom and artwork accents tint the selected Library row, input focus and text selection, and enabled switches. The switch knob and track use paired contrast colours in each interactive state.

When motion is enabled, an artwork-to-artwork palette change uses the artwork fade's duration and easing. On every animation frame, the launcher updates the text and icon contrast against that frame's fill. An interrupted transition starts from the displayed colour, and reduced motion applies the target palette immediately.

- **Depends on** the main window artwork and search hosts, `ArtworkFraming`, profile framing and custom artwork paths, `SteamCatalog` downloads, Avalonia bitmap APIs and SkiaSharp, `WindowsDesktop`, and the shared frosted menu and artwork toolbar components.
- **Used by** the main window, navigation, settings, game and collection cards, logo decoding, Steam refresh, and artwork smoke checks. The main window disposes images at shutdown, and artwork requests recheck cancellation and target state before applying results.

[Artwork source](../../../avalonia/LGLauncher.Desktop/Launcher.Artwork.cs) · [Application shell](app.md)

## `avalonia/LGLauncher.Desktop/ArtworkAccent.cs`

Scores neighbouring hues in the existing downsampled artwork, weighting visible coloured pixels and ignoring mostly transparent and low-chroma pixels. The window uses the same contrast helpers for artwork and custom accent fills. It chooses the higher-contrast black or white for text and icons on the fill and uses a brighter accent for text drawn directly on dark surfaces. [Contrast ratio](https://www.w3.org/WAI/WCAG22/Understanding/contrast-minimum.html)

[Accent source](../../../avalonia/LGLauncher.Desktop/ArtworkAccent.cs)

## `avalonia/LGLauncher.Desktop/Launcher.GameLogos.cs`

Shows the custom or Steam logo in place of the game title when enabled and available. It tries the custom file first, then the launcher cache and local Windows Steam cache before fetching. It uses a bounded cache for recently used Steam logos and retains an accessible title fallback. It discards decoded images for games that are no longer selected.

- **Depends on** the game's Steam ID and logo preference, `SteamCatalog`, the artwork bitmap decoder, the Windows Steam install location lookup, and `GameLogo` and `GameTitle` in the main window.
- **Used by** game detail rendering and window cleanup, `Launcher.SteamRefresh.cs` for refreshed logos, and `Launcher.Verification.GameMetadata.cs`.

[Game logo source](../../../avalonia/LGLauncher.Desktop/Launcher.GameLogos.cs)

## `avalonia/LGLauncher.Desktop/Launcher.GameMedia.cs`

Builds the Manage metadata dialog. It accepts local images for the backdrop, portrait, and logo and MP3, Ogg Vorbis, Opus, or WAV audio for theme music. It recommends 1920 × 1080 px for backdrops, 600 × 900 px for portraits, and a wide transparent PNG for logos, with 720 × 192 px as an example. Each field displays the saved original basename and can clear its override and restore its automatic fallback. Imports stage and validate media off the UI thread, save asset IDs and names together, and remove new copies if the operation is cancelled or fails. Backdrop changes reset artwork framing. Earlier overrides without a saved basename show “Custom file”. Previously saved assets remain available to older profile backups.

The game title sits at the right of the dialog heading and shows the full name on hover. Image recommendations are centred vertically beside their field labels when there is room on the same line.

- **Depends on** — `MediaFileField`, the modal and storage helpers, `LibraryStore`, the bitmap decoder, `ThemeAudioImport`, and `ThemeMusicPlayer` for audio validation.
- **Used by** — The game context menu and the Edit game footer. Desktop smoke exercises the nested modal and import transaction.

[Game media source](../../../avalonia/LGLauncher.Desktop/Launcher.GameMedia.cs)

## `avalonia/LGLauncher.Desktop/Launcher.ThemeMusic.cs`

Tracks the active game details and plays the profile's uploaded audio when Theme music is enabled. For a Steam-matched game without playable custom audio, it searches the local Steam libraries' `steamapps/music` folders after normalising and deduplicating the registered library paths. It plays a track only when exactly one MP3 or WAV filename matches a recognised theme name in an album named for the game. Steam supplies downloadable albums whose availability and filenames vary by game. [Steam soundtrack documentation](https://partner.steamgames.com/doc/store/application/soundtrackapp)

When the user switches games, the outgoing theme fades out immediately. The selected game must remain selected for one second before the launcher tries its custom track or searches for an installed Steam soundtrack. Choosing another game cancels the pending start or lookup. Music stops immediately when the window loses focus, leaves game details, minimises, closes, or launches a game, and when Theme music is turned off.

- **Depends on** the saved preference and media filename, the Windows Steam installation path, and `ThemeMusicPlayer`.
- **Used by** main-window rendering and lifetime events, the Library settings toggle, and the theme music smoke fixture.

[Theme music source](../../../avalonia/LGLauncher.Desktop/Launcher.ThemeMusic.cs)

## `avalonia/LGLauncher.Desktop/ThemeAudioImport.cs`

Chooses the profile extension for theme uploads and decodes uploaded mono or stereo Ogg Vorbis and Opus tracks to 16-bit WAV once at import. Opus conversion applies the header gain and trims codec padding. The decoder rejects oversized decoded tracks and checks cancellation between reads. The metadata transaction copies MP3 and WAV uploads and validates playable output for all formats before saving.

- **Depends on** — `NVorbis`, `Concentus.Oggfile`, and .NET binary I/O.
- **Used by** — The metadata audio field.

[Audio importer](../../../avalonia/LGLauncher.Desktop/ThemeAudioImport.cs)

## `avalonia/LGLauncher.Desktop/ThemeMusicPlayer.cs`

Uses Windows Media Foundation to open local MP3 and WAV audio. Playback starts at zero and fades to 30% of the player's volume over 350 milliseconds. A game switch fades the current volume to zero over 350 milliseconds and releases the player. A loop keeps the current volume and any fade already in progress. Media import uses a separate, non-playing instance to check that Windows can open the file before saving it. [Windows Media Foundation playback](https://learn.microsoft.com/en-us/windows/win32/medfound/getting-started-with-mfplay)

- **Depends on** — `mfplay.dll` and Avalonia's UI dispatcher. Playback notifications return to the UI thread before touching the current player.
- **Used by** — The theme music window lifecycle and metadata audio validation.

[Windows player source](../../../avalonia/LGLauncher.Desktop/ThemeMusicPlayer.cs)

## `avalonia/LGLauncher.Desktop/Launcher.Genres.cs`

Coordinates automatic Steam genre backfill while preserving manual genres, parses commas in genre entry fields, handles title and genre searches, and builds the clickable genre chips on game details. An unsuccessful save restores the profile fields in memory.

- **Depends on** the `Game` genre fields and completeness marker, `SteamCatalog.GetGenres`, the background fetch gates and save methods, and named genre and search controls.
- **Used by** the Steam metadata queue, the game editor and manual Steam refresh, library search, game detail rendering, and metadata verification.

[Genre source](../../../avalonia/LGLauncher.Desktop/Launcher.Genres.cs) · [Search](library.md)

## `avalonia/LGLauncher.Desktop/Launcher.SteamTags.cs`

Starts the genre and tag queues together, prioritises the selected game's missing tags, and persists tags against their Steam App ID. Tag changes trigger updates when dynamic collection membership changes. The display preference hides the detail labels while saved tags remain available for collection rules.

- **Depends on** `SteamCatalog.GetStoreTags`, the profile tag fields and rule evaluation, the genre queue, launcher save/cancellation state, and the `StoreTags` layout host.
- **Used by** the main window after startup or a game session, Edit game after saving, manual Steam refresh, game detail rendering, and the metadata smoke tests.

[Store tag source](../../../avalonia/LGLauncher.Desktop/Launcher.SteamTags.cs) · [Collection rules](library.md)

## `avalonia/LGLauncher.Desktop/Launcher.SteamRefresh.cs`

Implements the explicit refresh action for a matched game. It fetches genre, tag, hero, header, portrait, icon, and logo data concurrently, then updates the corresponding caches and affected views. It guards duplicate refreshes for the same ID and leaves manual genres, artwork selection, and framing intact.

- **Depends on** `SteamCatalog`, the genre and tag save helpers, artwork and logo cache methods, and the current window, selection, and game session state.
- **Used by** the game details refresh button, the game context menu, and the metadata verification that checks action availability.

[Steam refresh source](../../../avalonia/LGLauncher.Desktop/Launcher.SteamRefresh.cs) · [Game dialogs](settings.md)
