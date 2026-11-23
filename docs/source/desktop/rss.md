# RSS and Atom

[Desktop source index](README.md)

## `avalonia/LGLauncher.Desktop/Launcher.Rss.cs`

Owns feed settings, subscription persistence, refresh scheduling, and the RSS, article, and Home views. It refreshes enabled feeds with bounded concurrency, reconciles removed feeds and offline snapshots, and creates native text and image controls. About release notes reuse `BuildRssParagraph` for selectable text. The Home shelf refreshes in place when it can preserve the existing cards.

The Home shelf header links to RSS feed settings and refreshes all enabled feeds through the shared scheduler. Its empty state distinguishes a library with no subscriptions from one with no articles. Refresh is disabled during a check and when all feeds are disabled or absent.

- **Depends on** the profile's `RssSubscription` and refresh preferences, `RssReader`, `RssMarkup` block types, `RssArticleImage`, the launcher background work gates, and the common field, menu, and Home shelf helpers.
- **Used by** the main window renderer and startup or game session handlers, `Launcher.Home.cs`, navigation history, and `Launcher.Rss.Verification.cs`.

[RSS view source](../../../avalonia/LGLauncher.Desktop/Launcher.Rss.cs) · [Home and navigation](library.md)

## `avalonia/LGLauncher.Desktop/RssReader.cs`

Imports local feeds or OPML outlines, reads RSS and Atom from files or HTTP, and exposes feed, snapshot, and article records. It bounds feed size, prohibits DTDs, resolves HTTP(S) links and image enclosures, and caches snapshots by subscription ID. Conditional remote requests reuse valid offline content when available.

- **Depends on** the profile's `RssSubscription`, .NET HTTP, XML, and JSON APIs, the profile cache directory, and `RssMarkup` for article body conversion.
- **Used by** the RSS settings and refresh controllers, the article view's web address checks, `RssMarkup` and `RssArticleImage` for URL validation, and RSS smoke fixtures.

[Feed reader source](../../../avalonia/LGLauncher.Desktop/RssReader.cs)

## `avalonia/LGLauncher.Desktop/RssMarkup.cs`

Converts feed body markup into bounded native paragraphs, styled runs, headings, lists, quotes, code, and image records. It skips non-content sections, resolves eligible image URLs, and produces plain text for fallback display and cached snapshots. `ReadMarkdown` converts GitHub release bodies with Markdig and passes the result through the same bounded block parser. About displays the text blocks and links to the release page for supplementary content.

- **Depends on** XML nodes, .NET text processing and HTML entity decoding, Markdig for Markdown conversion, and `RssReader.ResolveWebAddress` for relative image addresses.
- **Used by** `RssReader` when parsing entries, `Launcher.Rss.cs` when creating article controls, `Launcher.ReleaseNotes.cs` for release notes, and verification of markup handling and size limits.

[Article markup source](../../../avalonia/LGLauncher.Desktop/RssMarkup.cs)

## `avalonia/LGLauncher.Desktop/RssArticleImage.cs`

Displays an inline article image only when its placeholder is inside or near the viewport. It limits concurrent requests, response size and decoded dimensions, and cancels work or disposes the bitmap when the control detaches.

- **Depends on** Avalonia viewport events and bitmap controls, SkiaSharp decoding, .NET HTTP and cancellation, and web address checking from `RssReader`.
- **Used by** the article renderer in `Launcher.Rss.cs` and the image-viewport smoke check. The check uses an injected HTTP fixture to exercise redirect handling without a live feed.

[RSS image source](../../../avalonia/LGLauncher.Desktop/RssArticleImage.cs) · [Desktop verification](verification.md)
