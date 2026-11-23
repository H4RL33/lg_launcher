# OptiScaler mod management implementation plan

> Historical implementation plan. See the [current mod management reference](../../source/desktop/mods.md) for shipped behaviour.

> **For agentic workers** Use `superpowers:subagent-driven-development` or `superpowers:executing-plans` to implement this plan task by task. Checkboxes track the steps.

**Goal** Add verified and guided OptiScaler installation to LG Launcher, with reusable OptiScaler, REFramework, and Special K packages and recoverable per-game removal.

**Architecture** Desktop services acquire packages, resolve a reviewed file plan, and commit or undo it using a durable journal. The Settings and game detail views use the same services. A recorded installation can be inspected and removed independently of the package cache and library entries.

**Tech stack** .NET 10, Avalonia 12, Windows x64, ZIP and 7z readers, JSON manifests, SHA-256, and the existing PowerShell smoke harnesses.

**Spec** [Approved design](../specs/2026-11-20-optiscaler-mod-management-design.md)

## Global constraints

- Target Windows x64 local game folders. Keep the new services in Desktop and preserve the legacy compiler and library serialization contracts.
- Use official upstream packages, fixed providers, verified digests, and reviewed layouts. Read the wiki as compatibility evidence. Take file instructions from reviewed recipes and supported guided choices.
- Keep original files and records independent of cached packages. Use the same preview and transaction for automatic and guided installs. Known online-only, anti-cheat, and incompatible titles cannot deploy through either flow.
- Use isolated verification profiles and game folders beneath `.build-work/`. Follow `AGENTS.md` for source docs, catalog reuse, UI gaps, captures, and lint. With a Git checkout, commit passing tasks individually. The current workspace has no `.git` metadata.

## File map

| Location | Responsibility |
| --- | --- |
| `avalonia/LGLauncher.Desktop/Mods/ModPackages.cs`, `ModArchiveReader.cs` | Provider metadata, package identity, validated extraction and immutable store |
| `avalonia/LGLauncher.Desktop/Mods/ModPaths.cs`, `ModInstallState.cs`, `ModTransactions.cs` | Windows path identity, manifests and journals, install and removal |
| `avalonia/LGLauncher.Desktop/Mods/ModCompatibility.cs`, `ModInstallPlanner.cs`, `mod-catalog.json` | Advisory snapshot, verified recipes, installation eligibility and file plans |
| `Launcher.Mods.cs`, `Launcher.axaml`, `Launcher.axaml.cs` under `avalonia/LGLauncher.Desktop/` | Settings and game details, lifecycle, action and selection state |
| `avalonia/LGLauncher.Desktop/Mods/ModVerification.cs`, `Launcher.Mods.Verification.cs`, `Program.cs`, `Launcher.Verification.cs` under Desktop | Fixture service cases and integrated Avalonia checks |
| `tests/Verify-AvaloniaMods.ps1`, `docs/source/desktop/mods.md` | Fixture entry point and source reference with recipe verification records |

## Review focus

- Interrupted uninstall after cache deletion restores the exact installed bytes and leaves its committed record open (task 3).
- A game update leaving the mod DLL in place still blocks restoring a stale original (task 3).
- A redirected or changed Special K archive leaves the previous approved copy available (task 1).
- A nested alias or junction cannot write through an install root or let two entries race (task 2).
- A game selection change during acquisition never applies the completed download to the new selection (task 5).

## Fixture commands

Run from the repository root. This publishes the same Windows single-file format as the normal build into a disposable staging folder.

```powershell
dotnet publish avalonia/LGLauncher.Desktop/LGLauncher.Desktop.csproj -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=embedded -o .build-work/mods-candidate
pwsh -NoProfile -File ./tests/Verify-AvaloniaMods.ps1 -Launcher './.build-work/mods-candidate/LG Launcher.exe'
```

The harness fails on a nonzero exit, a missing case report, or a failed case. For the UI and updater checks in tasks 5 and 6, pass the same `-Launcher` path to `tests/Verify-AvaloniaMigration.ps1` and `tests/Verify-AvaloniaUpdate.ps1`.

## Task 1. Verify and cache official packages

**Files** Create `Mods/ModPackages.cs`, `Mods/ModArchiveReader.cs`, and `Mods/ModVerification.cs` in Desktop, `tests/Verify-AvaloniaMods.ps1`, and `docs/source/desktop/mods.md`. Modify Desktop `Program.cs` and `LGLauncher.Desktop.csproj`.

**Produces** `ModPackageStore.CheckAsync(ModProviderId, CancellationToken) → Task<ModPackageCheck>`, `AcquireAsync(ModPackageDescriptor, CancellationToken) → Task<CachedModPackage>`, `List(ModProviderId) → IReadOnlyList<CachedModPackage>`, and `Delete(ModPackageKey)`. `ModPackageKey` holds provider, variant, version, and digest. `CachedModPackage` exposes validated entries for a plan, with a read lease during staging.

