# Avalonia UI for LG Launcher

The production Windows launcher uses `LGLauncher.Desktop`. `LGLauncher.Components` contains the shared controls and theme, and `LGLauncher.Gallery` previews them with sample data. Start with the [UI catalog](../docs/ui/README.md) and [component source articles](../docs/source/components.md) when changing a control.

## Toolkit decision history

LG Launcher adopted Avalonia for its shared desktop rendering layer, reusable control templates, and access to native window integration. These were the alternatives considered during UI prototyping.

| Toolkit | Strengths for LG Launcher | Trade-off to validate |
| --- | --- | --- |
| Avalonia | Shared rendered controls, XAML control themes, and Win32, Linux, and macOS desktop backends | Linux runs through X11 or XWayland by default. Native Wayland is opt-in and needs separate testing. |
| Qt Quick | Mature declarative components and a GPU scene graph with Direct3D, OpenGL, Vulkan, and Metal backends | A Qt implementation would need its own application layer and deployment setup. Confirm the distribution terms for the modules used. |
| Tauri | Reusable web components and a native host with access to platform services | Windows renders through WebView2. Linux and macOS render through different WebKit configurations, so visual fidelity and resource use need testing on each platform. |
| Flutter | A shared desktop widget system and strong support for custom-drawn interfaces | Native window and launcher behavior depends on platform-specific plugins or code. Verify keyboard and accessibility behavior alongside rendering performance. |

Sources include [Avalonia platform support](https://docs.avaloniaui.net/docs/supported-platforms), [Avalonia Linux integration](https://docs.avaloniaui.net/docs/platform-specific-guides/linux), [Qt Quick rendering](https://doc.qt.io/qt-6/qtquick-visualcanvas-scenegraph-renderer.html), [Qt licensing](https://doc.qt.io/qt-6/licensing.html), [Tauri webviews](https://v2.tauri.app/reference/webview-versions/), and [Flutter desktop support](https://docs.flutter.dev/platform-integration/desktop).

## Shared components

- **Tokens** define the canvas, surfaces, borders, text, accent, status colors, radii, padding, and icons.
- **Button styles** provide primary, secondary, quiet, and selected navigation states. Shared templates handle pointer, keyboard focus, and disabled feedback.
- **Library widgets** provide game rows, portrait tiles, and collection mosaic cards.
- **Settings and status** use common section, input, empty-state, and status-indicator styles.
- **Artwork toolbar** presents the zoom value and exposes save and cancel commands. The host screen owns the cropping and persistence.

Load `avares://LGLauncher.Components/Theme/Components.axaml` in the host's application styles after `FluentTheme`. Use the shared classes and templates on new screens. Add a token for a repeated visual role and leave feature behavior in the host screen or its model.

For Desktop choice fields, use the shared `BuildChoicePicker` builder documented in [Preferences and dialogs](../docs/source/desktop/settings.md). It supplies the frosted context menu, alignment, and keyboard behaviour. The gallery also demonstrates the underlying `lg-select` ComboBox style. Use `Classes="lg-number"` on a `NumericUpDown` and assign the range and value with Avalonia's usual properties.

For context menus, create a menu with `LauncherMenus.Create()`, add actions, and assign it to the owning control. Pass `PlacementMode.Bottom` for a button menu and call `Open(owner)` from its click handler. `AddAction` returns the menu item so the owner can change its enabled state.

```csharp
var menu = LauncherMenus.Create();
var edit = menu.AddAction("Edit game", EditGame, "LG.Icon.Edit");
menu.AddSeparator();
menu.AddAction("Remove from library", RemoveGame, "LG.Icon.Delete", destructive: true);
gameCard.ContextMenu = menu;
```

## Run the gallery

Use the .NET 10 SDK.

```text
dotnet run --project avalonia/LGLauncher.Gallery/LGLauncher.Gallery.csproj
```

The gallery uses sample data and demo actions. To run its desktop checks and capture the result, supply a PNG path.

```powershell
New-Item -ItemType Directory -Path .build-work -Force | Out-Null
dotnet run --project avalonia/LGLauncher.Gallery/LGLauncher.Gallery.csproj -- --smoke .build-work/gallery.png
```

## Platform verification targets

1. **Windows** validates startup, scroll and input latency, idle resource use while a game is running, DPI, keyboard navigation, file pickers, embedded icons, and window behavior. Extract Windows-specific functions behind adapters.
2. **Linux** validates native executable launch, file and icon conventions, and both X11 and Wayland desktop sessions. Treat XWayland and the opt-in native Wayland backend as separate targets.
3. **macOS** validates native launch and window conventions, Apple Silicon, and the signing and notarization workflow for distribution.

The Windows launcher in `LGLauncher.Desktop` uses these components and shares the existing library serializer, process tracking, directory watcher, Steam catalog, and artwork framing math. The Windows adapter supplies embedded executable icons and compositor corner preferences. See the [main README](../README.md) for build and migration checks.
