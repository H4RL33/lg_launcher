# Mod management

[Desktop source index](README.md)

The Windows desktop launcher keeps approved mod packages under `mods/packages` in its local profile. Managed installation records, rollback data, and archived edited configurations use `mods/installations`. Package deletion and installation removal are separate actions. These services preserve the shared library format used by the legacy launcher.

## `avalonia/LGLauncher.Desktop/Launcher.Mods.cs`

Builds Settings → Mods with separate Available mods and Managed installations panels. Available package rows put details and status on the left and actions, including the info tooltip, on the right. OptiScaler's refresh icon checks the package and compatibility catalog together. Both results appear inline beside the cached version and channel. Its tooltip shows the active catalog and last successful check. Managed rows show the rounded library game icon, title, and muted executable path including its extension beside the Manage action. The folder name and a game symbol identify records without a unique library match.

The game-detail wrench opens a Mods dialog where the OptiScaler row displays compatibility or installation status. Selecting the row opens the compatibility, recovery, or managed-install flow. Closing that dialog returns to the Mods list. The shell defers game launch and update handoff during game file changes. It checks for interrupted operations after startup. Managed records can be reopened from Settings after a game entry changes or is removed.

The ReShade row opens guided setup for 64-bit Windows games using DirectX 9, DirectX 10–12, OpenGL, or Vulkan. It uses the shared graphics API picker, download progress, file preview, and install progress. New installations use the official build with full add-on support. An existing launcher-managed standard install can be upgraded from its manager with **Enable full add-on support**. When pairing DirectX ReShade with managed OptiScaler, setup uses the same rendering executable and previews `ReShade64.dll` and the `LoadReshade` setting in `OptiScaler.ini`. ReShade settings, shaders, and presets remain in the game folder through an upgrade or removal of a managed DLL. [OptiScaler coexistence](https://github.com/optiscaler/OptiScaler/wiki/Compatibility-with-other-mods-%28Reshade%2C-SpecialK%29)

The Vulkan preview explains whether it will reuse an enabled registration, upgrade the shared launcher-managed layer for all games using it, or register a shared layer for the current Windows user. It activates ReShade by adding a `ReShade.ini` beside the rendering executable, including when OptiScaler is already installed. Per-game removal archives edits to that INI and keeps the registration available for other games. A current-user layer is available to games launched without administrator privileges. [Khronos Vulkan layer discovery](https://github.com/KhronosGroup/Vulkan-Loader/blob/main/docs/LoaderLayerInterface.md#windows-layer-discovery) · [ReShade activation source](https://github.com/crosire/reshade/blob/v6.8.0/source/dll_main.cpp)

The reviewed AMDNR launcher source handles its own OptiScaler deployment and proxy selection. The separate OpenNR installer reuses an installed Vulkan layer or registers one for the current Windows user, then uses a per-game `ReShade.ini` to activate it. Its shared layer stays registered after per-game removal. ReShade's official OpenXR setup uses machine-wide layer registration. [AMDNR installer](https://github.com/3zwr1/AMD-NR---OptiScaler/blob/1442e75e9f386479f8cd8c301712332c707b208f/Launcher/OpenSource/src/AmdnrLauncher.Core/Install/Installer.cs) · [OpenNR Vulkan layer](https://github.com/zmodelerlover/OpenNR-Installer/blob/163ab2320035a8c5014342990413631909474f70/src/AmdNr.Core/Routes/ReShade/Work.VulkanLayer.cs) · [ReShade OpenXR setup](https://github.com/crosire/reshade/blob/v6.8.0/setup/MainWindow.xaml.cs#L1154-L1173)

- **Depends on** mod services including `ModCatalogStore`, launcher navigation and session state, `SettingsRow`, `Section`, `Actions`, native storage pickers, `LauncherMenus`, and the shared modal host.
- **Used by** `Launcher.axaml`, `Launcher.axaml.cs`, `Launcher.Navigation.cs`, and `Launcher.Mods.Verification.cs`.

Each package row uses symbolic refresh and download buttons and shows download progress with the shared splash bar. The percentage survives navigation back to Mods. Game setup shows per-package download progress in its download dialog and keeps the installation progress and result in the install dialog. File previews distinguish additions, in-place edits, replacements, and removals by action name, symbol, and colour. When ReShade setup edits `LoadReshade` in an existing `OptiScaler.ini`, the install preview marks that file **Modified** with a blue pencil. It uses the plan's chaining configuration path to identify the edit. The install preview explains how original files are backed up and links to upstream usage guides.

Each Available mods row has one red trash button when it has a cached download. For a single download, the tooltip names the version and the button opens confirmation directly. With several cached downloads, the button opens the shared menu with each version and variant. Choosing an entry opens the same confirmation. The dialog identifies the chosen download, explains that games keep their installed mods, and uses a red confirmation button.

Game setup keeps the form open while **Preview install** shows **Preparing preview…**. Cancel stops a pending preview. The destination dialog opens before setup closes, and a dismissed download prompt returns to the setup form.

The Mods settings heading explains that requests are for game-agnostic mods and directs users to a dedicated manager for granular, per-game mod management. **Request a mod** opens the public LG Launcher issue composer with prompts for the mod name, official link, supported games, dependencies, and reason to include it. The user completes and submits the draft on GitHub. [GitHub issue query parameters](https://docs.github.com/en/issues/tracking-your-work-with-issues/using-issues/creating-an-issue#creating-an-issue-from-a-url-query)

The game Mods list shows each managed mod on its own row. Its amber **Warnings** panel shows a count and starts collapsed. It appears when there is a warning, such as the shared Insert hotkey when OptiScaler and REFramework are installed together. The entire header opens and closes the panel, with a hover highlight on the chevron. The panel uses the same top-down reveal and collapse as context menus and respects Reduced Motion. Each mod opens the same manager with its usage guide, version in the footer, and uninstall, verification, and affected-files actions. Before uninstall, a fresh inspection supplies the confirmation preview. The confirmation uses the recorded dependency links to name any installed mods that depend directly or indirectly on the selected mod. Other providers' files and backups are kept.

Guided and verified setup and Manage OptiScaler use the shared picker for **DXGI hardware spoofing** with Automatic, Enabled, and Disabled choices. Verified recipes supply the preselected mode. The install preview shows the chosen mode. In the manager the inline tick saves the selected setting for the next game launch. [OptiScaler v0.9.5 configuration](https://github.com/optiscaler/OptiScaler/blob/v0.9.5/OptiScaler.ini)

The manager opens from its installation record and reuses the most recent inspection. A first inspection can run in the background and concurrent requests share it. File operations invalidate the cache. The verification icon runs a fresh check with progress and reports results for the selected provider. The adjacent white status icon opens **Affected files** with yellow replacement symbols for changed files and red removal symbols for missing files. Closing either dialog returns to the manager.

## `avalonia/LGLauncher.Desktop/Mods/ModPackages.cs`

Selects official downloads by provider, variant, version, digest, and reviewed package members. The update check reports when the cached package matches upstream. It stages downloads through [VerifiedDownload](updates.md#avalonialglauncherdesktopverifieddownloadcs) before publishing a cache entry and can report whole percentages from the bytes written and the known package size. A newer layout or rolling archive requires its own review. The game flow can acquire required packages before previewing the install.

| Provider | Reviewed asset | Install payload |
| --- | --- | --- |
| OptiScaler | `Optiscaler_0.9.5-final.20261116.7z` and its GitHub asset digest | Loader, INI and runtime files in the reviewed 0.9 layout. [Release asset](https://github.com/optiscaler/OptiScaler/releases/expanded_assets/v0.9.5) |
| REFramework | Unified nightly `01441`, `REFramework.zip`, and its GitHub asset digest | Non-VR `dinput8.dll`. [Release asset](https://github.com/praydog/REFramework-nightly/releases/expanded_assets/nightly-01441-49c21b26d50b23a333e9025743c8d45c46bb460e) |
| Special K | Official `SpecialK.7z`, with the digest pinned from the reviewed archive | `SpecialK64.dll`, PE version `26.10.10`. A changed archive is held for review. [Local install guide](https://wiki.special-k.info/SpecialK/Local) |
| ReShade | Full add-on `ReShade_Setup_6.8.0_Addon.exe` downloaded directly from ReShade, with a pinned SHA-256 digest | Extracts `ReShade64.dll` and its Vulkan layer manifest and verifies both digests. The standard edition uses a separate cache entry. Stable upstream tags announce newer releases while downloads stay pinned to the reviewed build. [Official download](https://reshade.me/#download) |

## `avalonia/LGLauncher.Desktop/Mods/ModReShadeVulkan.cs`

Finds enabled x64 implicit Vulkan layers in the machine and current-user registry. It reuses a single enabled copy if its DLL matches the reviewed full add-on binary. The launcher stages its reviewed DLL and Vulkan manifest together in a versioned profile folder, outside the package cache. It registers that copy for the current user after rechecking for an existing layer. For a managed standard layer, it verifies the older binary and replaces only the DLL because both editions ship the same manifest. An unverified external layer needs to be switched or disabled through its owning tool. Game removal and cache deletion leave the installed shared layer available. [Khronos Vulkan layer discovery](https://github.com/KhronosGroup/Vulkan-Loader/blob/main/docs/LoaderLayerInterface.md#windows-layer-discovery) · [OpenNR approach](https://github.com/zmodelerlover/OpenNR-Installer/blob/163ab2320035a8c5014342990413631909474f70/src/AmdNr.Core/Routes/ReShade/Work.VulkanLayer.cs)

## `avalonia/LGLauncher.Desktop/Mods/ModArchiveReader.cs`

Checks ZIP and 7z member paths, types, count, and expanded size. It extracts files covered by the reviewed descriptor and hashes the result, reusing one copy buffer across members. The ReShade reader opens the embedded ZIP at the pinned package's reviewed offset without executing the installer. SharpCompress reads 7z archives.

## `avalonia/LGLauncher.Desktop/Mods/SharpCompress.LICENSE.txt`

Retains the upstream notice for SharpCompress. The Desktop project embeds it alongside the archive reader dependency.

[SharpCompress license](../../../avalonia/LGLauncher.Desktop/Mods/SharpCompress.LICENSE.txt)

## `avalonia/LGLauncher.Desktop/Mods/ModCompatibility.cs`

Matches the bundled wiki snapshot by normalized title or a linked Steam App ID. Verified recipes also require a matching launch path, rendering executable and build identity. Local checks look for known anti-cheat markers and dependency-specific executables. This is the compatibility input for the install planner.

## `avalonia/LGLauncher.Desktop/Mods/ModCatalogStore.cs`

Downloads the versioned OptiScaler advisory feed over HTTPS with a size limit and timeout. It validates the data before atomically replacing `mods/optiscaler-catalog-v1.json` in the local profile. A failed check keeps the last usable catalog. A missing or invalid cache falls back to the bundled snapshot. The launcher checks when it starts and periodically while open, with a daily automatic check interval and a manual refresh. Downloaded reports supply display labels, supported inputs, notes, and official wiki links. Install eligibility uses the bundled catalog and local checks. [Public feed](https://raw.githubusercontent.com/H4RL33/lg_launcher/refs/heads/compatibility-catalog/optiscaler-v1.json)

## `avalonia/LGLauncher.Desktop/Mods/mod-catalog.json`

Contains the 719-row bundled fallback from OptiScaler wiki revision `61ce9f4ad2ee274890efc59ecdd7dd38c0fdb7a4`, bundled install restrictions, and verified recipes. Keep dependency and anti-cheat dispositions conservative when refreshing it. Enable a recipe only after testing the matching game build, package versions, overlay, selected upscaler, uninstall, and a clean subsequent boot. [Compatibility list](https://github.com/optiscaler/OptiScaler/wiki/Compatibility-List)

### PRAGMATA pilot

OptiScaler `v0.9.5` and REFramework nightly `01441` ran on a copy of the local `PRAGMATA.exe` build recorded in the catalog. On the Radeon RX 9070 XT, DirectX 12 displayed both overlays and OptiScaler reported active FSR 4.1.2 from the game's FidelityFX input. The recipe preserves the tested build's existing FidelityFX and XeSS libraries. Both documented Special K arrangements tested with version `26.10.10` exited with a Windows access violation, so the recipe offers the two-mod stack. After removal, the copy matched 40 original EXE and DLL hashes and started without local mod loaders. [PRAGMATA compatibility notes](https://github.com/optiscaler/OptiScaler/wiki/PRAGMATA)

## `avalonia/LGLauncher.Desktop/Mods/ModInstallPlanner.cs`

Converts guided choices or a verified recipe into exact destinations and checked payloads. It previews additions and replacements, reports collisions, and requires the complete dependency stack before proceeding. The guided path takes a local Windows x64 executable, an approved OptiScaler package, and a supported DLL name. The planner applies the DXGI selection to the staged INI after verifying the cached original.

The ReShade plan selects a local loader for the chosen graphics API and rejects occupied destinations or recognized unmanaged ReShade proxies. For coexistence, the plan verifies the managed OptiScaler loader, reads the INI beside it, and records the original load setting and the expected configuration hash. It includes an INI change in the plan only if enabling `LoadReshade` changes the existing bytes. To switch a standalone ReShade install to coexistence, uninstall ReShade through its manager, install OptiScaler, and add ReShade again.

For Vulkan, the planner reuses a verified full add-on layer without requiring the launcher package. Preview checks the local activation path and blocks an existing INI, which may belong to an external installation. A new registration or managed standard-layer upgrade requires the reviewed package.

## `avalonia/LGLauncher.Desktop/Mods/ModDxgiConfiguration.cs`

Reads and changes the single `Dxgi` value in `[Spoofing]` and the single `LoadReshade` value in `[Plugins]`. Both accept `auto`, `true`, or `false` and preserve the other bytes, including the optional UTF-8 byte order mark. The parser checks size and encoding and rejects ambiguous or unsupported configurations. Streamline and Vulkan spoofing use separate upstream settings. [OptiScaler v0.9.5 configuration](https://github.com/optiscaler/OptiScaler/blob/v0.9.5/OptiScaler.ini)

## `avalonia/LGLauncher.Desktop/Mods/ModPaths.cs`

Checks relative paths and rejects Windows device names, streams, aliases, and reparse points. The archive reader and transaction service share these rules.

## `avalonia/LGLauncher.Desktop/Mods/ModInstallState.cs`

Defines game identity, file snapshots, install plans, and persisted manifests. Plans and manifests can record provider dependency links, the original ReShade chain setting, and use of the shared Vulkan layer. Older OptiScaler records infer links to the companion packages that their setup stored together. It verifies PE architecture and hashes the game executable and affected files. State and journal writes flush before replacement.

## `avalonia/LGLauncher.Desktop/Mods/ModTransactions.cs`

Backs up originals and records intended changes before touching the game directory. It verifies the game build and expected files under the operation lock. Read-only inspection can report progress while checking recorded file hashes. Install progress covers preparation, preflight, file application, and verification, with completion reported after journal cleanup. A matching state transaction ID commits the journal. Recovery compares actual files with the recorded before and after states and keeps unexpected bytes for review. Uninstall snapshots deployed files for rollback and checks build identity before restoring vendor files.

Uninstall can target the files of one provider in the manifest. Other providers' file ownership and dependency links carry forward in the same journal transaction. Editing DXGI spoofing uses a single-file journal for the managed INI next to the rendering executable. Reading the setting avoids rehashing the executable. Saving still requires a matching file snapshot and game build, an eligible game folder, and an idle game. It retains original backups and preserves the edited-file status of configurations that were already changed outside the launcher.

Adding ReShade to an existing OptiScaler record applies the DLL and the INI change in one journal after rechecking the expected INI hash. The INI stays owned by OptiScaler and keeps its original backup. Removing ReShade restores the prior load value if the key is still enabled and keeps changes to other settings. The dependency warning names ReShade when removing the OptiScaler copy that loads it.

An in-place ReShade upgrade verifies the installed standard DLL, then journals the binary replacement and updates its package record. It keeps the original uninstall information and leaves configuration and other mods intact. A Vulkan upgrade instead replaces the known launcher-managed shared DLL and keeps each game's activation file intact.

Vulkan applies its game activation file via the same journal after validating or registering the shared layer. A registration failure rolls back the game operation. Verification reports missing or invalid shared registrations and allows normal edits to the activation INI. Uninstall archives edited activation settings before removing the game file.

## `avalonia/LGLauncher.Desktop/Mods/ModVerification.cs`

Exercises package validation, catalog refresh and offline fallback, install and removal recovery, and guided eligibility using disposable data. The optional `-Archives` argument supplies the official release files for offline validation, including both standard and full add-on ReShade installers. The ReShade fixtures check the extracted payload, all four API choices, protection of occupied files, coexistence and stale preview checks, selective uninstall, and recovery from an interrupted upgrade. Vulkan fixtures inject a registry substitute to check registration failure, reuse, shared upgrades, external layer protection, and archived settings. [Test harness](../../../tests/Verify-AvaloniaMods.ps1) · [UI verification](verification.md)
