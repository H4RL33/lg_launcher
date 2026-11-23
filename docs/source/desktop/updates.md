# Official updates

[Desktop source index](README.md)

The portable launcher can download its official executable and replace the running copy. Per-user Windows Setup installs download the official Setup package, apply it in place, and relaunch without requesting elevation. Setup updates the current account's Start menu shortcut and uninstall registration. Existing machine-wide copies open the release page for the one-time migration to per-user Setup. [Windows Setup verification](../build-and-tests.md#installerlglauncheriss)

## `avalonia/LGLauncher.Desktop/UpdateFeed.cs`

Checks GitHub's latest release endpoint, requires a stable `vX.Y.Z` tag, and compares normalized versions. It exposes separate packages for portable and Setup installs. Each must have the expected official URL, bounded size, uploaded state, and SHA-256 digest. The Setup asset name includes the exact release version. A hotfix uses the next version so the installed launcher can discover it. [Stable release procedure](../build-and-tests.md#stable-windows-releases)

About makes a separate request using the app's exact `vX.Y.Z` tag and accepts only a matching public stable release with a bounded Markdown body. It uses the accepted newer release's version and Markdown from the existing latest-release response for the available notes. The same HTTP client limits response size and time for both requests. [GitHub release-by-tag endpoint](https://docs.github.com/en/rest/releases/releases#get-a-release-by-tag-name)

- **Depends on** the official LG Launcher GitHub release metadata, .NET HTTP and JSON APIs, and the desktop assembly version.
- **Used by** startup, the manual About check and release notes, the main window version label, the installer transaction, and smoke tests with injected feed data.

[Update feed source](../../../avalonia/LGLauncher.Desktop/UpdateFeed.cs) · [Startup and shell](app.md)

## `avalonia/LGLauncher.Desktop/Launcher.Updates.cs`

Connects the feed result to the status bar and About actions, and selects the package for the running installation. Per-user Setup and portable builds support manual updates and opt-in automatic installation at startup. Automatic installation is off by default and skips the launch immediately after an update attempt so a cancellation or failure can be retried manually. The updater controls progress, cancellation, application exit, and handoff state. It postpones restart during gameplay, editing, or mod file operations and requires pending profile saves to succeed. About offers the release page when the correct package is unavailable or the current copy is installed machine-wide.

- **Depends on** `UpdateFeed.Result`, `UpdateInstaller`, preferences and session state, the main shell and update controls, and the shared save and modal state.
- **Used by** the main window startup, close, and session-completion paths, the About update controls, settings changes that cancel automatic updates, and the modal dismissal callback.

[Update interaction source](../../../avalonia/LGLauncher.Desktop/Launcher.Updates.cs) · [Preferences and dialogs](settings.md)

## `avalonia/LGLauncher.Desktop/UpdateInstaller.cs`

Implements the Windows single-file update transaction. `LG-Launcher.install.ini` beside the executable identifies a Setup-managed copy, including custom installation directories. The `Scope=User` marker enables the Setup updater. Portable downloads stage beside the executable and Setup downloads stage in the current user's temporary directory. Both paths verify redirect hosts, version, length, and digest.

The helper validates the parent and staged package before the launcher exits. The portable helper replaces the executable under the launcher mutex and restores its backup if restart fails. The Setup helper waits for the launcher to exit, rechecks and holds the package open for reading, and runs the installer with a progress window. The installer keeps the previous per-user installation directory and updates its registration. The helper verifies the installed version, reopens the launcher with the result after success, cancellation, or failure, and the restarted launcher cleans up staging.

- **Depends on** `UpdateFeed.Package` and digest validation, the single-file build marker, Windows executable metadata, .NET file/process APIs, and the mutex and restart argument contract in `Program.cs`.
- **Used by** the launcher update controls, the early command-line worker and restart modes, and `UpdateInstallVerification.cs` for download, replacement, relaunch, and rollback coverage.

[Update transaction source](../../../avalonia/LGLauncher.Desktop/UpdateInstaller.cs) · [Desktop verification](verification.md)
