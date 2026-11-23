# Application shell and startup

[Desktop source index](README.md)

## `avalonia/LGLauncher.Desktop/LGLauncher.Desktop.csproj`

Defines the desktop executable, assembly version, linked core sources, and embedded resources. It embeds the OptiScaler catalog, references the 7z reader and the Markdig release-notes parser, and marks single-file publishes as eligible for the in-app updater. The Setup upgrade smoke harness uses `SetupFixtureAppId` to build disposable bundles for its unique per-user registration.

The project embeds its MIT license as `LGLauncher.LICENSE.txt`, so the portable executable and the executable installed by Windows Setup carry the notice. [Project license](../../../LICENSE)

- **Depends on** `avalonia/Directory.Build.props` for target framework and Avalonia version, the shared component project, Avalonia Desktop and Fluent packages, `Library.cs`, `GameSessions.cs`, `SteamCatalog.cs`, `ArtworkFraming.cs`, the app manifest, and the icon and splash artwork.
- **Used by** the root build script, desktop executable and smoke checks, Windows installer packaging, and update code that reads the bundle marker and assembly version.

[Project file](../../../avalonia/LGLauncher.Desktop/LGLauncher.Desktop.csproj)

Desktop directly references SkiaSharp for image decoding. Its managed package and explicit Linux and Windows native references share `SkiaSharpVersion` so Dependabot can propose the update together. The managed package also resolves its matching macOS native dependency. [Dependency maintenance](../build-and-tests.md#githubdependabotyml)

### Windows x64 Expat assessment

The November 2026 Windows x64 build resolves SkiaSharp and its Windows native assets at `3.119.4` through Avalonia `12.1.6`. The extracted single-file `libSkiaSharp.dll` matches the NuGet asset by SHA-256. Its matching debug symbols record Expat `2.7.5` tokenizer code, with no executable section contributions from Expat's `xmlparse.c` or Skia's `SkXMLParser.cpp`. [SkiaSharp 3.119.4 component manifest](https://github.com/mono/SkiaSharp/blob/v3.119.4/cgmanifest.json)

The affected Expat parsing path is unreachable in this build. RSS and OPML use .NET's `XmlReader`, with DTD processing prohibited and the resolver disabled. Imported artwork, Steam artwork and RSS article images use Skia's bitmap codecs. The Windows DLL's JPEG gain-map hook returns false without parsing XMP, and the pinned DNG SDK build disables XMP. Ordinary JPEG and DNG fixtures successfully exercised native pixel decoding in the matching DLL. [Steam and artwork](steam.md) · [RSS](rss.md) · [Pinned JPEG codec](https://github.com/mono/skia/blob/7dbfc07dd33181f84e0958afb7ee805c6c769f0b/src/codec/SkJpegCodec.cpp) · [Pinned DNG build](https://github.com/mono/skia/blob/7dbfc07dd33181f84e0958afb7ee805c6c769f0b/third_party/dng_sdk/BUILD.gn)

The latest Windows stable package checked, `4.157.0`, and the newest release candidate checked, `4.159.0-rc.1.26567.13`, both pin Expat `2.8.1`. That version fixes CVE-2026-45186. The related CVE-2026-66046 and CVE-2026-76641 were fixed in `2.8.4`. The November review closed the Windows x64 exposure finding and left the upstream dependency upgrade pending. Recheck the native asset when changing SkiaSharp or adding a Skia-backed XML or image input path. Use the upstream component manifest and Skia source pin alongside the NuGet advisory audit, which covers package identities. [Stable manifest](https://github.com/mono/SkiaSharp/blob/v4.157.0/cgmanifest.json) · [Release candidate manifest](https://github.com/mono/SkiaSharp/blob/v4.159.0-rc.1.26567.13/cgmanifest.json) · [Expat change history](https://github.com/libexpat/libexpat/blob/master/expat/Changes)

## `avalonia/LGLauncher.Desktop/Markdig.LICENSE.txt`

Retains the upstream notice for Markdig. The Desktop project embeds it alongside the markup parser dependency used for [About release notes](settings.md).

[Markdig license](../../../avalonia/LGLauncher.Desktop/Markdig.LICENSE.txt)

## `avalonia/LGLauncher.Desktop/ThemeAudio.LICENSE.txt`

Bundles the upstream license notices for NVorbis, Concentus.Oggfile, and Concentus in the portable launcher. The Desktop project includes these dependencies for Ogg Vorbis and Opus imports.

[Audio decoder licenses](../../../avalonia/LGLauncher.Desktop/ThemeAudio.LICENSE.txt)

## `avalonia/LGLauncher.Desktop/Program.cs`

Provides the desktop process entry point. It separates a normal single-instance launch from early update worker, restart, and verification arguments, including the mod fixture runner. The worker and restart modes retain whether the package is Windows Setup or a portable executable so they can find the correct staging directory. Both Setup and portable launches resolve the running account's Local Application Data known folder and append `LG Launcher`. An existing Windows instance receives an activation signal. Smoke modes, including `--verify-setup`, take an explicit fixture directory and preview path.

The entry point loads the profile before platform initialization and passes it to the window. Windows uses Avalonia's default Direct3D 11 renderer through ANGLE with software fallback. If the saved preference disables hardware rendering, startup selects the software renderer and redirection-surface composition. [Avalonia Windows rendering modes](https://docs.avaloniaui.net/docs/platform-specific-guides/windows#rendering-mode)

The renderer restart launches a replacement process that waits for the single-instance mutex before opening the profile. A normal second launch still asks the existing instance to come forward immediately.

- **Depends on** Avalonia's desktop builder and Win32 platform options, `LibraryStore`, the platform profile directory, the Windows named mutex and activation event, `App`, `UpdateInstaller`, and `UpdateInstallVerification`.
- **Used by** the generated executable entry point, `App.axaml.cs` for profile and preview settings, `Launcher.axaml.cs` for update restart acknowledgement, and the PowerShell verification scripts for the isolated launch modes.

[Program source](../../../avalonia/LGLauncher.Desktop/Program.cs) · [Updates](updates.md) · [Verification](verification.md)

## `avalonia/LGLauncher.Desktop/App.axaml`

Assembles the application's dark Fluent theme and shared LG Launcher styles. It also makes the window decoration template available to the compositor-frame bridge.

- **Depends on** the Avalonia Fluent theme, the `WindowDrawnDecorations` template contract, and the shared component stylesheet.
- **Used by** `App.axaml.cs` when loading the application and by the launcher and splash at runtime. `WindowsDesktop.UseCompositorFrame` uses the decoration resource by key.

[Application markup](../../../avalonia/LGLauncher.Desktop/App.axaml)

## `avalonia/LGLauncher.Desktop/App.axaml.cs`

Owns the application lifetime so the splash can hand off to the launcher after startup work. It connects the second-instance activation signal, schedules migration verification when requested, and gives recoverable profile-load failures an error window.

- **Depends on** `App.axaml`, Avalonia's desktop lifetime and dispatcher, `Program` startup state, `LibraryStore`, `Launcher`, and `StartupWindow`.
- **Used by** `Program.Run` for real and test launches. The migration harness depends on its preview and error-report hooks and exit status.

[Application lifetime source](../../../avalonia/LGLauncher.Desktop/App.axaml.cs) · [Verification](verification.md)

## `avalonia/LGLauncher.Desktop/Launcher.axaml`

Declares the main-window visual tree shared by the feature partials. It owns the sidebar and navigation buttons, title/status chrome, game detail and artwork hosts, page heading with the Mods request action and scope note, page body and scroll viewer, search results panel, and modal host. The sidebar uses the original 32-dip game-row height for game and collection rows and buttons. Both sidebar lists can scroll when space runs out. The window styles tie frosted surfaces to the artwork state. The search panel uses the shared search frame, result row, and height transition styles.

On game details, a matched Steam App ID appears beside Playtime and Last Launched in the wrapping stats row. `RenderDetails` hides the whole item for unmatched games.

The collection list wires realized rows to the shared context menu so users can open the icon picker directly from the sidebar.

- **Depends on** Avalonia window, chrome, and layout types, the application theme tokens and shared controls, and the generated `Launcher` partial.
- **Used by** `Launcher.axaml.cs` and the other `Launcher.*.cs` files that render or handle events on named controls. Desktop visual tests also resolve these controls by name.

[Main window markup](../../../avalonia/LGLauncher.Desktop/Launcher.axaml) · [Library](library.md) · [Preferences](settings.md)

## `avalonia/LGLauncher.Desktop/Launcher.axaml.cs`

Coordinates the main window. Its constructor wires the shared shell and initializes services from the loaded profile. The window title displays the installed stable or Next preview tag. It owns startup and disposal hooks. When the usage notice is pending, startup runs the first-usage prompts before allowing automatic installation. The render dispatcher selects page content, updates the sidebar lists and their selections, builds game cards and collection member grids, and updates Discoveries and game details. It also starts folder watches, scans local executables, matches games to Steam, and records play sessions.

When the last tracked game exits, `EndSession` saves the game-return count with playtime and uses the normal tray restore path to show and activate the launcher. It then checks whether a contribution reminder is due.

When the window opens minimised after an update, it acknowledges the restart before awaiting library discovery. It shows the update result once the launcher is ready to display status.

`Launch` passes the profile's process priority opt-in to `GameSessions` for the selected executable.

`SortCollection` captures each visible page member's position by game before rebuilding the sorted grid. A wrapper applies the resulting translation so `GameCard` keeps its own hover feedback. New grid members appear in place, and changing pages cancels the motion when the old tile hosts detach.

- **Depends on** the named controls in `Launcher.axaml`, `LibraryStore`, `Discovery`, `LibraryWatcher`, `GameSessions`, `SteamCatalog`, the shared UI components, and the feature partials.
- **Used by** `App` when creating the window, the modal and navigation code for shared rendering and persistence, the artwork and Steam partials for session and cancellation state, and the desktop verification entry point.

[Main window source](../../../avalonia/LGLauncher.Desktop/Launcher.axaml.cs) · [Library](library.md) · [Steam and artwork](steam.md)

## `avalonia/LGLauncher.Desktop/Launcher.Startup.cs`

Coordinates the initial library scan, update check, and splash progress. The splash progress indicator uses the profile's default accent. Startup honours the usage acknowledgement before scanning and allows slow startup tasks to finish after the splash closes. About’s version and update group uses the shared animated picker for **Stable** and **Next preview**. Changing the channel refreshes the check and its release notes. About offers **Install update** for a verified package on a per-user installation and the release page for an earlier machine-wide installation.

- **Depends on** the profile and background cancellation state, `StartupWindow`, `WindowsDesktop`, the main-window watcher and scan methods, `UpdateFeed`, and update action methods.
- **Used by** `App` on splash startup, `Launcher.axaml.cs` for the path that opens without a splash and after the usage notice, and `Launcher.Settings.cs` for About.

[Startup source](../../../avalonia/LGLauncher.Desktop/Launcher.Startup.cs) · [Updates](updates.md) · [Preferences](settings.md)

## `avalonia/LGLauncher.Desktop/StartupWindow.axaml`

Defines the splash window's artwork, contrast gradients, operation text, and progress bar. The splash owns a small independent visual surface so startup can report progress before the main window opens.

- **Depends on** Avalonia window and panel controls, theme brush and font tokens, the shared `lg-progress` style, and the paired `StartupWindow` code-behind.
- **Used by** `StartupWindow.axaml.cs` to display the embedded artwork and update progress, and by `Launcher.Verification.cs` for splash layout checks.

[Splash markup](../../../avalonia/LGLauncher.Desktop/StartupWindow.axaml)

## `avalonia/LGLauncher.Desktop/StartupWindow.axaml.cs`

Loads the splash artwork and updates the operation label and progress bar. It applies the chosen font, optional progress motion, and native window frame support, and disposes the artwork bitmap on close.

- **Depends on** `StartupWindow.axaml`, the embedded `LGLauncher.Welcome.jpg` resource, Avalonia imaging, the shared motion styles, and `WindowsDesktop`.
- **Used by** `Launcher.Startup.cs` to construct and update the splash, `App.axaml.cs` for its lifetime, and the splash verification checks.

[Splash source](../../../avalonia/LGLauncher.Desktop/StartupWindow.axaml.cs)

## `avalonia/LGLauncher.Desktop/WindowsDesktop.cs`

Keeps Windows platform work outside the page code. It manages the startup registry entry, native window frame and animation settings, non-client pointer tracking, and executable icon extraction with Win32 resource cleanup.

- **Depends on** Avalonia's window, compositor, and imaging APIs, `LG.CustomWindowDecorations` from `App.axaml`, the current-user registry, and the Win32/DWM/GDI/Shell interop APIs.
- **Used by** the main and splash windows for chrome, `Launcher.Behaviour.cs` for the startup registration, `Launcher.Settings.cs` for motion preferences, `Launcher.Artwork.cs` for pointer tracking and icon fallback, and the smoke tests for native captions.

[Windows platform bridge](../../../avalonia/LGLauncher.Desktop/WindowsDesktop.cs)
