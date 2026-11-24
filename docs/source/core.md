# Shared library and services

[Source map](README.md) · [Desktop](desktop/README.md)

The four files here are linked into the .NET 10 Desktop project. The Windows core fixtures compile `Library.cs`, `SteamCatalog.cs`, and `GameSessions.cs` with the same SDK. Artwork framing is exercised through the desktop checks.

## `src/Library.cs`

[Source](../../src/Library.cs)

- **Purpose** — Owns the saved format for games, collections, preferences, and artwork framing. `LibraryStore` reads and writes `library.json`, keeps a recovery backup, normalises older profiles, and resolves launcher-managed artwork paths. Optional fields let existing profiles default to hardware rendering, game artwork accents, the built-in purple palette, and enabled contribution reminders with zero recorded game returns. Desktop saves a custom default accent as `DefaultAccentColour` in `#RRGGBB` form and omits the value for the built-in palette. This file also holds discovery scanning and the debounced folder watcher. The collection evaluator supports legacy genre filters, newer genre/tag rules, and manual membership.
- **Depends on** — .NET Data Contracts and JSON serialization, filesystem and timer APIs. Artwork and metadata are recorded by ID and source, with the display layer responsible for loading them.
- **Used by** — The Avalonia launcher shell and its settings, collections, RSS subscriptions, and artwork code. `GameCollection.IconName` optionally stores a Lucide name for a sidebar override. `ArtworkFraming.cs` uses `ArtworkPosition` bounds. Core and migration fixtures exercise serialization, rule matching, and discovery.

Profiles with unreadable JSON or null game, collection, or RSS subscription records fall back to the last readable backup. The first successful save after recovery replaces the unusable primary while preserving that backup. Subsequent saves resume backing up the previous primary.

The optional `UsePreviewUpdates` preference records the Next preview opt-in. Fresh and existing profiles default to Stable when the field is absent. The optional `TileColumns` and `TileMinimumWidth` preferences store the game tile column limit and width threshold. Desktop defaults to six columns and 120 px when those fields are absent.

Optional game properties store the added date, manual tags, scale, and Steam release date, developers, and publishers. Steam details are scoped to the ID that supplied them. Collection creation dates and older game addition dates can be unknown. Dynamic rules also accept Developer, Publisher, and Modded. The launcher populates Modded from installation records after loading a profile. Profiles can override the base colourway, widget radii and shadow strength, or disable playtime tracking. Optional flags hide the tile playtime caption or mod indicator, or enable friendly launch dates. Older profiles retain both captions and numeric dates by default.

Desktop saves backdrop, portrait, and logo overrides as separate GUIDs in the current profile. `CustomArtworkPath` resolves them under `artwork/custom`. The optional `ThemeMusicFile` is a GUID plus a supported extension. `ThemeMusicPath` validates the complete filename before resolving it under `music`. Optional original basenames are saved with each override for display in Manage metadata. Earlier overrides can have an asset ID without an original name. Existing profiles start with theme music off.

## `src/SteamCatalog.cs`

[Source](../../src/SteamCatalog.cs)

- **Purpose** — Centralises unauthenticated Steam title matching, genre and tag lookup, and downloads/cache management for game icons, headers, portraits, backdrops, and logos. It validates and de-duplicates metadata and image URLs and bounds concurrent requests. Individual feature views can render from cache and offer an explicit refresh.
- **Depends on** — `HttpClient`, `System.Text.Json`, filesystem APIs, Steam Store, Community, and asset endpoints, and the Desktop artwork decode validator.
- **Used by** — Desktop’s search/matching, game editor, artwork, logos, tags, genres, and refresh partials. `tests/CoreTests.cs` checks local parser fixtures, matching, asset selection, and transactional artwork refresh with injected responses.

Steam images are staged beside the target cache entry and decoded before publication. The validator checks one staged image at a time. Unreadable candidates and canceled downloads leave the existing cache in place, and Steam can supply an alternative candidate when available. Display paths can retry unreadable cached files when fetching is available.

`GetGameDetails` fetches Steam's developer and publisher lists and parses complete English release dates for the matching app. Coming-soon and incomplete dates remain unset.

## `src/GameSessions.cs`

[Source](../../src/GameSessions.cs)

- **Purpose** — Separates executable launch and elapsed-time tracking from window logic. Tracks active processes and completed durations by game ID, exposes an end-of-session callback, and lets callers acknowledge durations after saving them.
- **Depends on** — .NET process, timing, and synchronisation APIs. The tracked process is the executable the user chose.
- **Used by** — Desktop `Launcher.axaml.cs` handles launch, pauses background work as appropriate, and persists playtime. `tests/CoreTests.cs` exercises a short-lived process, its completion event, and the High priority opt-in.

On Windows, Desktop can opt in to setting `ProcessPriorityClass.High` on the process started and tracked by `GameSessions.Launch`. The library stores the opt-in as `LauncherPreferences.SetProcessPriority`, with existing and fresh profiles defaulting to off. The priority change is best effort so short-lived processes and Windows access restrictions do not interrupt session tracking. High is the highest non-realtime Windows process priority class. [Windows scheduling priorities](https://learn.microsoft.com/en-us/windows/win32/procthread/scheduling-priorities)

Sessions started with tracking off still report process exit. Disabling tracking discards active and pending durations. The next launch uses the saved preference. Clearing playtime resets pending durations and restarts any active timers.

## `src/ArtworkFraming.cs`

[Source](../../src/ArtworkFraming.cs)

- **Purpose** — Keeps subject-aware framing and manual crop math in one place. It scores artwork detail by columns and rows, returns viewport-filling crop rectangles, and computes cursor-anchored zoom and drag positions. The positions are normalised, so framing travels with the saved library across window sizes.
- **Depends on** — `ArtworkPosition` in `Library.cs` and Avalonia geometry types.
- **Used by** — Desktop `Launcher.Artwork.cs`. Avalonia desktop smoke checks exercise saved framing, crop and zoom.
