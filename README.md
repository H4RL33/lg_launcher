# LG Launcher

LG Launcher is an open-source desktop library for locally installed games. Download Windows Setup or the portable executable from the [latest stable release](https://github.com/H4RL33/lg_launcher/releases/latest). Both require 64-bit Windows and the x64 .NET 10 Runtime.

For code changes, start with the [developer documentation](docs/README.md), [coding style](docs/coding-style.md), and [UI catalog](docs/ui/README.md). Automated checks run through `pwsh -NoProfile -File ./lint.ps1`.

- **Home** shows recent RSS articles, recently played games, collections, and the full library. Use the shelf arrows to browse collections when they overflow. Select a portrait tile or a game in the **Library** sidebar to open its details and launch it.
- Game tiles prefer custom portrait artwork, then Steam’s portrait Library Capsule when available. A subdued black or white wrench marks games with launcher-managed mods and adapts to the artwork behind it. Library and Collection tiles show tracked playtime from one minute.
- The **Library** sidebar uses Steam icons for matched games when available, then icons embedded in the executable.
- Use **Add new** under **Library** to open the custom game form. Use **Add new** under **Collections** to choose a manual or dynamic collection. Click either sidebar section heading to collapse it.
- **Choose library folder** scans a folder and its subfolders, then watches for changes. Use **Items for review** in the status bar to open Discoveries.
- **Play** starts the selected executable, minimizes LG Launcher, and records the launch date and elapsed time. Playtime follows the executable you selected. If it hands off to another process, select the executable that stays running during play.
- **Collections** opens from the icon row at the top of the sidebar. Open a collection from its artwork mosaic or the sidebar list below **Library**. Dynamic collections get a sidebar icon based on their genre or Steam tag rules when one is available. Right-click a collection and choose **Change icon** to search the Lucide icon set, or choose **Reset to default** and save to restore the automatic icon. Use **Add to collection** from a game's context menu to change its manual collection memberships.
- The sidebar **Library** icon opens the full library in the same portrait grid as collection members. Sort games by title, playtime, last launched, added date, or release date. Sort collections by name, cumulative playtime, created date, or game count. Portraits move to their new positions when animations are enabled.
- Use the mouse Back and Forward buttons to revisit the last 64 views within the launcher. History restores the collection, article, or game you opened and your position in lists.
- Dynamic collections can use genre, Steam tag, developer, publisher, and Modded rules. The search box rotates examples for `genre:`, `tag:`, `developer:`, `publisher:`, `year:`, and `scale:`. Use one prefix per search. **Edit game** supports manual genres and tags, Steam tag import, and an Indie, AA, or AAA scale.
- **Open Folder** in a game's options menu opens the directory containing its executable.
- **Edit game** also lets you search Steam or enter an App ID. LG Launcher tries exact title matching on import and caches available artwork from Steam. A Steam account or API key is unnecessary. Open **Manage metadata** from Edit game or the game's context menu to import backdrop artwork, a portrait tile, a logo, or a theme in MP3, Ogg Vorbis, Opus, or WAV format. The dialog shows recommended image sizes and remembers imported filenames. Collection mosaics use custom portraits where available. Clear an imported asset to restore its automatic fallback.
- Game details prefer a Steam Library Hero or full-size screenshot. The store header remains available as a fallback. When artwork is present and translucency is enabled, it shows through the frosted title bar, sidebar, status line, and search surfaces. Matched games show their Steam App ID beside Playtime and Last Launched.
- Right-click the art on a game's details page to replace it, adjust its framing, or reset it. In **Move Artwork**, drag to pan or use the mouse wheel to zoom, then save the position and zoom for that game.
- **Settings → Appearance** controls the interface font, font size, scale, translucency, motion, and accent colours. Choose a preset or enter a hex value for the default accent. The optional artwork accent overrides it on Game Details when the game has a usable artwork colour. **Settings → App Behaviour** controls startup at Windows sign-in, starting minimised, closing to the tray, hiding the window during play, hardware rendering, and contribution reminders. It can also reset the first-usage prompts. When the last game launched by LG Launcher exits, the launcher saves playtime and brings the window back up.
- **Settings → Appearance → Theme** offers Classic with directional lighting and flat Adwaita, Catppuccin Mocha, Everforest, and Tokyo Night colourways. **Widget Customisation** sets shared corner radii and shadow strength.
- **Settings → Library → General** can turn off playtime recording and hide playtime statistics and sorts. Its trash button opens confirmation before clearing recorded durations. **Game Tiles** controls the mod indicator and playtime caption independently. **Game Details → Date format** offers numeric or friendly Last Launched dates, such as “Nov. 22”.
- **Settings → App Behaviour → Performance → Set process priority** is off by default. Turn it on to request High CPU priority for the executable when you launch a game on Windows.
- **Settings → RSS** adds and manages RSS and Atom URLs, local feed files, and OPML subscription lists. Open the RSS sidebar tab to read formatted articles with images or to refresh feeds manually. Enabled feeds check at startup and every 30 minutes by default. **Settings → Library** can show recent RSS articles on Home, with quick access to feed settings and refresh.
- Game details prefer a custom logo, then a Steam logo. Use **Settings → Library → Game Details** to switch between logos and text titles, hide Steam Store tags, or turn on **Theme music**. Music uses your imported track first. For Steam-matched games, LG Launcher can also use an installed Steam soundtrack with a recognisable main-theme file name. When you switch games, the current theme fades out and the new theme fades in after a one-second pause on the selection. **Refresh from Steam** retries metadata and artwork.
- **Settings → Mods** manages shared downloads and refreshes the compatibility catalogs. **Manage** and the game-detail wrench open the same Mods dialog, with installed mods first. The update icon caches an eligible shared release, and the installed mod’s manager previews updates for that game. A new release awaiting review links to its official page. Deleting a cached version leaves game files intact. RenoDX uses full add-on ReShade and downloads directly during the game’s setup or update. If no automatic download is available, the game’s Mods dialog accepts a local `.addon64` file.
- The startup splash starts the watched-folder rescan and checks the official releases for updates. **Settings → About → Version & Updates → Release channel** defaults to **Stable**. Choose **Next preview** to include prereleases built from the `next` branch. Switching back to Stable offers the next stable upgrade as soon as one is available. Per-user Windows Setup installs and portable copies can install verified packages in-app. **Settings → App Behaviour → Automatic updates** installs available updates at startup, and **About** offers a manual check and release notes. [Official LG Launcher releases](https://github.com/H4RL33/lg_launcher/releases)
- Use the status bar to manage the watched folder, open items for review, and see the total game count.

The anti-piracy notice appears the first time you open LG Launcher. The Windows Avalonia build opens the existing `%LOCALAPPDATA%\LG Launcher` profile, including the acknowledgment, game library, launch history, settings, cached artwork, and saved crop and zoom. Folder scans and automatic artwork lookups pause while a game is running.

## Install on Windows

Run `LG-Launcher-Setup-X.Y.Z.exe` from the official releases. Setup installs for the current Windows account without administrator approval. It defaults to `%LOCALAPPDATA%\Programs\LG Launcher` and adds **LG Launcher** to that account's Start menu. The x64 .NET 10 Runtime is required. Setup preserves the profile under `%LOCALAPPDATA%\LG Launcher` during installation and uninstall.

If you used the machine-wide Setup under Program Files, switch once by closing LG Launcher, uninstalling the machine-wide copy through **Windows Settings → Apps**, and running the new per-user Setup. The machine-wide uninstaller may ask for administrator approval and removes the shared shortcut for all accounts. Open the new Start menu shortcut to continue with your existing library. If you used **Start with System**, turn it off and back on in **App Behaviour** to register the new executable path.

## Build and verify

See the [Avalonia component guide](avalonia/README.md) to run the gallery and the [build and test guide](docs/source/build-and-tests.md) for the full verification matrix.

From this directory in PowerShell, run `.\build.ps1` with the .NET 10 SDK to publish the single-file Windows x64 executable to `build\LG Launcher.exe`. Intermediate files and test output go into `.build-work`.

Run `.\build.ps1 -Smoke` to check library compatibility, update installation and relaunch, and rollback before publishing. Run `.\build.ps1 -Test` for the Windows core fixtures using the .NET 10 SDK.

Follow the [stable Windows release procedure](docs/source/build-and-tests.md#stable-windows-releases) to stage the verified portable executable, checksum, and release notes. The workflow builds and verifies Windows Setup before publishing.

Build Windows Setup with `.\build-installer.ps1 -Launcher <verified-executable>`. Install Inno Setup 6 or provide its `ISCC.exe` with `-Compiler`. Add `-Smoke` to check the install path, per-user Start menu and Apps & Features entries, launch, update controls, and uninstall with an isolated installation and fixture profile.

## Contributing

Use GitHub issues for bug reports and feature requests, and open ordinary pull requests against `dev`. Run the relevant checks before submitting a change. [Development and release workflow](docs/workflow.md) · [Developer documentation](docs/README.md)

## License

LG Launcher is available under the MIT License. Third-party components retain their license notices. [Project license](LICENSE)