- [ ] Add fixture cases `RejectsUntrustedPackage`, `RejectsArchiveEscapeAndCollisions`, and `RetainsPreviousPackageAfterFailure`. Assert that a changed Special K digest, unexpected redirect, truncated ZIP/7z, cancellation, and interruption before publication leave the previous copy readable. Exercise oversized extraction, reparse entries, and two entries that resolve to the same Windows name.
- [ ] Wire `--verify-mods <fixture-directory>` to the fixture runner before normal profile or mutex setup. The PowerShell harness creates its own disposable absolute directory below `.build-work/`, launches the candidate, checks its exit status, and reads the case report. Run it and observe the missing package behavior fail in the new cases.
- [ ] Implement the provider and store contracts. Pin an approved OptiScaler v0.9.5 `.7z` layout and an identified REFramework non-VR ZIP. Distinguish unified, per-game, and legacy REFramework assets at selection. Use the official Special K local archive with a reviewed digest. Show an unverified update without replacing the cached version. Publish only after validation and serialize acquisition and deletion against active leases.
- [ ] Verify the managed 7z reader against the official archive and a Windows single-file candidate. Inspect known payload members without executing upstream installers or scripts. Record package versions, asset names, expected contents, digest provenance, and any provider that remains unavailable in `docs/source/desktop/mods.md`. Run the fixture harness and require all package cases to pass.

## Task 2. Commit installs and recover interruptions

**Files** Create Desktop `Mods/ModPaths.cs`, `Mods/ModInstallState.cs`, and `Mods/ModTransactions.cs`. Extend `Mods/ModVerification.cs`.

**Consumes** `CachedModPackage` and its entry lease. **Produces** `ModInstallPlan(GameInstallTarget Target, IReadOnlyList<ModFileChange> Changes, IReadOnlyList<ModPackageKey> Packages)`, `ModTransactions.InstallAsync(ModInstallPlan, CancellationToken) → Task<ModOperationResult>` and `RecoverAsync(CancellationToken) → Task<IReadOnlyList<ModOperationResult>>`. `GameInstallTarget` includes the validated canonical root, rendering executable, and observed build identity. `ModFileChange` carries a relative destination, staged payload source or generated bytes, expected preimage, and owner.

- [ ] Add `RecoversAtEveryInstallBoundary`, `BlocksNestedReparseAndDuplicateRoot`, and `PreservesChangesAfterPreview`. Interrupt before and after each mutation and before and after commit publication, then restart from the same files and journal. Assert untouched third-party files, preserved original metadata, root alias serialization, and no writes after preflight fails. Include access denied, a locked destination, insufficient space, and an unavailable drive. Run the fixture harness and observe the transaction cases fail.
- [ ] Implement target identity and containment. Validate a local Windows root, expected PE architecture, each existing ancestor, and each relative destination. Reject reparse points, Windows path aliases, and unsupported volume paths. Acquire an exclusive operation lease by canonical root and use the lease for play/mutation coordination. Check running-process state and open file access immediately before writes.
- [ ] Implement install and startup recovery. Stage on the target volume, preserve originals and metadata, flush the journal before mutation, and publish the verified manifest with the transaction ID as the commit point. Before that point recover by comparing each file with its expected before and after states. A divergent file or unreadable backup keeps the journal and enters recovery review. A matching commit only requires cleanup. Permit cancellation at safe boundaries.
- [ ] Run the fixture harness. Require byte and metadata comparisons for committed, rolled-back, and interrupted installs and verify no writes occur on a rejected plan.

## Task 3. Inspect, uninstall, and retain recovery records

**Files** Extend Desktop `Mods/ModInstallState.cs`, `Mods/ModTransactions.cs`, and `Mods/ModVerification.cs`.

**Consumes** the committed manifest and transaction primitives from task 2. **Produces** `ModTransactions.InspectAsync(GameInstallTarget, CancellationToken) → Task<ModInstallInspection>`, `UninstallAsync(ModRemovalPlan, CancellationToken) → Task<ModOperationResult>`, `FindInstallations() → IReadOnlyList<ModInstallationRecord>`, and `RelinkAsync(Guid installationId, GameInstallTarget, CancellationToken) → Task<ModOperationResult>`. Inspection yields the expected and observed managed files, recognised external loaders, and a proposed removal plan. An installation can be found by root even after library edits or removal.

- [ ] Add `UninstallsAfterCacheDeletion`, `RecoversInterruptedRemovalWithoutCache`, `RefusesStaleOriginalAfterGameUpdate`, and `PreservesEditedConfigAndForeignFiles`. Assert already-restored originals and absent added files are accepted, changed or unrecognised files are preserved, and missing or corrupt backups cannot trigger restoration. Test a disconnected root and a relink candidate that fails identity or file comparison. Run and observe the new cases fail.
- [ ] Implement read-only inspection, removal preview, and persisted lookup for orphaned installs. Require the recorded game build before restoring vendor files. Expose manual review plus safe removal of independently owned additions while retaining unresolved records. Archive a launcher-created edited INI only after explicit confirmation.
- [ ] Implement the uninstall journal using snapshots of the current deployed files. Publish a transaction-stamped removal record before clearing the committed install. Remove only empty directories the launcher created. Relink only when the new root verifies against the persisted game identity and managed files.
- [ ] Run the harness with the package cache absent for the removal cases. Require the same byte and metadata verification used for install.

