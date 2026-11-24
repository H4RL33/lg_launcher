# Web workspace

[Desktop source index](README.md) · [UI catalog](../../ui/README.md)

The Web icon in the sidebar opens a workspace with Browser, Downloads, and Files in a symbolic rail on the right. **Home → Library** is the full game library. The sidebar game and collection lists still open their respective details.

## `avalonia/LGLauncher.Desktop/Launcher.Web.cs`

Builds the three Web sections on demand and keeps each attached when the user switches sections or navigates through the launcher. Its separate height-constrained host leaves scrolling to the active Web section. **Open in Files** can pass a file or folder directly to the explorer. Web data lives within the active profile's `web` directory, and completed downloads appear in `web/Downloads`.

[Web workspace source](../../../avalonia/LGLauncher.Desktop/Launcher.Web.cs) · [Application shell](app.md)

## `avalonia/LGLauncher.Desktop/Launcher.Web.Browser.cs`

Hosts Avalonia NativeWebView with the Windows Evergreen WebView2 runtime, which supplies page rendering and native navigation history. The square, full-height address field sits above square tabs that span the width of their strip and scroll as they overflow. The **New tab** icon is in the address bar and each tab includes its own close icon. The bookmark icon opens the saved list and its **Add current page** action. A blank tab needs no browser process. The first address opens WebView2 for that tab and subsequent tabs share the same runtime profile under `web/Runtime`. The browser permits up to twelve open tabs. It opens user-requested HTTP and HTTPS popups in a tab and routes user-requested Magnet links to the launcher confirmation. Ordinary web content has no access to launcher host objects or web-message handlers. The browser blocks other top-level external URI schemes and hides the native surface behind launcher modals.

Browser downloads use WebView2's native transfer operation, including the original page session. They continue when changing sections or closing their tab. The launcher retains a closed tab's controller until its downloads terminate, then removes it.

[Browser source](../../../avalonia/LGLauncher.Desktop/Launcher.Web.Browser.cs) · [Avalonia WebView](https://docs.avaloniaui.net/docs/app-development/embedding-web-content) · [WebView2 Runtime](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution)

## `avalonia/LGLauncher.Desktop/WebBookmarks.cs`

Checks address schemes and normalizes addresses typed without a scheme to HTTPS. Bookmarks store the title and absolute URL in `web/bookmarks.json`. Saving uses a temporary file alongside the previous file. Bookmarks survive restart and browser tabs are session-only.

[Address and bookmark source](../../../avalonia/LGLauncher.Desktop/WebBookmarks.cs)

## `avalonia/LGLauncher.Desktop/WebTransfers.cs`

Owns browser and BitTorrent transfer state for the current session. It starts MonoTorrent after the first accepted Magnet link. It fetches metadata, verifies destinations, and downloads to a unique staging subdirectory within the session under `Downloads/.transfers`. Metadata lookup times out after two minutes. Completed transfers are moved into Downloads without overwriting existing files. Browser downloads use the same staging and publication rules. Closing the launcher allows up to three seconds for the queue to stop. A later transfer session can remove abandoned staging data while skipping an active session. Completed files remain on disk and the next launch starts a new queue.

[Transfer engine source](../../../avalonia/LGLauncher.Desktop/WebTransfers.cs) · [MonoTorrent](https://github.com/alanmcgovern/monotorrent)

## `avalonia/LGLauncher.Desktop/Launcher.Web.Transfers.cs`

Builds the Downloads panel with a Magnet input, an active progress bar, Cancel, and **Open in Files** for completed output. Browser progress is read on the UI thread while transfers are active. Transfer errors are shown with their rows.

[Downloads source](../../../avalonia/LGLauncher.Desktop/Launcher.Web.Transfers.cs)

## `avalonia/LGLauncher.Desktop/Launcher.Web.Files.cs`

Builds a scrollable list for the current directory. It starts in Downloads and supports a typed path, Up, and activation of folder rows. It enumerates on a background thread and ignores older results after the location changes. Incomplete staging files are unavailable for opening. The Open or Run action uses the operating system shell with the file's parent as the working directory. Executables and other recognised active file types prompt for confirmation. Files launched this way are independent of game-library registration and playtime tracking.

[Files source](../../../avalonia/LGLauncher.Desktop/Launcher.Web.Files.cs)

## `avalonia/LGLauncher.Desktop/Web.LICENSE.txt`

Carries the license notices for Avalonia Controls WebView, the WebView2 SDK, and MonoTorrent. The desktop project embeds the file in both portable and Setup builds.

[Web dependency notices](../../../avalonia/LGLauncher.Desktop/Web.LICENSE.txt)

## `avalonia/LGLauncher.Desktop/Launcher.Web.Verification.cs`

Uses a fresh launcher profile and a loopback server to check Web navigation, tab history, bookmark persistence, the Magnet confirmation, and the WebView2 download-to-Files handoff. It checks the symbolic rail, address bar placement, and tab-strip width and overflow. It captures the workspace at 730, 1230, and 1600 dips, with separate native page captures because an Avalonia bitmap omits the native child window.

[Web fixture source](../../../avalonia/LGLauncher.Desktop/Launcher.Web.Verification.cs) · [Build and test guide](../build-and-tests.md)
