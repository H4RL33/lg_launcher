# LG Launcher coding style

[Developer documentation](README.md) · [Source map](source/README.md) · [UI catalog](ui/README.md)

Write code so a later maintainer can follow the data, state changes, and failure paths on the first read. Match the surrounding file when the project has more than one established style.

## Structure and naming

- Use names that describe the domain and distinguish similar concepts, such as a Steam App ID, a launcher game ID, and an artwork cache key. Use PascalCase for types and members and camelCase for locals and parameters. Use the containing file’s private-field convention.
- Keep one clear responsibility per method. Use early returns for validation and unavailable states. Split compound conditions into named values when the names make the decision easier to review.
- Keep related code together. Reuse existing models, commands, and components. Introduce a helper when it gives a repeated operation a clear name or a testable boundary.
- Put comments next to assumptions, compatibility decisions, lifetime constraints, and concurrency rules. Keep straightforward code self-explanatory.

## C#

- Indent with four spaces. Put method and type braces on their own lines, use braces for multi-statement control flow, and keep one statement per line in new or expanded blocks. Use a short expression-bodied member or simple inline accessor when it reads naturally.
- Use `var` where the initializer makes the type clear. Prefer explicit types where the reader would otherwise need to inspect a distant API. Use a descriptive variable name even for a short block when it carries domain meaning.
- Use nullable annotations according to the project configuration. Validate or narrow nullable values before use. Reserve the null-forgiving operator for a documented invariant or an immediately verifiable local guarantee.
- The Avalonia projects and core fixtures use .NET 10. Keep the shared model and services independent of window state so the core fixture can run without creating a UI.
- Use `Task` for async work that has a caller to await. Keep `async void` at event boundaries. Observe cancellation, recheck the target after awaits, and return to the UI dispatcher before changing controls from background work. Release streams, bitmaps, timers, and native handles at the boundary that owns them.

## Persistence, launch, and integrations

- Preserve existing profile data and serializers. Add defaults for absent saved fields and guard against stale cache records. Use the store’s existing save and recovery conventions. Restore the previous in-memory state if persistence fails.
- Keep launch paths and argument handling explicit. Maintain the existing process-tracking contract, and let expensive artwork, network, or library work yield while a game is running.
- Keep Windows-specific calls behind platform checks and the existing adapter when possible. Preserve the intended behavior for other desktop hosts when touching shared controls.
- Keep external input validation and timeouts near the integration boundary. Make cache replacement transactional and allow a previous usable result to survive a failed refresh.

## XAML and UI

- Use four-space nesting in Avalonia XAML and wrap long attribute lists onto readable lines.
- Use shared controls and theme keys for equivalent UI roles. Keep design tokens and control templates in the component project. Keep library changes, feature commands, and persistence in Desktop.
- Give icon-only actions an automation name. Preserve keyboard behavior, focus feedback, scaling, text wrapping, motion preferences, and both translucent and solid surfaces where applicable.
- Prefer named elements when a code-behind contract or test uses them. Verify changes to template selectors or named parts against the project’s pinned Avalonia version.
- Run the development build and inspect the result at multiple window sizes. Use the [visual verification checklist](../AGENTS.md#verify-ui-changes-visually) to compare styling, typography, spacing, and legibility with the rest of the app.

## Scripts, configuration, and documentation

- Use descriptive PowerShell names, explicit parameters, and literal paths for file operations. Check native exit codes. Keep build outputs and test profiles separate from source and the installed user profile.
- Follow the installer’s existing section structure and Pascal indentation. Keep per-user setup, prerequisites, and release naming visible in the setup definition.
- Preserve configuration keys and resource names used by callers. Use UTF-8 and avoid tabs and trailing whitespace in authored source and configuration. Keep change-specific prose in the matching source article or catalog entry.

## Required verification

Run the lint entrypoint from the repository root in PowerShell 7.

```powershell
pwsh -NoProfile -File ./lint.ps1
```

For edited or new C# files, use the same entrypoint with their paths from an existing PowerShell session.

```powershell
./lint.ps1 -ChangedPath 'src/Library.cs','avalonia/LGLauncher.Desktop/Launcher.Settings.cs'
```

The lint entrypoint checks the source tree, script and config syntax, local documentation links, source articles, and UI catalog references. It compiles the Avalonia projects with warnings treated as errors. It uses the SDK’s Roslyn formatter in verification mode for the C# paths you supply. An existing restore supports `-NoRestore`. Rectify each finding and rerun the check. Keep unrelated inherited formatting out of feature changes by passing the C# files you edited.

Use the [build and test guide](source/build-and-tests.md) to verify the runtime behavior or Windows packaging that changed. Automated checks catch mechanical issues. Apply the readability and ownership rules above during review.

See [Microsoft’s formatter reference](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-format) and [Avalonia’s XAML compilation guide](https://docs.avaloniaui.net/docs/xaml/compilation) for tool behavior.