## Task 4. Resolve compatibility and both setup flows

**Files** Create Desktop `Mods/ModCompatibility.cs`, `Mods/ModInstallPlanner.cs`, and `Mods/mod-catalog.json`. Modify Desktop `LGLauncher.Desktop.csproj` to embed the catalog and extend `Mods/ModVerification.cs`.

**Consumes** the approved packages and `GameInstallTarget`. **Produces** `ModCompatibility.AssessAsync(Game, CancellationToken) → Task<ModAssessment>` and `ModInstallPlanner.PreviewAsync(ModSetupRequest, CancellationToken) → Task<ModPlanPreview>`. A preview carries status, chosen target, required or missing packages, conflicts, actions, and an immutable plan when eligible.

- [ ] Add `RequiresVerifiedLocalIdentity`, `RejectsBlockedGamesInGuidedSetup`, and `PlansExplicitDependencyStack`. Test a title collision, edited Steam ID, changed game build, launcher stub, unlisted game, dependency without an approved recipe, occupied loader, and unsupported package layout. Test the advisory snapshot against a saved upstream revision with a positive, negative, and third-party dependency case. Assert the same deployment rules hold before final commit. Run and observe the preview cases fail.
- [ ] Load an advisory snapshot with source revisions and reviewed compatibility records separately from executable recipes. Implement candidate matching and build verification. For guided setup require the confirmed root, rendering `.exe`, approved package, supported loader, and offline-use confirmation. Apply known incompatibility and dependency blocks before exposing deployment. For verified stacks resolve all packages, INI values, and destinations into one plan. Require a fresh preview for an alternate loader or a newly acquired dependency.
- [ ] Run the fixture harness offline. Verify that fixture recipes can exercise single, nested, and multi-mod layouts without treating upstream notes or a positive title match as deployment instructions. Include packaged and foreign loaders with the same filename.

## Task 5. Integrate Settings and game details

**Files** Create Desktop `Launcher.Mods.cs` and `Launcher.Mods.Verification.cs`. Modify Desktop `Launcher.axaml`, `Launcher.axaml.cs`, `Launcher.Verification.cs`, and `Launcher.Updates.cs` where needed to coordinate close and update handoff. Update `docs/ui/catalog.json` and the source articles for each changed entry point.

**Consumes** all four service contracts. **Produces** the `Page.Mods` route, game details status and actions, and the existing shell’s launch/mutation interlock.

- [ ] Add `VerifyMods(preview)` to the existing migration smoke flow. Use injected package and compatibility fixtures. Assert settings actions and navigation, verified and guided previews, external conflict notices, installed and recovery states, and wrong-selection protection while a download is pending. Verify switching away and back restores the right package state. Observe the new checks fail with the route or controls absent.
- [ ] Compose the Mods page and details actions from `SettingsRow`, `Section`, `Actions`, `ShowModal`, and the existing storage pickers. Include package availability, check/download/delete, in-context acquisition, preview, local inspection, uninstall, and recovery. Use operation identity for completion feedback after navigation. Gate launcher play by install root while mutation or recovery is pending, and defer application update handoff during writes. Use `AGENTS.md` if a required UI component is missing.
- [ ] Run the fixture-based migration smoke. Capture Mods settings and game details at `730 × 510`, `1230 × 805`, and `1600 × 900` at default scale and repeat the narrow case with a larger interface scale. Check keyboard/focus, text and control wrapping, spacing, and contrast over artwork. Keep captures beneath `.build-work/` and fix defects before moving on.

## Task 6. Validate pilot recipes and the Windows candidate

**Files** Update Desktop `Mods/mod-catalog.json`, `docs/source/desktop/mods.md`, `docs/source/desktop/README.md`, and `docs/source/build-and-tests.md`.

- [ ] Validate clean game copies for a straightforward OptiScaler install, a nested executable, a permitted offline REFramework stack, and one local Special K combination. Cyberpunk 2077 and Clair Obscur are the first two candidates. Record store/build, game and package hashes, GPU/API and mode, remaining user steps, and the source revision. Verify boot, overlay and intended upscaler, then uninstall, compare files, and boot again. Only enable the matching recipes after they pass. Keep other entries as guidance.
- [ ] Verify Special K’s approved package acquisition from an empty cache. A replaced rolling archive without a matching published or reviewed digest leaves that package and dependent automation gated until verified. Record any unavailable real-game fixture or provider in the result.
- [ ] In PowerShell run `./lint.ps1 -ChangedPath $changedCSharp`, passing the actual new and edited C# paths. Require `Lint checks passed.` Run `pwsh -NoProfile -File ./build.ps1 -Test` for shared-profile compatibility and require a successful exit. Run the mod harness and both existing desktop smoke scripts against the staged single-file candidate. Require successful exits and review the screenshots. Use the normal `pwsh -NoProfile -File ./build.ps1 -Smoke` when delivering an updated `build/LG Launcher.exe`. Keep publication and remote release state subject to explicit authorization.
