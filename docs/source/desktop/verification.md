# Desktop verification

[Desktop source index](README.md)

The launcher executable includes isolated verification entry points so checks can exercise real Avalonia controls and the Windows update handoff against disposable data. The PowerShell scripts create or copy the fixtures, run the executable, and inspect the results. Keep added checks independent of live Steam or RSS servers and of the user's real profile.

## `avalonia/LGLauncher.Desktop/Launcher.Verification.cs`

Implements the main desktop smoke check. It covers saved library data, startup, shell status, Home, collections, search and history, artwork framing and glass, menus, and major navigation paths. It runs the more focused verification partials and provides helpers for assertions, asynchronous waits, and screenshots.

The navigation check verifies eviction of the oldest route at the 64-entry limit and Back and Forward after eviction. The genre fixture persists two Steam App IDs together and verifies that manual genres and a rematched game survive the batch. Search covers a query with terms split between the title and genres.

The collection sidebar check opens a collection from its row, compares the game and collection row sizes, checks the miniature symbols, and verifies the sort picker's frosted menu. At 730, 1230, and 1600 dips wide it captures the sidebar, popup, and sorted tile during and after movement. It tracks the remaining translation through animation cleanup to catch any jump toward the original slot. The same widths exercise and capture the immediate reorder with motion disabled.

The Home fixture checks that Collections hides its scrollbar and retains animated shelf arrows and horizontal wheel input. It captures the overflowing shelves across narrow, typical, and wide windows.

The App Behaviour fixture checks the rendering restart prompt in both directions, deferred restart, restart blocked during gameplay, the saved preference after reopening, and the software compositor. It checks that the Set process priority switch starts off and saves immediately in both directions, and captures Performance at three widths. It also verifies that the reset row dims while both prompts are pending and exercises the same prompt sequence used at startup.

The Setup fixture opens an isolated profile with a saved automatic-update opt-in. At 730, 1230, and 1600 pixels wide it checks the enabled switch and **Install update** action for per-user installs, or the release-page action for machine-wide installs. It verifies the release-page fallback when only a portable package is available or the Setup digest is invalid. Captures are saved alongside the installer smoke preview.

The game-return fixture checks the first contribution prompt, an ordinary return, the return at 50, deferral behind a modal, and the saved opt-out. It captures the prompt and preference at three window sizes.

- **Depends on** the actual `Launcher` window and named controls, Avalonia layout/rendering and simulated input, shared UI components, and the isolated profile and artwork fixtures.
- **Used by** `App.axaml.cs` through the `--verify-migration`, `--verify-existing`, and `--verify-setup` arguments. The other launcher verification partials use its helpers.

[Main smoke source](../../../avalonia/LGLauncher.Desktop/Launcher.Verification.cs) · [Migration harness](../../../tests/Verify-AvaloniaMigration.ps1)

## `avalonia/LGLauncher.Desktop/Launcher.Mods.Verification.cs`

Exercises the wrench in Settings and game details, the package info and refresh buttons, and the Mods list in the migration fixture. It captures settings, game details, active downloads, preview preparation, the game file preview, and inline DXGI spoofing at narrow, typical, and wider sizes. It checks the prefilled request link and Mods-only scope note, continuity and cancellation during preview preparation, the shared DXGI picker, and an in-place edit on a managed installation. It also verifies the management action order, read-only installation verification, affected-file indicators, and the shared hotkey warning on the Mods list. Run the migration harness with `-ModsOnly` for this slice. The mod harness exercises the catalog, streamed package downloads, update checks, and transactions. [Mod harness](../../../tests/Verify-AvaloniaMods.ps1)

The Settings fixture checks the combined inline update result, the separate package and managed-installation panels, and shared sidebar row heights. The game fixture checks that the management dialog opens while an inspection is pending, that each installed mod can be inspected separately, and that removing a dependency requires confirmation.

The Mods fixture verifies the warning count, intermediate clipping during reveal and collapse, and immediate changes with Reduced Motion enabled. It checks the chevron hover scope, insets, and radius, and captures collapsed, hovered, and expanded states at three widths. It also checks that the symbolic package download and cache-delete buttons fit with the other actions. It checks the deletion confirmation and that removing the cached package preserves the game installation.

The ReShade cache fixture seeds three downloads, including two variants of the same version. It checks and captures the single trash action and its shared version menu at 730, 1230, and 1600 pixels wide. It also checks confirmation, cancellation, and that deleting one download keeps the other versions and variants.

The ReShade dialog fixture checks the Settings entry, rendering executable, shared graphics API picker, Vulkan guidance, preview action, and return to the game Mods list. It also checks the full add-on upgrade action and confirmation. With `-Archives`, the package fixture verifies both official ReShade editions and checks registration, reuse, upgrade, removal, and coexistence using an isolated registry substitute.

