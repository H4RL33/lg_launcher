# Avalonia desktop application

[Source reference index](../README.md)

The Windows production build enters through `Program.Main`, then Avalonia's classic desktop lifetime constructs `App` and opens the splash or the main `Launcher` window. `Launcher` is one partial class. Its feature files share selection, profile, and window lifetime state, and coordinate through named controls and the methods listed in these articles.

| Chapter | Start here when changing |
| --- | --- |
| [Application shell and startup](app.md) | Project references, entry point, splash, window chrome, profile startup, game launch, discovery scanning, and the main view. |
| [Library views and navigation](library.md) | Home shelves, browsing or editing collections, history, and search. |
| [Game artwork and Steam metadata](steam.md) | Artwork framing, shared glass, icons, logo cache, genres, tags, or Steam refresh. |
| [Preferences and dialogs](settings.md) | Appearance, settings dependencies, app behaviour, edit game, and modal workflows. |
| [Managed mods](mods.md) | Package cache, OptiScaler compatibility, ReShade setup and upgrades, game-folder transactions, and verification. |
| [Web workspace](web.md) | Windows browser tabs, bookmarks, downloads, Magnet links, and local files. |
| [RSS and Atom](rss.md) | Subscriptions, imports, parsing, article views, caching, and images. |
| [Official updates](updates.md) | Feed validation, download, Windows executable replacement, or rollback. |
| [Desktop verification](verification.md) | Isolated profile fixtures, visual smoke tests, and the updater test worker. |

Keep the main-window constructor and rendering paths in mind when adding an asynchronous operation. The launcher cancels background work at shutdown, pauses automatic scans and metadata work during play, and resumes pending work when play ends. Use the surrounding methods to check that the originating game, view, or subscription still exists before applying a result.
