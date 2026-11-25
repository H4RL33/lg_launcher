# Web workspace

[Desktop source index](README.md) · [UI catalog](../../ui/README.md)

The Web icon in the sidebar opens a workspace with Browser, Downloads, and Files in a symbolic rail on the right. **Home → Library** is the full game library. The sidebar game and collection lists still open their respective details.

## `avalonia/LGLauncher.Desktop/Launcher.Web.cs`

Builds the three Web sections on demand and keeps each attached when the user switches sections or navigates through the launcher. Its separate height-constrained host leaves scrolling to the active Web section. Switching tools uses the launcher’s view animation, with the browser animation limited to its chrome so the native page stays in place. Rows in Downloads and Files share the same hover style and accent tint. The tint marks a Files selection or a row with its context menu open. **Open in Files** and **Open target folder** can pass a file or folder directly to the explorer. Mouse Back returns to Downloads and Forward reopens that destination. Web data lives within the active profile's `web` directory. Browser downloads go to the launcher downloads directory, which defaults to `web/Downloads` under the profile. It also controls the Downloads shortcut. Each BitTorrent transfer uses the folder chosen when it is added.

[Web workspace source](../../../avalonia/LGLauncher.Desktop/Launcher.Web.cs) · [Application shell](app.md)

## `avalonia/LGLauncher.Desktop/Launcher.Web.Browser.cs`

Hosts Avalonia NativeWebView with the Windows Evergreen WebView2 runtime, which supplies page rendering and native navigation history. The square, full-height address field sits above square tabs that span the width of their strip and scroll as they overflow. The **New tab** icon is in the address bar and each tab includes its own close icon. Browser settings can hide the tab strip when only one tab is open. The bookmark icon opens the saved list and its **Add current page** action. A blank tab needs no browser process. The first address opens WebView2 for that tab and subsequent tabs share the same runtime profile under `web/Runtime`. The browser permits up to twelve open tabs. The toolbar arrows and mouse Back and Forward buttons use the active tab’s native navigation history. The launcher blocks automatic pop-ups by default, opens allowed HTTP and HTTPS pop-ups in tabs, and routes user-requested Magnet links to the launcher confirmation. A configured homepage opens in new tabs. When a search engine is configured, the address bar also accepts search terms. The native WebView2 status popup is disabled. The launcher displays the hovered link in an owned, click-through popup over the bottom edge of the native page and closes it when the page is hidden. The page fills the viewport with or without a preview. Choosing **Always open** and adding a download remembers the confirmation preference for the current profile. Every Magnet link still opens the download folder picker. **Settings → App Behaviour → Miscellaneous** can restore the prompt. Ordinary web content has no access to launcher host objects or web-message handlers. The browser blocks other top-level external URI schemes and hides the native surface behind launcher modals.

Browser downloads use WebView2's native transfer operation, including the original page session. They continue when changing sections or closing their tab. The launcher retains a closed tab's controller until its downloads terminate, then removes it.

