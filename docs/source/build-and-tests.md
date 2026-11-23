# Build, packaging, tests, and assets

[Source map](README.md) · [Coding style and lint](../coding-style.md) · [Desktop](desktop/README.md)

Run commands from the repository root. The default Windows product is `build/LG Launcher.exe`. Smoke fixtures and staging files belong under `.build-work/`. Create that directory before running the standalone gallery smoke on a fresh checkout. The source tree can be used without release credentials.

| When | Command |
| --- | --- |
| After changes | `pwsh -NoProfile -File ./lint.ps1` |
| Shared model, parsers, process tracking, or discovery | `pwsh -NoProfile -File ./build.ps1 -Test` |
| Windows desktop behaviour, migration, or update handoff | `pwsh -NoProfile -File ./build.ps1 -Smoke` |
| Windows Mods UI only | `pwsh -NoProfile -File ./tests/Verify-AvaloniaMigration.ps1 -Launcher './build/LG Launcher.exe' -ModsOnly` |
| Windows mod package and transaction fixtures | `pwsh -NoProfile -File ./tests/Verify-AvaloniaMods.ps1 -Launcher './build/LG Launcher.exe'` |
| OptiScaler catalog importer | `python -B -m unittest discover -s tests -p 'test_import_optiscaler_catalog.py'` |
| Shared UI preview and control smoke | `dotnet run --project avalonia/LGLauncher.Gallery/LGLauncher.Gallery.csproj -- --smoke .build-work/gallery.png` |
| Windows Setup changes | `pwsh -NoProfile -File ./build-installer.ps1 -Launcher './build/LG Launcher.exe' -Smoke` |
| In-app Windows Setup upgrade | `pwsh -NoProfile -File ./tests/Verify-AvaloniaSetupUpdate.ps1` |

## Stable Windows releases

