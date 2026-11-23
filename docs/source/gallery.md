# Avalonia component gallery

[Source map](README.md) · [Shared components](components.md) · [UI catalog](../ui/README.md)

Gallery is a standalone preview with sample data. It exercises the component assembly without opening a real library or checking for updates.

## `avalonia/LGLauncher.Gallery/LGLauncher.Gallery.csproj`

[Source](../../avalonia/LGLauncher.Gallery/LGLauncher.Gallery.csproj)

- **Purpose** — Builds the standalone preview application and embeds the artwork sample.
- **Depends on** — `avalonia/Directory.Build.props`, Avalonia Desktop, Fluent Theme, the Components project, and `assets/welcome.jpg`.
- **Used by** — The gallery run and smoke commands in [the Avalonia README](../../avalonia/README.md). Shipping Windows setup uses the Desktop project.

## `avalonia/LGLauncher.Gallery/Program.cs`

[Source](../../avalonia/LGLauncher.Gallery/Program.cs)

- **Purpose** — Starts the gallery with a classic Avalonia desktop lifetime and platform detection, forwarding arguments to the application.
- **Depends on** — Avalonia Desktop and the gallery `App`.
- **Used by** — `dotnet run --project avalonia/LGLauncher.Gallery/LGLauncher.Gallery.csproj` and the published gallery entrypoint when built separately.

## `avalonia/LGLauncher.Gallery/App.axaml`

[Source](../../avalonia/LGLauncher.Gallery/App.axaml)

- **Purpose** — Defines the preview’s dark theme setup so the gallery displays the same templates as the launcher.
- **Depends on** — Fluent Theme and `avares://LGLauncher.Components/Theme/Components.axaml`.
- **Used by** — `App.axaml.cs` loads it. Its resources are visible to `MainWindow.axaml` and the code-built demo controls.

## `avalonia/LGLauncher.Gallery/App.axaml.cs`

[Source](../../avalonia/LGLauncher.Gallery/App.axaml.cs)

- **Purpose** — Creates the preview window and connects the `--smoke <png-path>` mode. Runs the assertions after the window opens and sets the process exit code.
- **Depends on** — `Program.cs` for the lifetime, the paired `App.axaml`, Avalonia dispatch, and `MainWindow.VerifyAndCapture`.
- **Used by** — Gallery startup and scripting that checks or captures the gallery.

## `avalonia/LGLauncher.Gallery/MainWindow.axaml`

[Source](../../avalonia/LGLauncher.Gallery/MainWindow.axaml)

- **Purpose** — Lays out Library, Controls, and Artwork demo pages, plus navigation and a sample status line. Exposes the component variants in an app-like layout.
- **Depends on** — Shared controls, theme resources, and named elements wired by the paired code-behind.
- **Used by** — Gallery `MainWindow` and its preview and smoke capture.

## `avalonia/LGLauncher.Gallery/MainWindow.axaml.cs`

[Source](../../avalonia/LGLauncher.Gallery/MainWindow.axaml.cs)

- **Purpose** — Supplies synthetic game rows, portrait cards, menu actions, and in-memory artwork transforms. Checks the basic gallery interactions and renders a PNG for smoke mode.
- **Depends on** — The paired `MainWindow.axaml`, embedded sample artwork, shared widgets and menu factory, and Avalonia rendering/input.
- **Used by** — Gallery `App.axaml.cs` for interactive startup and smoke mode.