[Browser source](../../../avalonia/LGLauncher.Desktop/Launcher.Web.Browser.cs) · [Avalonia WebView](https://docs.avaloniaui.net/docs/app-development/embedding-web-content) · [WebView2 Runtime](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution)

## `avalonia/LGLauncher.Desktop/WebContentBlocking.cs`

Enables extensions in the shared Windows WebView2 environment and installs bundled uBlock Origin Lite before the first page loads. It extracts the pinned archive once under the profile’s `web/uBO-Lite/<version>` directory, reuses the same profile installation across launches, and removes registrations for older bundled versions when a replacement is installed. WebView2 applies the extension’s default rules for ads and trackers. The Browser settings switch enables or disables the installation in the running profile. If WebView2 cannot enable the blocker, the browser displays its status and keeps browsing available.

[Content blocking source](../../../avalonia/LGLauncher.Desktop/WebContentBlocking.cs) · [WebView2 extension API](https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/win32/icorewebview2profile7)

## `avalonia/LGLauncher.Desktop/uBOLite.edge.zip`

The official uBO Lite Edge archive is embedded so portable and Setup builds can install the extension offline. The bundled version is 2026.1124.1811 and includes its upstream license and notices. To update it, verify the replacement archive’s SHA-256 against the upstream release, update the version and license notice, and run Web verification.

[Bundled Edge archive](../../../avalonia/LGLauncher.Desktop/uBOLite.edge.zip) · [Upstream release](https://github.com/uBlockOrigin/uBOL-home/releases/tag/2026.1124.1811)

## `avalonia/LGLauncher.Desktop/WebBookmarks.cs`

Checks address schemes and normalizes addresses typed without a scheme to HTTPS. With a search provider selected, plain search terms use its URL template with an escaped query. The custom template must be an HTTP or HTTPS URL containing `%s`. Bookmarks store the title and absolute URL in `web/bookmarks.json`. Saving uses a temporary file alongside the previous file. Bookmarks survive restart and browser tabs are session-only.

[Address and bookmark source](../../../avalonia/LGLauncher.Desktop/WebBookmarks.cs)

## `avalonia/LGLauncher.Desktop/WebTransfers.cs`

Owns browser and BitTorrent transfer state for the current session. It starts MonoTorrent after the first accepted Magnet link. It opens magnets in metadata-only mode, validates their output paths, and starts payload download in the same MonoTorrent manager so it can reuse the peers it already discovered. The pinned package exposes metadata-only startup through an internal overload. Torrent payloads start in a visible, uniquely named `.part` folder directly in the chosen destination. The Files view shows that folder and protects incomplete data from being opened or changed. Browser downloads use a hidden staging directory in the configured download folder. Magnet metadata lookup times out after two minutes. Direct HTTPS `.torrent` links supply metadata and retain the publisher’s full tracker list and any web seeds. RSS also accepts HTTP descriptors. Both routes limit the response to 8 MiB and 20 seconds, validate HTTP(S) redirect addresses, and reject HTTPS downgrades. MonoTorrent defaults to IPv4 with DHT enabled and can bootstrap from multiple routers if some are unreachable. The user can set IPv4, IPv6, or both for peer connections and can limit a family to a specific local address. DHT is available when IPv4 is enabled. The queue uses MonoTorrent’s transfer rates and calculates session ratio from uploaded and downloaded payload, including across Stop and Start. Completed transfers are moved into their selected destinations without overwriting existing files. Multi-file torrents publish their staging folder by its verified Windows handle. The queue keeps each browser download’s starting destination when the preference changes. Stop keeps data in the active session, and Start resumes the browser operation or rechecks existing BitTorrent pieces. Remove keeps completed files in place and moves retained partial data under `.transfers/retained` in the selected destination. The explicit deletion option checks ownership of completed output and leaves changed or separately added files alone. The Stop ratio keeps completed torrents seeding from their published paths until the target is reached or the user chooses Stop. Fresh profiles and profiles without an explicit value use 1.0. A value of 0 stops on completion. Closing the launcher allows up to three seconds for the queue to stop. A later transfer session can remove abandoned staging data while skipping an active session. Completed files remain on disk and the next launch starts a new queue.

[Transfer engine source](../../../avalonia/LGLauncher.Desktop/WebTransfers.cs) · [MonoTorrent](https://github.com/alanmcgovern/monotorrent)

## `avalonia/LGLauncher.Desktop/WebDhtEngine.cs`

Keeps the latest nonempty IPv4 DHT routing nodes in the profile’s `web/dht-nodes.bin` on graceful shutdown. A fresh engine checks and reuses that cache alongside its bootstrap routers. A missing or invalid cache falls back to router discovery, and an empty session preserves the last usable snapshot.

[DHT source](../../../avalonia/LGLauncher.Desktop/WebDhtEngine.cs)

## `avalonia/LGLauncher.Desktop/Launcher.Web.Transfers.cs`

Builds the Downloads table beneath a full-width field for Magnet and HTTPS `.torrent` links. Rows show name, source, status, progress, size, download and upload speeds, ETA, and ratio. Progress is shown as a percentage or a dash when the size is unknown. The table scrolls horizontally in narrow windows. Column headers and values align left. Drag a column’s right header edge to resize it, or double-click the edge to fit its contents. Right-click anywhere across a row for the shared animated menu with Start, Stop, Remove, and Open target folder. Completed downloads also offer Open in Files. Invalid transfer submissions display a timed amber warning beneath the address bar. Remove uses the standard menu foreground. Remove offers a separate confirmation to delete files. Hover over the status for the full message or over the size for the received byte count. Browser progress and download speed are sampled on the UI thread while transfers are active.

[Downloads source](../../../avalonia/LGLauncher.Desktop/Launcher.Web.Transfers.cs)

## `avalonia/LGLauncher.Desktop/Launcher.Web.Files.cs`

Builds a scrollable table for the current directory with name, type, size, and local modification time. Its left-aligned columns can be resized by dragging their right header edges. Double-click an edge to fit the column, including names in virtualized rows. A full-width path bar holds symbolic actions for folder navigation, refresh, and opening the current selection. Downloads is available from Quick Access beside the table. Right-click a folder row to pin it. It enumerates on a background thread and ignores older results after the location changes. Folder navigation slides the listing in the direction of travel. Hidden files and extensions are shown by default. Hidden and cut items are dimmed, and folder icons use the active colourway’s yellow. Clicking empty table space, pressing Escape, opening a context menu, or leaving Files clears the ordinary selection. A context menu highlights its own row until it closes. Open in Files from a transfer still selects the requested file. The mouse Back and Forward buttons step through up to 64 destinations in the current session, including paths opened through Quick Access and Downloads. Incomplete staging files are unavailable for opening. The Open or Run action uses the file’s parent as the working directory. Executables and other recognised active file types prompt for confirmation. Batch scripts use a cog and run via Windows Command Prompt with the validated path passed in the child process environment. Files launched this way are independent of game-library registration and playtime tracking.

[Files source](../../../avalonia/LGLauncher.Desktop/Launcher.Web.Files.cs)

## `avalonia/LGLauncher.Desktop/Launcher.Web.Files.Operations.cs`

Adds the animated item menu for Rename, Copy, Cut, Delete, and Run for executables, plus Quick Access actions for folders. Copy and Cut use the launcher’s file clipboard. In the listing background, Paste, New Directory, and Go Back act on the current folder. Delete uses the Windows Recycle Bin. Holding Shift changes the action to red Destroy and opens a confirmation before permanently deleting the item. File operations validate the source and destination and protect in-progress downloads. The folder picker has its own read-only navigation.

[File operation source](../../../avalonia/LGLauncher.Desktop/Launcher.Web.Files.Operations.cs)

## `avalonia/LGLauncher.Desktop/Launcher.Web.Files.Picker.cs`

Uses the same Files view in a folder-only modal when adding a Magnet link or HTTPS `.torrent` URL. It starts in the configured Downloads directory, falls back to the profile default when that directory is unavailable, and exposes the path bar, parent navigation, Quick Access, and drive roots. File Viewer settings reuse it to change the default. The titleless dialog sets its height when it opens. Its footer shows the current folder in a read-only path field, followed by **Cancel** and **Use this folder**. The confirm action becomes available after the folder loads. Cancellation leaves the transfer queue unchanged. The picker keeps its folder state separate from Files history.

[Folder picker source](../../../avalonia/LGLauncher.Desktop/Launcher.Web.Files.Picker.cs)

## `avalonia/LGLauncher.Desktop/Launcher.Web.Files.QuickAccess.cs`

Shows a fixed Downloads shortcut alongside user-pinned folders in the Files sidebar. Folder and shortcut menus use the launcher’s shared animated context menu. The launcher resolves a folder before pinning it and saves shortcuts in `web/quick-access.json`. It loads the pins independently of the game library.

[Quick Access source](../../../avalonia/LGLauncher.Desktop/Launcher.Web.Files.QuickAccess.cs)

## `avalonia/LGLauncher.Desktop/Web.LICENSE.txt`

Carries the license notices for Avalonia Controls WebView, the WebView2 SDK, MonoTorrent, and bundled uBO Lite. The extension archive also includes its full license and third-party notices. The desktop project embeds the file in both portable and Setup builds.

[Web dependency notices](../../../avalonia/LGLauncher.Desktop/Web.LICENSE.txt)

## `avalonia/LGLauncher.Desktop/Launcher.Web.Verification.cs`

Uses a fresh launcher profile and a loopback server to check Web navigation, tab history, bookmark persistence, the Magnet confirmation, and the WebView2 download-to-Files handoff. It checks the symbolic rail, address bar placement, tab-strip width and overflow, the three settings pages, live ad blocking, automatic pop-ups, the homepage, and custom address-bar searches. It exercises animated transfer and folder menus, the timed warning, the two removal choices, a live browser stop and resume, Quick Access pinning and navigation, changing the download folder, batch confirmation, file menu operations, and column drag resize and auto-fit. A queued torrent keeps its visible and protected partial directory while the fixture switches views. The fixture checks that the browser link popup leaves the native viewport at full height. It sends mouse Back and Forward to the launcher and the native renderer, checks in-session Files history, and follows a transfer context menu’s target folder back to Downloads and forward again. An offline browser fixture checks that uBO Lite blocks a request and replaces another with its local script. It captures the workspace at 730, 1230, and 1600 dips, with separate native page captures because an Avalonia bitmap omits the native child window.

[Web fixture source](../../../avalonia/LGLauncher.Desktop/Launcher.Web.Verification.cs) · [Build and test guide](../build-and-tests.md)
