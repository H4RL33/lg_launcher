# Preferences and dialogs

[Desktop source index](README.md)

The Mods page uses the standard settings navigation row with a wrench and the shared section, action, tooltip, and dialog styles. The Available mods info buttons describe packages. RenoDX’s info button opens a dialog explaining where to find its game-specific add-ons. [Mods source article](mods.md)

## `avalonia/LGLauncher.Desktop/Launcher.Behaviour.cs`

Builds App Behaviour settings and the common settings navigation rows. Rows accept a theme icon resource or a prebuilt `SymbolIcon`, which lets the Mods list use the bundled Lucide catalog. It coordinates sign-in registration, tray and window restoration, and view animations. It also supplies icon and icon-button helpers used throughout the desktop shell.

Per-user Setup and portable installations support the **Automatic updates** opt-in. Earlier machine-wide installs show the switch disabled and retain the saved preference for the one-time move to per-user Setup.

Performance contains the Windows hardware rendering and process priority preferences. A saved hardware rendering change opens a restart prompt with Later and Restart now. Restart now requires games and busy operations to finish before handing off to a replacement process. **Set process priority** is off by default and takes effect on subsequent launches. It asks Windows for High CPU priority on the tracked game process.

Miscellaneous contains Reset first-usage prompts, which clears the usage acknowledgement and automatic-update opt-in in the same save. The running launcher updates the Automatic updates switch and cancels any automatic update download before handoff. The reset row uses the shared disabled preference style while both prompts are pending.

Miscellaneous also contains **Contribution reminders**, enabled by default. Turning it off suppresses the first and recurring prompts. The saved return count continues to advance, and reminders skipped while disabled are not queued.

The main window reuses `RestoreWindow` to restore and activate the launcher when the final tracked game exits.

- **Depends on** profile preferences and save state, `WindowsDesktop` startup registration, Avalonia tray/notification support, shared `ViewMotion` and `SymbolIcon`, and update state for automatic update cancellation.
- **Used by** the main window close and game launch paths, `App.axaml.cs` for instance activation, the navigation and artwork partials for animation, and other settings and feature pages for the common controls.

[App Behaviour source](../../../avalonia/LGLauncher.Desktop/Launcher.Behaviour.cs) · [Shell and startup](app.md) · [Updates](updates.md)

## `avalonia/LGLauncher.Desktop/Launcher.Settings.cs`

Renders Appearance, Library, and About. About places release notes for the installed version below the release-channel picker and update controls. The settings code applies font size, interface scale, translucency, and motion to the live shell. Reusable preference rows disable overridden choices, expose the reason, and keep the underlying preference available for restoration. For example, Reduce Motion overrides Interactive Artwork Shift.

Library → Game Details contains the Windows **Theme music** toggle, off by default. When enabled, game details play a local custom or matched Steam soundtrack track while the launcher is active. [Theme music](steam.md)

Library → General enables playtime tracking on new and existing profiles by default. Turning it off hides duration statistics and playtime sorts while keeping launch dates. The symbolic trash action uses the shared destructive confirmation and saves the cleared durations as one profile change.

Appearance uses `BuildChoicePicker` for the font family, font size, and scale. Its button displays the current value, and its scrollable menu uses the same frosted surface, spacing, and motion as context menus. The menu aligns to the button's left edge and switches to its right edge if the measured menu would extend past the launcher. Font families keep keyboard text search. The preference applies after the menu closes so changes to typography and scale leave the closing animation aligned with its trigger. Collections use the same picker for member sorting and rule matching, attributes, and operators. Mods use it for OptiScaler’s DLL name and DXGI spoofing, the ReShade graphics API, and RenoDX add-on variants.

Theme includes a Default accent colour picker for the launcher palette. Its dialog shows colour-only preset buttons and a square live preview the same height as the six-digit hex field. Apply saves the selection and updates the launcher immediately. Selecting the first purple swatch restores the built-in palette. The Artwork accent colour switch is enabled by default and uses the selected game's artwork colour on Game Details when available.