1. Select changes for `next` and set `Version` in the [Desktop project](../../avalonia/LGLauncher.Desktop/LGLauncher.Desktop.csproj) to the approved three-part version. Review the `next` → `main` pull request after Windows integration passes. [Development and release workflow](../workflow.md#publish-from-main)
2. Build and verify the Windows x64 portable executable from the resulting `main` commit. In a fresh PowerShell session, set `$env:IncludeSourceRevisionInInformationalVersion = 'false'` before running `pwsh -NoProfile -File ./build.ps1 -Smoke`. The publication workflow and Setup builder require the executable's `ProductVersion` to equal the three-part release version. Run the additional fixtures relevant to the release.
3. Stage `LG-Launcher.exe`, its GNU-format `LG-Launcher.exe.sha256`, and `RELEASE-NOTES.md` on `release-assets/vX.Y.Z`. The workflow compiles Setup using the installer files from the tagged source commit. With release authorization, push the staging branch before pushing the matching `vX.Y.Z` tag from `main`.
4. Wait for the read-only verification job to check the portable executable and smoke-test Windows Setup. On stable tags, it uploads the verified portable executable, checksum, Setup, and release notes as a GitHub Actions artifact. A separate write-enabled job downloads the artifact by ID, validates it, and publishes its files. It then checks public access to the three downloads and deletes the staging branch if it still points to the verified commit. The updater uses the GitHub digest for the package that matches the installation.

Packaging-only `verify-packaging-vX.Y.Z` tags can verify a candidate from `next`. They leave the staging branch available for publication. Add a numeric suffix such as `-2` to repeat the check. Restore the staging branch first if the release workflow has deleted it.

[Project releases](https://github.com/H4RL33/lg_launcher/releases) · [Updater contract](desktop/updates.md)

## `.github/workflows/publish-release.yml`

Runs Windows packaging and Setup smoke in a read-only job. The tag version must match the Desktop project. Stable tags must point to the first-parent history of `main`. A dependent write-enabled job verifies the artifact handoff, publishes the release, checks the public downloads, and cleans up the staging branch with a lease on its verified commit.

[Stable release workflow](../../.github/workflows/publish-release.yml) · [Stable Windows releases](#stable-windows-releases)

## `.github/workflows/publish-installer.yml`

Attaches Setup to a previously published stable release. Stage `LG-Launcher-Setup-X.Y.Z.exe` and its GNU-format checksum on `installer-assets/vX.Y.Z`, then push `installer-vX.Y.Z` from `main`. The workflow verifies the checksum and attaches Setup to the existing release.

[Historical installer workflow](../../.github/workflows/publish-installer.yml)

## `build.ps1`

[Source](../../build.ps1)

- **Purpose** — Publishes the Windows x64 Avalonia single-file executable and stages it before replacing the normal build artifact. The executable requires the installed x64 .NET 10 Runtime. `-Smoke` runs migration and updater checks against the staged file. `-Test` runs the Windows core fixtures and keeps their outputs under `.build-work/core-tests/`.
- **Depends on** — PowerShell, the .NET 10 SDK, the Desktop and core fixture projects, bundled assets, and the appropriate smoke harnesses.
- **Used by** — Development builds and verification. `build-installer.ps1` and official release staging use its verified executable as an input.

## `build-installer.ps1`

[Source](../../build-installer.ps1)

- **Purpose** — Builds Windows Setup from a particular versioned launcher executable. The optional smoke run gives Setup a unique identity, default folder name, and application mutex. It checks the launcher under the account's Local Programs directory, the user Start menu shortcut, the HKCU uninstall registration, the installed executable hash, the installed update controls, and uninstall. It checks that the fixture library hash survives launch and uninstall.
- **Depends on** — PowerShell, Inno Setup’s `ISCC.exe`, `installer/LGLauncher.iss`, and a built Windows executable. Installer smoke runs under the current user's permissions and writes its log, isolated profile, and captures to `.build-work/installer-smoke/`.
- **Used by** — Local packaging and the stable release repository workflow. The normal setup file is written to `build/`.

## `lint.ps1`

[Source](../../lint.ps1)

- **Purpose** — Provides a single lint command. Checks authored source whitespace, PowerShell syntax, XML markup, and developer documentation before compiling. Builds the Avalonia projects with warnings treated as errors. When given changed C# paths, it verifies their full Roslyn formatting.
- **Depends on** — PowerShell 7, `scripts/Test-DeveloperDocumentation.ps1`, the .NET 10 SDK and package restore, the Avalonia project files, and `.editorconfig`. Lint prefers the SDK in `.build-work/.avalonia-sdk` when present and otherwise uses the first `dotnet` executable on `PATH`.
- **Used by** — Agents and contributors before completing source changes and the GitHub Actions pull-request workflow. It leaves the main release executable and installed profile untouched.

## `scripts/Test-DeveloperDocumentation.ps1`

Checks the repository files discovered by `lint.ps1`. It uses PowerShell's Markdown renderer to resolve local links, image paths, and heading fragments in developer documentation. It requires unique source articles for root scripts and authored files in the documented source trees, including checked-in test fixtures and packaged assets. It checks that UI catalog paths, IDs, cross-references, source names, and style references still resolve. It emits diagnostics for lint to report and runs without network access.

[Documentation checks](../../scripts/Test-DeveloperDocumentation.ps1) · [Documentation maintenance](../README.md#maintain-the-documentation-with-the-code) · [GitHub's Markdown link rules](https://docs.github.com/en/get-started/writing-on-github/getting-started-with-writing-and-formatting-on-github/basic-writing-and-formatting-syntax#section-links)

## `.github/workflows/pull-request-lint.yml`

Checks pull request titles and routing for `dev`, `next`, and `main`, then runs lint and core fixtures on Windows. Pull requests to `next` and `main` and pushes to `next` also run desktop, portable updater, and Setup smoke. On a pull request into `main`, GitHub tests the proposed merge as **Release integration**. The job requires this repository's `next` branch as the source and checks the title against the version in the Desktop project. The `main` ruleset requires this GitHub Actions check. Other runs report **Lint**. New commits cancel earlier runs for the same pull request or push branch.

[Pull-request workflow](../../.github/workflows/pull-request-lint.yml) · [GitHub required status checks](https://docs.github.com/en/pull-requests/reference/status-checks)

## `.github/dependabot.yml`

Checks NuGet project manifests and GitHub Actions weekly and targets version update pull requests at `dev`. The default branch still holds the configuration. GitHub handles security updates against the default branch. Review any resulting security pull request on `main` and carry the selected fix through `dev` and `next`. The graphics group proposes Avalonia and SkiaSharp updates together. For the Expat follow-up, check the SkiaSharp component manifest for the proposed Windows native asset.

[Dependabot configuration](../../.github/dependabot.yml) · [SkiaSharp assessment](desktop/app.md#windows-x64-expat-assessment) · [GitHub version update setup](https://docs.github.com/en/code-security/how-tos/secure-your-supply-chain/secure-your-dependencies/configure-version-updates)

## `.editorconfig`

[Source](../../.editorconfig)

- **Purpose** — Shares indentation, whitespace, and compatible C# layout preferences between editors and the SDK formatter.
- **Depends on** — EditorConfig-aware tools and root-relative file matching.
- **Used by** — `lint.ps1 -ChangedPath` through `dotnet format`, and the IDEs used to edit project files.

## `installer/LGLauncher.iss`

[Source](../../installer/LGLauncher.iss)

- **Purpose** — Installs for the current account without elevation. The default destination is `%LOCALAPPDATA%\Programs\LG Launcher`, the shortcut goes in the current user's Start menu, and uninstall is registered in HKCU. Setup checks for the x64 .NET 10 Runtime and adds a `Scope=User` marker in `LG-Launcher.install.ini` beside the executable. With the same application ID and install scope, upgrades use the existing install directory. Setup and uninstall preserve the separate `%LOCALAPPDATA%\LG Launcher` profile. Inno treats the former machine-wide installation as a separate installation. The [Windows installation instructions](../../README.md#install-on-windows) describe the one-time transition.
- **Depends on** — Inno Setup, preprocessor values from `build-installer.ps1`, the launcher icon, and the Desktop app’s Windows mutex and startup registration conventions.
- **Used by** — The installer builder for local and official Windows releases.

## `avalonia/Directory.Build.props`

[Source](../../avalonia/Directory.Build.props)

- **Purpose** — Sets shared MSBuild defaults for .NET, nullable annotations, implicit usings, and the pinned Avalonia package version.
- **Depends on** — MSBuild’s automatic ancestor property imports and the .NET SDK. Individual projects can override defaults.
- **Used by** — All three Avalonia projects and any build, lint, or gallery command loading them.

## `src/app.manifest`

[Source](../../src/app.manifest)

- **Purpose** — Provides Windows application identity, compatibility, DPI awareness, and an as-invoker execution level.
- **Depends on** — Windows manifest schemas and the compiler/publisher application manifest options.
- **Used by** — `LGLauncher.Desktop.csproj`.

## `tests/LGLauncher.CoreTests.csproj`

[Source](../../tests/LGLauncher.CoreTests.csproj)

- **Purpose** — Builds the Windows core fixture runner as a .NET 10 console application. It links the model and services that the Desktop project uses.
- **Depends on** — The .NET 10 SDK, `CoreTests.cs`, and the linked C# files from `src/`.
- **Used by** — `build.ps1 -Test` and the pull-request workflow.

## `tests/CoreTests.cs`

[Source](../../tests/CoreTests.cs)

- **Purpose** — Exercises the shared code without a UI or a live Steam feed. Fixtures cover discovery, exact Steam matching, metadata and artwork URL parsing, Steam cache replacement and cancellation, collection rules, preference and library round trips, backup recovery and subsequent saves, watcher events, process completion, and the actual priority class of a launched process with the setting both on and off.
- **Depends on** — `Library.cs`, `SteamCatalog.cs`, `GameSessions.cs`, and Windows for the process fixtures.
- **Used by** — `build.ps1 -Test`, which builds and runs the .NET 10 fixture project. Process tests launch the fixture's executable app host and use disposable data beneath the test output.

## `tests/Verify-AvaloniaMigration.ps1`

[Source](../../tests/Verify-AvaloniaMigration.ps1)

- **Purpose** — Drives the Windows launcher with an isolated legacy-format library. Verifies a read-only open, runs the integrated UI/migration suite, captures screenshots, checks what the library saves, and reopens the fixture with software rendering selected. The full migration run allows 120 seconds for timed music and UI checks on Windows CI. A timeout reports the verification mode and the most recent captures. `-ModsOnly` runs the Mods UI fixture alone. The optional existing library input is copied into a test folder first. Reports and screenshots go under `.build-work/smoke/`.
- **Depends on** — A built Avalonia executable, the Desktop verification entrypoints, and sample artwork.
- **Used by** — `build.ps1 -Smoke` and direct smoke runs against a specific portable executable.

## `tests/fixtures/theme-audio/tone.mp3`

Synthetic 220 Hz mono tone for checking MP3 theme import and Windows decoder availability. The migration harness copies it into the fixture profile.

## `tests/fixtures/theme-audio/tone.ogg`

The same generated tone encoded as Ogg Vorbis. It exercises decoding and validation without network or OS codec downloads.

## `tests/fixtures/theme-audio/tone.opus`

The same generated tone encoded as Ogg Opus. It checks that a supplied Opus track can be converted into a playable profile asset.

## `tests/Verify-AvaloniaUpdate.ps1`

[Source](../../tests/Verify-AvaloniaUpdate.ps1)

- **Purpose** — Tests the in-app update worker’s executable replacement, rollback, and restart acknowledgement with disposable executable copies. Rollback cases cover early exit, another-instance activation, and a running process that never confirms readiness. A restart without an outcome message also checks cleanup.
- **Depends on** — A single-file Avalonia executable exposing the verification-only CLI, `UpdateInstallVerification.cs`, and `UpdateInstaller.cs`. Uses a fixture package and temporary paths.
- **Used by** — `build.ps1 -Smoke` and direct smoke runs against release candidates.

## `tests/Verify-AvaloniaSetupUpdate.ps1`

[Source](../../tests/Verify-AvaloniaSetupUpdate.ps1)

- **Purpose** — Builds and installs two versions under a unique per-user Setup identity. It checks that a copied marker cannot apply an upgrade, then runs the real Setup update worker from the registered custom directory and checks the installed hash, registration, restart, and staging cleanup. It uninstalls the fixture and stores outputs under `.build-work/setup-update-smoke/`.
- **Depends on** — Windows, the x64 .NET 10 SDK and runtime, Inno Setup, the Desktop project's `SetupFixtureAppId` build property, and `UpdateInstallVerification.cs`.
- **Used by** — Direct verification when changing the installed update or setup handoff.

## `tests/Verify-AvaloniaMods.ps1`

Runs catalog, mod package, deployment, and recovery checks under `.build-work/mods-smoke/`. The default run uses synthetic fixtures, including the RenoDX index and release metadata and a test x64 DLL. Optional `-Archives` points to a directory containing the fixed official assets listed in the [reviewed package table](desktop/mods.md#avalonialglauncherdesktopmodsmodpackagescs) under their original filenames. Supply both the full add-on `ReShade_Setup_6.8.0_Addon.exe` and the standard `ReShade_Setup_6.8.0.exe` to exercise the upgrade. Optional `-Catalog` checks a generated OptiScaler wiki catalog through the launcher's downloader and cache. Both inputs can be used in the same run.

The selected executable supplies the embedded fixtures. Any failed fixture fails the script, with the launcher report included in the error. The published v0.1.17 executable embeds an older DXGI spoofing fixture that can encounter a sharing violation after closing its disposable game process.

[Mod harness](../../tests/Verify-AvaloniaMods.ps1)

## `scripts/import_optiscaler_catalog.py`

Reads the committed tables from a local checkout of the official OptiScaler wiki and produces versioned advisory JSON. With `--previous`, it preserves the published file when the entries are unchanged and rejects large unexpected losses. To check upstream data locally, clone the wiki into `.build-work/optiscaler-wiki`, then run `python -B scripts/import_optiscaler_catalog.py --wiki-dir .build-work/optiscaler-wiki --output .build-work/optiscaler-v1.json`.

[Importer](../../scripts/import_optiscaler_catalog.py) · [OptiScaler wiki repository](https://github.com/optiscaler/OptiScaler.wiki.git)

## `tests/test_import_optiscaler_catalog.py`

Exercises the Markdown importer and its publication guards with checked-in sample tables and in-memory catalog documents. The workflow runs these Python tests before fetching upstream content.

[Importer tests](../../tests/test_import_optiscaler_catalog.py)

## `tests/fixtures/optiscaler-compatibility.md`

Provides representative wiki tables for the importer tests, including entries and table structures used to exercise parsing and validation.

[Markdown fixture](../../tests/fixtures/optiscaler-compatibility.md)

## `.github/workflows/optiscaler-catalog.yml`

Runs in the public LG Launcher repository on `main`. The job runs at 04:37 UTC daily, on changes to the publisher or its fixtures, and when dispatched manually. It runs the tests and fetches the wiki on each run. It publishes changed entries as `optiscaler-v1.json` on `mods-support/compatibility-catalog`. When the entries are unchanged, it normally exits without a commit and makes an empty keepalive commit if the branch's last commit is at least 30 days old. A failed import or validation leaves the previous catalog available.

[Workflow](../../.github/workflows/optiscaler-catalog.yml) · [Launcher catalog cache](desktop/mods.md#avalonialglauncherdesktopmodsmodcatalogstorecs)

## `assets/welcome.jpg`

[Source](../../assets/welcome.jpg)

- **Purpose** — Supplies the 1200 × 800 branded splash and preview artwork and a deterministic image fixture for desktop checks. The flat frosted-glass controller sits on the right so the splash can place its title and status over the darker areas.
- **Depends on** — Avalonia's JPEG decoding and the embedded-resource names in the project files.
- **Used by** — The Desktop splash, Gallery preview, migration fixtures, and RSS image fixtures.

## `assets/launcher.ico`

[Source](../../assets/launcher.ico)

- **Purpose** — Supplies the Windows application and setup icon. It uses the controller cropped from `assets/welcome.jpg`, centered on a rounded navy tile with transparent outer corners. The ICO contains ten 32-bit sizes from 16 × 16 to 256 × 256 pixels.
- **Depends on** — Windows ICO support and build-time icon embedding.
- **Used by** — The Desktop executable and window and `installer/LGLauncher.iss`.

## `.gitignore`

[Source](../../.gitignore)

- **Purpose** — Keeps build products, intermediate files, and scratch release work out of the source tree’s tracked changes.
- **Depends on** — Build and test outputs following the documented directory conventions.
- **Used by** — Git when the source workspace is a checkout.
