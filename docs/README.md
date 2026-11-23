# LG Launcher developer documentation

Start here before working on LG Launcher. This documentation is organised by the layer that owns the behaviour and then by the file that implements it.

| Start with | Use it to |
| --- | --- |
| [Source map](source/README.md) | Find a subsystem and read the article for each source file you will change. Follow links to its callers, tests, and shared dependencies. |
| [UI catalog](ui/README.md) | Find an existing component, theme resource, style, or UI pattern before designing or changing an interface. |
| [Coding style](coding-style.md) | Understand the readability rules and run the required automated checks. |
| [Development and release workflow](workflow.md) | Choose a branch, describe the change, and promote selected commits to a release. |
| [Build and test articles](source/build-and-tests.md) | Pick the verification command and see where it writes its fixtures and outputs. |

## Find an article

1. Use the source map to choose a group. Each article uses the exact repository-relative source path as its heading.
2. Read the **Purpose**, **Depends on**, and **Used by** entries for the files in scope. Follow the dependencies and callers that your change could affect. Dependencies in these articles describe important direct relationships and contracts, including XAML resource consumers, linked source, and the tests that exercise a file.
3. For UI work, read the UI catalog guide and search the JSON catalog for the existing control, visual role, or behaviour. Follow the catalog entry to its source and the relevant source article.

For example, from the repository root:

```powershell
rg -n -F 'avalonia/LGLauncher.Desktop/Launcher.Settings.cs' docs/source
rg -ni 'animated toggle|ToggleSwitch' docs/ui/catalog.json
```

Start with [Desktop](source/desktop/README.md) for current launcher features, [Shared library and Steam code](source/core.md) for data and persistence, and [Shared UI components](source/components.md) for reusable interface controls. The Windows launcher uses .NET 10 and Avalonia. [Gallery](source/gallery.md) covers the standalone component preview.

## Maintain the documentation with the code

- Review the affected docs before completing a change. Document the final implementation, including behaviour, defaults, important callers, configuration, commands, and verification steps that changed.
- Add or update the matching source article when you introduce, move, remove, or change the contract of a file. Follow references from the source map and repair stale paths and links. Keep links to repository files relative.
- Update the UI catalog and the relevant source articles together when a control, style, token, icon, or usage pattern changes. Verify catalog IDs, related entries, source paths, selectors, and resource keys against the implementation.
- Check command examples against the actual scripts and workflows. Update the [build and test guide](source/build-and-tests.md) for maintainer procedures, the [project README](../README.md) for user-visible setup and behaviour, and the [Avalonia README](../avalonia/README.md) for the component gallery.
- Check touched links and examples before finishing. `lint.ps1` verifies repository-local links and Markdown heading fragments, source article coverage, and UI catalog references. Keep generated output and local test or release staging under the documented build directories.

The dated [designs and plans](superpowers/) record decisions and implementation proposals at the time they were written. Use the source articles and UI catalog for current behaviour and verification.
