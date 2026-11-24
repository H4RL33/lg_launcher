# Mod management

[Desktop source index](README.md)

The Windows desktop launcher keeps approved mod packages under `mods/packages` in its local profile. Managed installation records, rollback data, and archived edited configurations use `mods/installations`. Package deletion and installation removal are separate actions. These services preserve the shared library format used by the legacy launcher.

## `avalonia/LGLauncher.Desktop/Launcher.Mods.cs`

Builds Settings → Mods with separate Available mods and Managed installations panels. Available package rows put details and status on the left and actions on the right. The RenoDX row offers a catalog refresh and an Info dialog that directs users to their game’s Mods list for the matching add-on. OptiScaler's refresh icon checks the package and compatibility catalog together. Both results appear inline beside the cached version and channel. Its tooltip shows the active catalog and last successful check. Managed rows show the rounded library game icon, title, and muted executable path including its extension beside the Manage action. The folder name and a game symbol identify records without a unique library match.

The game-detail wrench and the managed row’s **Manage** action open the same Mods dialog. Installed supported mods sort ahead of setup options and carry a green check beside the chevron. An existing Special K installation appears under **Legacy installations**. The game detail stat, portrait wrench, and dynamic Modded rule use the matching managed installation record. Settings can reopen managed records after a library entry changes or is removed. The shell defers game launch and update handoff during game file changes and checks for interrupted operations after startup.

RenoDX uses the rendering executable already recorded for full add-on ReShade. When there is no matching automatic download, the setup uses the shared local file field and accepts a Windows x64 `.addon64` DLL. The preview records its digest as an imported package and uses the same transaction, dependency checks, verification, and uninstall process as a downloaded copy.