Colourway maps the interface's surface and foreground roles to Classic, dark Adwaita, Catppuccin Mocha, Everforest dark medium, and Tokyo Night. Classic retains its directional background lighting. The other colourways use solid canvas and placeholder colours. Accent resources are applied afterward and keep the existing default and artwork controls. The Widget Customisation subpage overrides the shared control, panel, and artwork radii and scales the opacity of existing small, popup, and dialog shadows. Defaults use each original radius and full shadow strength. [Adwaita CSS variables](https://github.com/GNOME/libadwaita/blob/main/doc/css-variables.md) · [Catppuccin Mocha palette](https://catppuccin.com/palette/) · [Everforest palette](https://github.com/sainnhe/everforest/blob/master/palette.md) · [Tokyo Night palette](https://github.com/folke/tokyonight.nvim/blob/main/lua/tokyonight/colors/night.lua)

`BindPreferenceAvailability` gives disabled preference rows the same muted treatment and exposes the reason on hover and through accessibility metadata. Interactive Artwork Shift and Reset first-usage prompts use it.

The scale picker offers presets and a Custom dialog that accepts any whole percentage from 75 through 200. Existing in-range values retain their saved percentage. An unset scale defaults to 100%.

- **Depends on** the profile preference model, section and control templates, the font picker and dynamic font resources, `WindowsDesktop` animation settings, and the artwork and update helpers.
- **Used by** the main render dispatcher, the constructor and activation hooks, `Launcher.Behaviour.cs` to notify preference listeners, and `Launcher.Settings.Verification.cs`.

[Desktop settings source](../../../avalonia/LGLauncher.Desktop/Launcher.Settings.cs) · [Steam and artwork](steam.md)

## `avalonia/LGLauncher.Desktop/Launcher.ReleaseNotes.cs`

Loads installed release notes when their About section attaches. It reuses successful notes for the window session and retries unavailable notes when About is reopened. When an update check reports a newer stable version, the section uses the shared tabs to switch between the installed and available releases. Both tabs use the selectable RSS article text controls. The panel header shows the selected version and its symbolic external-link action opens the corresponding GitHub release.

- **Depends on** `UpdateFeed`, `RssMarkup`, `BuildRssParagraph`, the shared settings section and tab styles, and window background cancellation.
- **Used by** `BuildAbout`, `CheckUpdates`, and the settings smoke test.

[Release notes source](../../../avalonia/LGLauncher.Desktop/Launcher.ReleaseNotes.cs) · [Official updates](updates.md) · [RSS renderer](rss.md)

## `avalonia/LGLauncher.Desktop/Launcher.Dialogs.cs`

Centralises modal opening and field layout, font selection and popup font resources, first-run usage acknowledgement, confirmation and notice dialogs, native pickers, folder and game editing, and shared game menu actions. Editing and asynchronous Steam lookups stay inside the modal until the user saves or cancels.

When a game save fails, Add game and Edit game keep the draft open and show a retry message in the form. The previous library and ignored paths are restored, and the discovery list and Steam retry state change only after a successful save.

The Tags field saves a manual override or uses Steam tags scoped to the App ID shown in the form. The Scale picker saves an optional library classification.

**Manage metadata** opens from the game context menu and the left side of the **Edit game** footer. Opening it from Edit game preserves the parent form and returns focus there when the child dialog closes. [Game media](steam.md)

Confirmation dialogs can use the shared red button treatment for the submit action. The cached mod download confirmation uses that treatment.

`ShowFirstUsagePrompts` runs on startup for new and reset profiles. It presents the usage disclosure first and then offers automatic updates on builds that support in-app updates.

`TryShowContributionReminder` uses the same dismissible dialog on the first return from play and on each multiple of 50 returns. It waits until the launcher is visible and any open modal has closed. Its buttons open the project's GitHub repository or the existing Ko-fi page in the user's browser.

- **Depends on** Avalonia font and storage APIs, shared `ModalHost`, `ModalDialog`, and menu builders, the profile and `SteamCatalog`, and the main window's game, artwork, and collection helpers.
- **Used by** the main window, collection editor, RSS settings, Home, artwork replacement, App Behaviour error reporting, and the modal and migration smoke tests.

[Dialogs source](../../../avalonia/LGLauncher.Desktop/Launcher.Dialogs.cs) · [Library views](library.md) · [Verification](verification.md)
