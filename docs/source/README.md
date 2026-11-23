# Source map

[Developer documentation](../README.md)

| Area | Articles |
| --- | --- |
| Avalonia launcher | [Desktop source](desktop/README.md) |
| Shared model, persistence, and services | [Library, Steam, sessions, and framing](core.md) |
| Reusable Avalonia interface | [Shared controls and theme](components.md) and [UI catalog](../ui/README.md) |
| Standalone UI preview | [Gallery](gallery.md) |
| Build, packaging, tests, and source assets | [Build and test files](build-and-tests.md) |

## How the parts fit

**Desktop** composes the launcher window, feature views, onboarding, and update flow. Its project links the shared C# model and services from `src/`, and references the **Components** project for controls, styles, and motion. **Gallery** references Components and supplies its own sample data. The Windows core fixture project compiles the linked model and services using the same .NET 10 target as Desktop.

The normal Windows product is `build/LG Launcher.exe`. The project repository holds the source, publication workflows, and official release feed. Release staging uses dedicated branches. [LG Launcher on GitHub](https://github.com/H4RL33/lg_launcher) · [Stable Windows releases](build-and-tests.md#stable-windows-releases)

The source articles cover authored code, XAML, project and runtime configuration, build and test scripts, and bundled assets. The directory names `build`, `.build-work`, `bin`, and `obj` refer to local outputs or managed build inputs.
