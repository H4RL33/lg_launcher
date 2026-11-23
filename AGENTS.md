# Agent instructions for LG Launcher

LG Launcher is a desktop library for locally installed games. The Windows product is the .NET 10 Avalonia app in `avalonia/LGLauncher.Desktop/`. The shared model and services live in `src/` and are tested through the .NET 10 core fixture project. Preserve compatibility with existing saved profiles. The standalone `avalonia/LGLauncher.Gallery/` project uses sample data to preview `avalonia/LGLauncher.Components/`.

## Before changing files

1. Read the [developer documentation entrypoint](docs/README.md). Follow its [source map](docs/source/README.md) and read the articles for the source files your task touches. Follow dependencies, callers, and tests that your changes could affect.
2. Read and follow the [project coding style](docs/coding-style.md) throughout the task.
3. Keep the working library and saved artwork safe. Use the test harnesses or copied fixture data for verification. Keep intermediate files under `.build-work/`. Use the normal build command when an updated Windows executable is requested, and publish or modify remote release state only with the user’s authorization.

## Branches and change descriptions

1. Follow the [development and release workflow](docs/workflow.md). Base ordinary work on `dev`, target `dev` for its pull request, and mark the change unslated unless the maintainer selects it.
2. Promote selected commits to `next` for integration. Release through a versioned `next` → `main` pull request and tag the resulting `main` commit only with release authorization.
3. Use the commit and pull request templates in the workflow guide and record the verification actually run.

## Before doing UI work

1. Read the [UI catalog guide](docs/ui/README.md) and search the [machine-readable catalog](docs/ui/catalog.json). Inspect the source entries for the controls and styles you plan to use.
2. Reuse the catalogued components, widgets, styles, and tokens where they fit. If a required component or widget is missing, prompt the user to choose one of these options:
   1. Create the missing element in line with the existing interface designs.
   2. Wait for further instructions.
3. Wait for that decision before implementing work that needs the missing element. Continue independent work within the authorized scope.

## Verify UI changes visually

1. Build and launch the current development version with a fixture library or sample data. Capture screenshots or use computer-use tools to inspect the running UI. Keep any screenshots in `.build-work/` and review them before reporting completion.
2. Inspect the affected views at a narrow supported width, at a typical size, and at a wider size. Check relevant interactive states and overflow, including how controls move or wrap as the window changes.
3. Compare reused elements with their existing counterparts in the app. Check common styling, font family, size and weight, and any misaligned or uneven padding and gaps. In menus, check the inset on each side and between the top or bottom edge and the first or last item.
4. Check that blank space groups related controls and gives text and artwork room to breathe. Fix visual defects and repeat the checks. Report the sizes and states you inspected and reference the captures when available.

## Before finishing

1. **Keeping developer documentation current is required for every task.** Review the affected documentation against the final source, UI, configuration, workflows, and tests. Update it in the same change whenever behaviour, interfaces, paths, dependencies, commands, verification, or packaging change. Add articles for new source files, update the source map when navigation changes, and remove or repair obsolete references. Keep the UI catalog and relevant README instructions in sync. Use the [documentation maintenance checklist](docs/README.md#maintain-the-documentation-with-the-code).
2. Run `pwsh -NoProfile -File ./lint.ps1` from the project root. When you changed or added C# files, pass their paths to `-ChangedPath` from PowerShell for the full formatter check. Rectify all reported issues and rerun until the linter passes.
3. Run the relevant [build and test checks](docs/source/build-and-tests.md) for the behavior or packaging you touched. Report the verification result and any checks a genuine environment blocker prevented.