The ReShade coexistence preview fixture checks and captures the blue **Modified** row for an edited `OptiScaler.ini` beside the green **Add** row for the ReShade DLL at 730, 1230, and 1600 pixels wide.

[Mods UI smoke source](../../../avalonia/LGLauncher.Desktop/Launcher.Mods.Verification.cs) · [Fixture options](../build-and-tests.md#testsverify-avaloniamodsps1)

## `avalonia/LGLauncher.Desktop/Launcher.Settings.Verification.cs`

Checks that preferences apply and persist, that older scale values map to the dropdown, and that feature overrides and disabled fades respect Reduce Motion. It also checks artwork controls, Library preferences, and the About page. Injected GitHub responses exercise release-by-tag selection, the unavailable and loading states, native formatting, and selection between separate installed and available notes. The fixture verifies that the header link follows the selected release and that the accent underline animates only when motion is enabled. It captures About at narrow, typical, and wider sizes.

The Library settings fixture checks that Theme music starts off and saves both switch states. It captures the enabled setting at 730, 1230, and 1600 pixels wide.

The artwork accent fixture checks the default-on switch, saved opt-out, distinct dominant colours, light and dark foreground contrast, and the purple fallback on Home and for games with no usable artwork colour. It checks that switching games holds the previous accent while artwork loads and that leaving game details restores purple immediately. It captures Appearance and Game Details at three widths and verifies the selected Library row keeps its red tint and text contrast with either translucent or solid chrome.

The fixture samples intermediate palette frames at three widths and checks that the Play button and discovery badge use the same fill and contrast foreground on each frame.

The default accent fixture captures the colour picker at three widths and exercises invalid hex, cancellation, saving a custom colour, and restoring the original purple palette. It checks the splash, switch, and text selection colours, then verifies that Home and artwork fallbacks use the saved custom colour, that artwork can override it, and that disabling artwork accents restores it on Game Details.

The Appearance fixture opens all three pickers and checks the shared frosted backdrop and inset, selected option, font search and saved values, reveal and dismissal, and the opaque and reduced-motion settings. A wider font menu exercises right alignment at the window edge. The custom scale dialog check covers the preset boundaries, invalid typed values, saving an intermediate percentage, and cancellation. The About fixture checks regular body weight and semibold headings with and without release tabs.

The accent picker fixture checks equal treatment of the default purple swatch and a square preview matched to the hex field at each window width.

- **Depends on** the desktop settings builders, isolation and screenshot helpers, real profile persistence, artwork state, and shared theme resources.
- **Used by** `Launcher.Verification.cs` during the migration smoke pass.

[Settings smoke source](../../../avalonia/LGLauncher.Desktop/Launcher.Settings.Verification.cs) · [Preferences](settings.md)

## `avalonia/LGLauncher.Desktop/Launcher.Verification.Modals.cs`

Exercises Edit game against an injected Steam genre lookup. It checks that the modal blocks the shell, fields are centred, invalid ID input leaves the genre value alone, and a successful lookup updates form fields before confirmation. It verifies nested focus and cancellation behaviour. The automatic-update prompt fixture verifies both choices, safe dismissal, and persistence, and captures the dialog at narrow, typical, and wider sizes.

The fixture opens Manage metadata from Edit game and captures its four file controls at 730, 1230, and 1600 pixels wide. At each width it checks the game title’s alignment with the modal heading and the vertical centring of the recommendations beside their field labels. It checks that pending Edit game fields survive dismissal.

The game save fixture locks the profile’s temporary save file to exercise a failed Add game and Edit game. It checks the draft, the stored and in-memory library, discovery and Steam retry state, and the successful Edit game retry after release of the lock. It captures the error at all three widths.

- **Depends on** `ShowGameEditor`, `Confirm`, the shared modal host, test event dispatch, and the fixture game and preview helpers.
- **Used by** the main migration verifier to protect the modal and game editor flow.

[Modal smoke source](../../../avalonia/LGLauncher.Desktop/Launcher.Verification.Modals.cs) · [Dialogs](settings.md)

## `avalonia/LGLauncher.Desktop/Launcher.Verification.CollectionRules.cs`

Drives the collection editor from old and new data. It verifies migration of a legacy genre filter, the narrow editor layout, validation, editing and removal of rule rows, and saved rule fields and match mode. It removes the temporary collection after exercising it.

The rule suggestion check covers case-insensitive deduplication, fuzzy matching with typos, keyboard selection and dismissal, and animated resizing. It captures the search results at three window widths.

The editor fixture opens its three shared pickers, verifies that their menus align to the left edge of the buttons, and checks that the full Steam tag label fits at narrow and regular sizes.

- **Depends on** `EditCollection`, the modal host and its named controls, profile persistence and rule data, and shared verifier helpers.
- **Used by** the main migration verifier to guard collection edit and persistence behaviour.

[Collection rule smoke source](../../../avalonia/LGLauncher.Desktop/Launcher.Verification.CollectionRules.cs) · [Collections](library.md)

## `avalonia/LGLauncher.Desktop/Launcher.Verification.CollectionIcons.cs`

Parses the pinned Lucide geometries and checks rule-derived and fallback icons. It opens the picker from a realized sidebar row, verifies virtualized rendering and search results, and checks that saving and resetting the override update the isolated profile. It verifies vertical-only overflow and captures the picker at widths 730, 1230, and 1600 and on a compact viewport at 200% interface scale.

- **Depends on** the collection icon resolver, sidebar row and context menu, shared Lucide data and modal controls, and migration fixture.
- **Used by** `Launcher.Verification.cs` alongside the collection card and sidebar checks.

[Collection icon smoke source](../../../avalonia/LGLauncher.Desktop/Launcher.Verification.CollectionIcons.cs) · [Collections](library.md)

## `avalonia/LGLauncher.Desktop/Launcher.Verification.GameMetadata.cs`

Uses a generated logo and a test game to verify cache reuse, title fallback, metadata refresh controls, complete genres, and tag layout and visibility. It checks the Steam App ID placement and captures matched and unmatched game details at narrow, typical, and wider sizes.

Generated artwork checks verify height-limited thumbnails, the resolution of a 2:3 portrait, and rejection of a source over the dimension limit. The Steam cache validator accepts the generated image and rejects truncated and oversized inputs. An isolated shared-header fixture cancels the Game Details wait while a card is waiting and verifies that the card receives the image.

The import fixture uses generated images and WAV, MP3, Ogg Vorbis, and Opus files. It checks the recommended image sizes, filename persistence after reopening, that compressed imports preserve the track length, and that a failed import removes staged files from read-only sources. The unmatched game’s custom portrait refreshes both manual and dynamic collection mosaics on Home. Clearing the portrait restores their normal fallbacks. A local Steam album fixture checks duplicate library paths and exact and ambiguous track matches.

The theme playback fixture uses a silent WAV to check the selection delay, intermediate fade volumes in both directions, and cancellation when the user skips a game or playback stops before the delay elapses.

- **Depends on** `Launcher.GameLogos.cs`, genre and tag helpers, the game detail controls and menu builder, profile fixtures, SkiaSharp image creation, and verifier helpers.
- **Used by** the main migration verifier to protect metadata rendering and the refresh affordance.

[Game metadata smoke source](../../../avalonia/LGLauncher.Desktop/Launcher.Verification.GameMetadata.cs) · [Steam and artwork](steam.md)

## `avalonia/LGLauncher.Desktop/Launcher.Rss.Verification.cs`

Tests RSS settings, file and OPML import, enablement, saved refresh interval, offline reuse, the Home shelf, article history, rendering, and image viewport loading. Local data and injected HTTP responses exercise Atom and RSS parsing, conditional requests, restricted web links, and bounded markup.

The image fixture opens game details from a scrolled article, checks that the decoded image detaches, then returns to the article through history and captures it at narrow, typical, and wider widths.

The Home shelf fixture checks its two empty messages, the settings link, the shared refresh request, and header alignment at narrow, typical, and wider window widths.

The RSS interval field verifies matching horizontal and vertical step-button insets on either side, including on a taller field, and captures the hover appearance.

- **Depends on** the RSS UI and navigation, `RssReader`, `RssMarkup`, `RssArticleImage`, fixture storage, shared menu theme and verifier helpers, and the bundled artwork used by the image fixture.
- **Used by** `Launcher.Verification.cs` in the desktop migration smoke check.

[RSS smoke source](../../../avalonia/LGLauncher.Desktop/Launcher.Rss.Verification.cs) · [RSS and Atom](rss.md)

## `avalonia/LGLauncher.Desktop/UpdateInstallVerification.cs`

Adapts the real updater for the smoke harness. It reads a fixture from the isolated executable directory through an injected HTTP handler, verifies rejection of the wrong checksum, starts the update worker, and records the new launch. Portable cases exercise rollback after early exit, another-instance activation, and readiness timeout. A separate case verifies restart when the outcome message cannot be written. The custom-directory Setup harness supplies a unique app ID at build time and uses the same worker and readiness path.

- **Depends on** `Program.cs` verification and restart arguments, `UpdateInstaller` and `UpdateFeed`, .NET hashing and process APIs, and the PowerShell harness's reserved test executable and report filenames.
- **Used by** the `--verify-update-install` and test restart branches and by both updater harnesses.

[Update smoke source](../../../avalonia/LGLauncher.Desktop/UpdateInstallVerification.cs) · [Portable harness](../../../tests/Verify-AvaloniaUpdate.ps1) · [Setup harness](../../../tests/Verify-AvaloniaSetupUpdate.ps1) · [Official updates](updates.md)