The ReShade row opens guided setup for 64-bit Windows games using DirectX 9, DirectX 10–12, OpenGL, or Vulkan. It uses the shared graphics API picker, download progress, file preview, and install progress. New installations use the official build with full add-on support. An existing launcher-managed standard install can be upgraded from its manager with **Enable full add-on support**. When pairing DirectX ReShade with managed OptiScaler, setup uses the same rendering executable and previews `ReShade64.dll` and the `LoadReshade` setting in `OptiScaler.ini`. ReShade settings, shaders, and presets remain in the game folder through an upgrade or removal of a managed DLL. [OptiScaler coexistence](https://github.com/optiscaler/OptiScaler/wiki/Compatibility-with-other-mods-%28Reshade%2C-SpecialK%29)

The Vulkan preview explains whether it will reuse an enabled registration, upgrade the shared launcher-managed layer for all games using it, or register a shared layer for the current Windows user. It activates ReShade by adding a `ReShade.ini` beside the rendering executable, including when OptiScaler is already installed. Per-game removal archives edits to that INI and keeps the registration available for other games. A current-user layer is available to games launched without administrator privileges. [Khronos Vulkan layer discovery](https://github.com/KhronosGroup/Vulkan-Loader/blob/main/docs/LoaderLayerInterface.md#windows-layer-discovery) · [ReShade activation source](https://github.com/crosire/reshade/blob/v6.8.0/source/dll_main.cpp)

The reviewed AMDNR launcher source handles its own OptiScaler deployment and proxy selection. The separate OpenNR installer reuses an installed Vulkan layer or registers one for the current Windows user, then uses a per-game `ReShade.ini` to activate it. Its shared layer stays registered after per-game removal. ReShade's official OpenXR setup uses machine-wide layer registration. [AMDNR installer](https://github.com/3zwr1/AMD-NR---OptiScaler/blob/1442e75e9f386479f8cd8c301712332c707b208f/Launcher/OpenSource/src/AmdnrLauncher.Core/Install/Installer.cs) · [OpenNR Vulkan layer](https://github.com/zmodelerlover/OpenNR-Installer/blob/163ab2320035a8c5014342990413631909474f70/src/AmdNr.Core/Routes/ReShade/Work.VulkanLayer.cs) · [ReShade OpenXR setup](https://github.com/crosire/reshade/blob/v6.8.0/setup/MainWindow.xaml.cs#L1154-L1173)

- **Depends on** mod services including `ModCatalogStore`, launcher navigation and session state, `SettingsRow`, `Section`, `Actions`, native storage pickers, `LauncherMenus`, and the shared modal host.
- **Used by** `Launcher.axaml`, `Launcher.axaml.cs`, `Launcher.Navigation.cs`, and `Launcher.Mods.Verification.cs`.

Each package row uses symbolic refresh and download buttons and shows download progress with the shared splash bar. The percentage survives navigation back to Mods. Game setup shows per-package download progress in its download dialog and keeps the installation progress and result in the install dialog. File previews distinguish additions, in-place edits, replacements, and removals by action name, symbol, and colour. When ReShade setup edits `LoadReshade` in an existing `OptiScaler.ini`, the install preview marks that file **Modified** with a blue pencil. It uses the plan's chaining configuration path to identify the edit. The install preview explains how original files are backed up and links to upstream usage guides.

The game Mods list and Available mods rows use the same bundled Lucide symbols. OptiScaler uses scaling, REFramework uses construction, ReShade uses a palette, and RenoDX uses a lightbulb. The legacy Special K row uses a gauge. The wrench identifies the Mods entry points.

Each Available mods row has one red trash button when it has a cached download. For a single download, the button opens confirmation directly. For several, it opens a modal listing each version, variant, and count of managed installations using that package. Choosing a version opens confirmation for that cached copy. Cache deletion leaves the package’s deployed files and uninstall information in place.

The update symbol downloads an eligible newer package into the cache. Inside a game’s Mods dialog, a mod manager can check for updates and preview an available replacement. When a newer release cannot be applied to the managed setup, Settings or the manager shows a link to the official release as applicable. A managed RenoDX install without an eligible automatic update can use **Update from file**. The replacement retains the deployed filename and uninstall backup. Local updates retain package ownership, configuration files, and original backups. They apply to same-variant managed records. Recipe-pinned combinations and shared Vulkan ReShade keep their existing update rules.

Game setup keeps the form open while **Preview install** shows **Preparing preview…**. Cancel stops a pending preview. The destination dialog opens before setup closes, and a dismissed download prompt returns to the setup form.

The Mods settings heading explains that requests are for game-agnostic mods and directs users to a dedicated manager for granular, per-game mod management. **Request a mod** opens the public LG Launcher issue composer with prompts for the mod name, official link, supported games, dependencies, and reason to include it. The user completes and submits the draft on GitHub. [GitHub issue query parameters](https://docs.github.com/en/issues/tracking-your-work-with-issues/using-issues/creating-an-issue#creating-an-issue-from-a-url-query)

The amber **Warnings** panel starts collapsed and counts unmet known requirements and compatibility warnings. It can show OptiScaler’s REFramework requirement before setup, the need for a specific REFramework branch and companion plugin, RenoDX’s full add-on ReShade prerequisite, or the shared Insert hotkey when OptiScaler and REFramework are installed together. The entire header opens and closes the panel. It uses the same reveal and collapse as context menus and respects Reduced Motion. Each mod opens the same manager with its usage guide, version in the footer, and uninstall, verification, and affected-files actions. Before uninstall, a fresh inspection supplies the confirmation preview and the recorded direct and indirect dependents.

Guided OptiScaler setup uses the shared animated picker for the DLL name. Guided and verified setup and Manage OptiScaler use it for **DXGI hardware spoofing**, with Automatic, Enabled, and Disabled choices. Verified recipes supply the preselected mode. The install preview shows the chosen mode, and the manager’s inline tick saves it for the next game launch. ReShade uses the same picker for graphics API, and RenoDX uses it when several add-ons match the game and managed ReShade setup. [OptiScaler v0.9.5 configuration](https://github.com/optiscaler/OptiScaler/blob/v0.9.5/OptiScaler.ini)

The manager opens from its installation record and reuses the most recent inspection. A first inspection can run in the background and concurrent requests share it. File operations invalidate the cache. The verification icon runs a fresh check with progress and reports results for the selected provider. The adjacent white status icon opens **Affected files** with yellow replacement symbols for changed files and red removal symbols for missing files. Closing either dialog returns to the manager.

## `avalonia/LGLauncher.Desktop/Mods/ModPackages.cs`

The package checker can select uniquely identified OptiScaler stable and REFramework unified archives with an official GitHub SHA-256 and size. The same archive path constraints and expected member validation apply when downloading them. Cached packages retain their official source descriptor for future setup and update previews. Guided OptiScaler and standalone REFramework can use newer verified caches. Exact recipes retain their reviewed package pins. ReShade installer updates remain tied to a reviewed installer and extracted payload digest. RenoDX refreshes update candidates from the verified snapshot.

Selects official downloads by provider, variant, version, digest, and package members. The update check reports when the cached package matches upstream. It stages downloads through [VerifiedDownload](updates.md#avalonialglauncherdesktopverifieddownloadcs) before publishing a cache entry and reports progress using the known package size. Fixed archives use reviewed layouts and pinned digests. Game-specific RenoDX packages use the official snapshot asset size and digest. The game flow can acquire required packages before previewing the install.

| Provider | Reviewed asset | Install payload |
| --- | --- | --- |
| OptiScaler | `Optiscaler_0.9.5-final.20261116.7z` and its GitHub asset digest | Loader, INI and runtime files in the reviewed 0.9 layout. [Release asset](https://github.com/optiscaler/OptiScaler/releases/expanded_assets/v0.9.5) |
| REFramework | Unified nightly `01441`, `REFramework.zip`, and its GitHub asset digest | Non-VR `dinput8.dll`. [Release asset](https://github.com/praydog/REFramework-nightly/releases/expanded_assets/nightly-01441-49c21b26d50b23a333e9025743c8d45c46bb460e) |
| Special K, legacy fixture | Official `SpecialK.7z`, with the digest pinned from the reviewed archive | `SpecialK64.dll`, PE version `26.10.10`. The fixture exercises recorded companion ownership and uninstall. [Local install guide](https://wiki.special-k.info/SpecialK/Local) |
| ReShade | Full add-on `ReShade_Setup_6.8.0_Addon.exe` downloaded directly from ReShade, with a pinned SHA-256 digest | Extracts `ReShade64.dll` and its Vulkan layer manifest and verifies both digests. The standard edition uses a separate cache entry. Stable upstream tags announce newer releases while downloads stay pinned to the reviewed build. [Official download](https://reshade.me/#download) |
| RenoDX | Game-specific `renodx-*.addon64` asset from RenoDX’s official snapshot, with its published SHA-256 digest | Validates and copies one Windows x64 add-on beside the rendering executable recorded for ReShade. [Official snapshot](https://github.com/clshortfuse/renodx/releases/tag/snapshot) |

## `avalonia/LGLauncher.Desktop/Mods/ModReShadeVulkan.cs`

Finds enabled x64 implicit Vulkan layers in the machine and current-user registry. It reuses a single enabled copy if its DLL matches the reviewed full add-on binary. The launcher stages its reviewed DLL and Vulkan manifest together in a versioned profile folder, outside the package cache. It registers that copy for the current user after rechecking for an existing layer. For a managed standard layer, it verifies the older binary and replaces only the DLL because both editions ship the same manifest. An unverified external layer needs to be switched or disabled through its owning tool. Game removal and cache deletion leave the installed shared layer available. [Khronos Vulkan layer discovery](https://github.com/KhronosGroup/Vulkan-Loader/blob/main/docs/LoaderLayerInterface.md#windows-layer-discovery) · [OpenNR approach](https://github.com/zmodelerlover/OpenNR-Installer/blob/163ab2320035a8c5014342990413631909474f70/src/AmdNr.Core/Routes/ReShade/Work.VulkanLayer.cs)

## `avalonia/LGLauncher.Desktop/Mods/ModArchiveReader.cs`

Checks ZIP and 7z member paths, types, count, and expanded size. It extracts files covered by the reviewed descriptor and hashes the result, reusing one copy buffer across members. The ReShade reader opens the embedded ZIP at the pinned package's reviewed offset without executing the installer. RenoDX ships as a single file. The reader checks its filename and x64 DLL header after the download digest check. SharpCompress reads 7z archives.

## `avalonia/LGLauncher.Desktop/Mods/SharpCompress.LICENSE.txt`

Retains the upstream notice for SharpCompress. The Desktop project embeds it alongside the archive reader dependency.

[SharpCompress license](../../../avalonia/LGLauncher.Desktop/Mods/SharpCompress.LICENSE.txt)

## `avalonia/LGLauncher.Desktop/Mods/ModCompatibility.cs`

Matches the bundled OptiScaler wiki snapshot by normalized title, alias, or linked Steam App ID. Maps known wiki entries and explicit compatibility notes to provider requirements, including the `pd-upscaler` REFramework branch where applicable. Verified recipes require a matching launch path, rendering executable, and build identity. Local checks look for known anti-cheat markers and executables supported by the unified REFramework package.

## `avalonia/LGLauncher.Desktop/Mods/ModCatalogStore.cs`

Downloads the versioned OptiScaler advisory feed over HTTPS with a size limit and timeout. It validates the data before atomically replacing `mods/optiscaler-catalog-v1.json` in the local profile. A failed check keeps the last usable catalog. A missing or invalid cache falls back to the bundled snapshot. The launcher checks when it starts and periodically while open, with a daily automatic check interval and a manual refresh. Downloaded reports supply display labels, supported inputs, notes, and official wiki links. Explicit REFramework requirements in the notes can add prerequisites for guided setup. The bundled catalog and local checks govern verified recipes and safety restrictions. The v0.1.16 executable uses a retired feed URL and falls back to its cached or bundled catalog. [Public feed](https://raw.githubusercontent.com/H4RL33/lg_launcher/refs/heads/mods-support/compatibility-catalog/optiscaler-v1.json)

## `avalonia/LGLauncher.Desktop/Mods/mod-catalog.json`

Contains the 719-row bundled fallback from OptiScaler wiki revision `61ce9f4ad2ee274890efc59ecdd7dd38c0fdb7a4`, with maintained dependency notes, title aliases, Steam IDs, install restrictions, and verified recipes. Keep dependency and anti-cheat dispositions conservative when refreshing it. Enable a recipe only after testing the matching game build, package versions, overlay, selected upscaler, uninstall, and a clean subsequent boot. [Compatibility list](https://github.com/optiscaler/OptiScaler/wiki/Compatibility-List)

### PRAGMATA pilot

OptiScaler `v0.9.5` and REFramework nightly `01441` ran on a copy of the local `PRAGMATA.exe` build recorded in the catalog. On the Radeon RX 9070 XT, DirectX 12 displayed both overlays and OptiScaler reported active FSR 4.1.2 from the game's FidelityFX input. The recipe preserves the tested build's existing FidelityFX and XeSS libraries. Both documented Special K arrangements tested with version `26.10.10` exited with a Windows access violation, so the recipe offers the two-mod stack. After removal, the copy matched 40 original EXE and DLL hashes and started without local mod loaders. [PRAGMATA compatibility notes](https://github.com/optiscaler/OptiScaler/wiki/PRAGMATA)

## `avalonia/LGLauncher.Desktop/Mods/ModInstallPlanner.cs`

The add-on planner validates the local file extension and x64 PE headers for an imported RenoDX add-on. The update planner reuses each deployed destination and validates the cached file digest before building the replacement preview. Existing INI files keep their settings.

Converts guided choices or a verified recipe into exact destinations and checked payloads. Unowned files at planned destinations are shown as replacements and their hashes are captured before installation. A destination owned by another managed mod is protected. The guided path takes a local Windows x64 executable, an approved OptiScaler package, and a supported DLL name. The planner applies the DXGI selection to the staged INI after verifying the cached original. An existing managed REFramework can satisfy a guided requirement or a verified recipe that expects the same package and destination. Games that require an external branch or other unsupported setup link to their compatibility notes.

The ReShade plan selects a local loader for the chosen graphics API and protects recognized unmanaged ReShade installations. For coexistence, the plan verifies the managed OptiScaler loader, reads the INI beside it, and records the original load setting and expected configuration hash. It includes an INI change in the plan only if enabling `LoadReshade` changes the existing bytes. To switch a standalone ReShade install to coexistence, uninstall ReShade through its manager, install OptiScaler, and add ReShade again.

The single-file setup path plans REFramework’s loader and RenoDX’s matching add-on. RenoDX needs full add-on ReShade in the same managed record. When the catalog specifies a rendering executable, graphics API, or loader name, the planner checks it against ReShade’s installation.

For Vulkan, the planner reuses a verified full add-on layer without requiring the launcher package. Preview checks the local activation path and blocks an existing INI, which may belong to an external installation. A new registration or managed standard-layer upgrade requires the reviewed package.

## `avalonia/LGLauncher.Desktop/Mods/ModDxgiConfiguration.cs`

Reads and changes the single `Dxgi` value in `[Spoofing]` and the single `LoadReshade` value in `[Plugins]`. Both accept `auto`, `true`, or `false` and preserve the other bytes, including the optional UTF-8 byte order mark. The parser checks size and encoding and rejects ambiguous or unsupported configurations. Streamline and Vulkan spoofing use separate upstream settings. [OptiScaler v0.9.5 configuration](https://github.com/optiscaler/OptiScaler/blob/v0.9.5/OptiScaler.ini)

## `avalonia/LGLauncher.Desktop/Mods/ModPaths.cs`

Checks relative paths and rejects Windows device names, streams, aliases, and reparse points. The archive reader and transaction service share these rules.

## `avalonia/LGLauncher.Desktop/Mods/ModInstallState.cs`

Defines game identity, file snapshots, install plans, and persisted manifests. Plans and manifests record provider dependency links with an optional package variant, the original ReShade chain setting, and use of the shared Vulkan layer. Dependency checks follow the recorded chain and can use a file inspection. Older OptiScaler records infer links to the companion packages that their setup stored together. It verifies PE architecture and hashes the game executable and affected files. State and journal writes flush before replacement.

## `avalonia/LGLauncher.Desktop/Mods/ModTransactions.cs`

Same-variant package updates use the normal installation journal. They verify that the original package and deployed file digests still match the preview, preserve the original uninstall backups, and publish the replacement package key only after the file changes succeed.

Backs up originals under each operation’s ID and records intended changes before touching the game directory. It verifies the game build, expected files, and existing managed prerequisites under the operation lock. Read-only inspection reports progress while checking recorded file hashes. Install progress covers preparation, preflight, file application, and verification, with completion reported after journal cleanup. A matching state transaction ID commits the journal. Recovery compares actual files with the recorded before and after states and keeps unexpected bytes for review. Uninstall checks build identity and the backup hash before restoring originals, including when a deployed file is missing. It can archive an edited INI before restoring the original.

Uninstall can target the files of one provider in the manifest. Other providers' file ownership and dependency links carry forward in the same journal transaction. Editing DXGI spoofing uses a single-file journal for the managed INI next to the rendering executable. Reading the setting avoids rehashing the executable. Saving still requires a matching file snapshot and game build, an eligible game folder, and an idle game. It retains original backups and preserves the edited-file status of configurations that were already changed outside the launcher.

Adding ReShade to an existing OptiScaler record applies the DLL and the INI change in one journal after rechecking the expected INI hash. The INI stays owned by OptiScaler and keeps its original backup. Removing ReShade restores the prior load value if the key is still enabled and keeps changes to other settings. The dependency warning names ReShade when removing the OptiScaler copy that loads it.

An in-place ReShade upgrade verifies the installed standard DLL, then journals the binary replacement and updates its package record. It keeps the original uninstall information and leaves configuration and other mods intact. A Vulkan upgrade instead replaces the known launcher-managed shared DLL and keeps each game's activation file intact.

Vulkan applies its game activation file via the same journal after validating or registering the shared layer. A registration failure rolls back the game operation. Verification reports missing or invalid shared registrations and allows normal edits to the activation INI. Uninstall archives edited activation settings before removing the game file.

## `avalonia/LGLauncher.Desktop/Mods/ModVerification.cs`

Exercises package validation, catalog refresh and offline fallback, install and removal recovery, and guided eligibility using disposable data. The guided fixture checks stale replacement previews and recovery of a missing game library. The RenoDX fixture verifies Steam and alias matching, rejected index changes, package identity, variant requirements, and selective uninstall. The optional `-Archives` argument supplies the official release files for offline validation, including standard and full add-on ReShade installers. The ReShade fixtures check the extracted payload, API choices, original file restoration, coexistence, selective uninstall, and recovery from an interrupted upgrade. Vulkan fixtures inject a registry substitute to check registration failure, reuse, shared upgrades, external layer protection, and archived settings. [Test harness](../../../tests/Verify-AvaloniaMods.ps1) · [UI verification](verification.md)

## `avalonia/LGLauncher.Desktop/Mods/ModRenoDxCatalog.cs`

Downloads the RenoDX website index and the publisher’s GitHub snapshot release metadata with a size limit and timeout. It requires the index SHA-256 and size to match the release asset. For each game-specific x64 add-on, it joins the artifact to the official asset by filename and size and uses the asset’s SHA-256 for the package download. The catalog matches Steam App IDs first and uses normalized titles and aliases when an ID is unavailable. Games whose add-ons come from other sites can still be looked up from the RenoDX mod page. Successful refreshes atomically update `mods/renodx-catalog-v1.json` for offline use. The launcher checks at startup and at most daily while it can fetch, and Settings offers a manual refresh. This service supplies availability, compatible add-on choices, and the ReShade requirement to the launcher and install planner. [Website index](https://renodx.com/games-index.json) · [Official snapshot](https://github.com/clshortfuse/renodx/releases/tag/snapshot)

The managed DXGI spoofing fixture completes its executable changes and idle-game operations before checking that a running game blocks edits. It closes the disposable game process's input and waits for exit after that final check. Windows can keep the executable mapped briefly after the game exits.
