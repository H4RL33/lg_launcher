# Official updates

[Desktop source index](README.md)

The portable launcher can download its official executable and replace the running copy. Per-user Windows Setup installs download the official Setup package, apply it in place, and relaunch without requesting elevation. Setup updates the current account's Start menu shortcut and uninstall registration. Existing machine-wide copies open the release page for the one-time migration to per-user Setup. [Windows Setup verification](../build-and-tests.md#installerlglauncheriss)

## `avalonia/LGLauncher.Desktop/UpdateFeed.cs`

Stable uses GitHub’s latest-release endpoint and accepts stable `vX.Y.Z` tags. Next preview checks the 20 most recent published releases and selects the highest recognized stable or `vX.Y.Z-next.N` prerelease. Drafts, unrelated prerelease tags, and releases from before the version reset are ignored. A stable release supersedes any preview with the same three-part version. Both channels offer newer builds only. Switching a preview install to Stable waits until a stable upgrade is available.

The feed exposes separate packages for portable and Setup installs. Each must have the expected official URL, bounded size, uploaded state, and SHA-256 digest. The Setup asset name contains the Windows product version, including the fourth component for previews. [Stable release procedure](../build-and-tests.md#stable-windows-releases) · [Next preview releases](../build-and-tests.md#next-preview-windows-releases)

About requests the installed build’s exact stable or preview tag and accepts only a matching public release and bounded Markdown body. Available notes use the release selected by the ongoing update check. The HTTP client limits response size and time. [GitHub release-by-tag endpoint](https://docs.github.com/en/rest/releases/releases#get-a-release-by-tag-name)

- **Depends on** the official LG Launcher GitHub release metadata, `VerifiedDownload` for digest validation, .NET HTTP and JSON APIs, and the desktop assembly version.
- **Used by** startup, the manual About check and release notes, the main window version label, the installer transaction, and smoke tests with injected feed data.

[Update feed source](../../../avalonia/LGLauncher.Desktop/UpdateFeed.cs) · [Startup and shell](app.md)

## `avalonia/LGLauncher.Desktop/Launcher.Updates.cs`

Connects the feed result to the status bar and About actions, and selects the package for the running installation. Per-user Setup and portable builds support manual updates and opt-in automatic installation at startup. Automatic installation is off by default and skips the launch immediately after an update attempt so a cancellation or failure can be retried manually. The updater controls progress, cancellation, application exit, and handoff state. It postpones restart during gameplay, editing, or mod file operations and requires pending profile saves to succeed. About offers the release page when the correct package is unavailable or the current copy is installed machine-wide.

The release-channel picker saves the selection before clearing the old result and starting a new check. It cancels pending installation work before handoff and ignores results from superseded checks. Automatic installation runs from startup, including on the Next channel when the separate preference is enabled.

- **Depends on** `UpdateFeed.Result`, `UpdateInstaller`, preferences and session state, the main shell and update controls, and the shared save and modal state.
- **Used by** the main window startup, close, and session-completion paths, the About update controls, settings changes that cancel automatic updates, and the modal dismissal callback.

[Update interaction source](../../../avalonia/LGLauncher.Desktop/Launcher.Updates.cs) · [Preferences and dialogs](settings.md)

## `avalonia/LGLauncher.Desktop/UpdateInstaller.cs`

Implements the Windows single-file update transaction. `LG-Launcher.install.ini` beside the executable identifies a Setup-managed copy, including custom installation directories. The `Scope=User` marker enables the Setup updater. Before staging, during handoff, and before running Setup, the updater checks the executable's directory against the registered per-user installation. It passes that directory explicitly to Setup. Portable downloads stage beside the executable and Setup downloads stage in the current user's temporary directory. Both paths verify redirect hosts, version, length, and digest.

The worker uses the channel version order to reject older packages. It verifies preview binaries using the numeric four-part product version, and permits a same-base stable release to replace a preview. Setup also verifies the exact product version after installation.

The helper validates the parent and staged package before the launcher exits. A successful restart requires the replacement process to acknowledge readiness within 30 seconds. An opened minimised window acknowledges before waiting for library discovery. An early exit, including activation of an existing instance, counts as a failed restart. The portable helper replaces the executable under the launcher mutex. It stops its own unacknowledged replacement on timeout so it can restore and reopen the backup. If the executable is still locked, the helper keeps the backup in staging.

The Setup helper waits for the launcher to exit, rechecks and holds the package open for reading, and runs the installer with a progress window. It verifies the installed version and attempts to reopen the launcher after success, cancellation, or failure. Setup owns the installed files and registration. A restart timeout reports failure to the helper while allowing the installed launcher to finish opening. The restarted launcher cleans up staging and displays the result message when available.

- **Depends on** `UpdateFeed.Package`, `VerifiedDownload`, the single-file build marker, Windows executable metadata and per-user uninstall registration, .NET file/process APIs, and the mutex and restart argument contract in `Program.cs`.
- **Used by** the launcher update controls, the early command-line worker and restart modes, and `UpdateInstallVerification.cs` for download, replacement, relaunch, and rollback coverage.

[Update transaction source](../../../avalonia/LGLauncher.Desktop/UpdateInstaller.cs) · [Desktop verification](verification.md)

## `avalonia/LGLauncher.Desktop/VerifiedDownload.cs`

Streams HTTP response bodies into a caller-owned destination, checks the advertised and actual byte counts, and verifies SHA-256 before the caller can publish the result. It reports increasing whole percentages during the copy. Callers validate source URLs, handle timeouts and redirects, and decide how to stage and publish the validated data.

- **Depends on** .NET HTTP, stream, and cryptography APIs.
- **Used by** `UpdateFeed`, `UpdateInstaller`, and `ModPackageStore`. The update and mod fixtures exercise both download paths.

[Download verification source](../../../avalonia/LGLauncher.Desktop/VerifiedDownload.cs) · [Mod package cache](mods.md#avalonialglauncherdesktopmodsmodpackagescs)
