# Shared Avalonia controls and theme

[Source map](README.md) · [UI catalog](../ui/README.md) · [Gallery](gallery.md)

Desktop and Gallery load `Theme/Components.axaml` after Fluent. Shared components own rendering, input, and generic control state. Feature views supply commands, domain data, and persistence.

## `avalonia/LGLauncher.Components/LGLauncher.Components.csproj`

[Source](../../avalonia/LGLauncher.Components/LGLauncher.Components.csproj)

- **Purpose** — Builds the reusable control assembly and packages its Avalonia themes, pinned Lucide icon data, and attribution.
- **Depends on** — `avalonia/Directory.Build.props`, the Avalonia package, the component C# and XAML files in this folder, `Lucide.0.468.0.tsv`, and `Lucide.LICENSE.txt`.
- **Used by** — The Desktop and Gallery project references. Both hosts resolve its shared styles through `avares://LGLauncher.Components/`.

## `avalonia/LGLauncher.Components/Widgets.cs`

`GameCard.IsModded` toggles a white wrench in an opaque, bordered badge at the portrait's top-right corner. The `:compact` state reduces the badge size and playtime padding below 64 DIP. The playtime pill stays within the card width.

[Source](../../avalonia/LGLauncher.Components/Widgets.cs)

- **Purpose** — Defines the bindable contracts for `StatusIndicator`, `LibraryRow`, `GameCard`, `CollectionCard`, and `ArtworkToolbar`. Library rows accept a bitmap or a symbolic icon in the same rounded icon slot. Game cards derive portrait height from their tile width. Home supplies the known column width before measurement, and every card updates when its arranged width changes. Library rows derive the icon radius from its bounds. Feature actions belong to the host.
- **Depends on** — Avalonia styled properties, rendering types, and `ICommand`. Visual templates live in `Theme/Components.axaml`.
- **Used by** — The Desktop window and its home, collections, search, artwork, and status views. Gallery also demonstrates the shared controls.

## `avalonia/LGLauncher.Components/MediaFileField.cs`

[Source](../../avalonia/LGLauncher.Components/MediaFileField.cs)

- **Purpose** — Displays a named file selection, optional file guidance, a browse button, and a clear action. Guidance is vertically centred alongside the field label when both fit on one line. It shows the saved filename when available and exposes the pending file or reset for the enclosing form to validate and save.
- **Depends on** — Shared button and muted text styles. The caller supplies the file picker, saved filename, and fallback label.
- **Used by** — Desktop's Manage metadata dialog for backdrop, portrait, logo, and music.

## `avalonia/LGLauncher.Components/SymbolIcon.cs`

[Source](../../avalonia/LGLauncher.Components/SymbolIcon.cs)

- **Purpose** — Renders a stroke icon from geometry in a common 24-unit coordinate system. Uses the control foreground for colour so the same symbol can follow its host’s state and theme.
- **Depends on** — Avalonia drawing, geometry, and styled properties. The shared symbol resource keys are declared in `Theme/Icons.axaml`.
- **Used by** — Shared widget templates, both app shells, feature views, and menu builders.

## `avalonia/LGLauncher.Components/LucideIconCatalog.cs`

[Source](../../avalonia/LGLauncher.Components/LucideIconCatalog.cs)

- **Purpose** — Loads the bundled Lucide names, labels, and keywords once per process and parses each icon's geometry on first use.
- **Depends on** — The embedded icon pack, Avalonia's `StreamGeometry`, and the 24-unit convention shared by `SymbolIcon`.
- **Used by** — Desktop's collection icon picker, automatic sidebar icon resolver, per-mod symbols, and migration smoke checks.

## `avalonia/LGLauncher.Components/Lucide.0.468.0.tsv`

[Asset](../../avalonia/LGLauncher.Components/Lucide.0.468.0.tsv)

- **Purpose** — Bundles all 1,544 canonical icons from the [Lucide 0.468.0 release](https://github.com/lucide-icons/lucide/tree/0.468.0/icons) for offline use. Each tab-separated line holds the original icon name, upstream aliases and tags, and path data. The SVG basic shapes are converted to path commands in their original 24-unit coordinates.
- **Depends on** — The pinned release's SVG artwork and JSON metadata, with attribution in `Lucide.LICENSE.txt`.
- **Used by** — `LucideIconCatalog.cs` through the resource embedded by `LGLauncher.Components.csproj`.

## `avalonia/LGLauncher.Components/LauncherMenus.cs`

[Source](../../avalonia/LGLauncher.Components/LauncherMenus.cs)

- **Purpose** — Provides the standard construction path for context menus, actions, destructive styling, separators, and editable text menus. Text editing delegates to the field’s clipboard commands and keeps platform shortcuts and enabled states.
- **Depends on** — `FrostedContextMenu.cs`, `SymbolIcon.cs`, Avalonia menu/clipboard APIs, and classes defined in `Theme/Inputs.axaml`.
- **Used by** — Desktop artwork, game, collection, mod cache, and RSS URL menus. Gallery demonstrates button-driven menu use.

## `avalonia/LGLauncher.Components/FrostedContextMenu.cs`

[Source](../../avalonia/LGLauncher.Components/FrostedContextMenu.cs)

- **Purpose** — Owns backdrop sampling and the reveal/collapse lifecycle for the launcher menu. Captures a small image of the owning window for frosted glass and releases its snapshot on close. Clips the presenter from the bottom when the popup is placed above its pointer or trigger, and from the top otherwise.
- **Depends on** — Avalonia popup and bitmap APIs and the shared easing in `ViewMotion.cs`. Its theme in `Inputs.axaml` supplies named template parts. Host classes `lg-motion` and `lg-opaque` control motion and translucency.
- **Used by** — `LauncherMenus.Create`, Desktop artwork pointer handling, and menu smoke checks. Hosts should construct it through the shared factory.

## `avalonia/LGLauncher.Components/ModalHost.cs`

[Source](../../avalonia/LGLauncher.Components/ModalHost.cs)

- **Purpose** — Defines `ModalHost` and `ModalDialog` so prompts remain inside the launcher window. Manages nested dialogs, underlay input, default and dismiss actions, focus restoration, and the accepted result. A dialog can show trailing content vertically centred beside the heading, additional header content below it, leading footer content alongside the actions, or a custom body control that manages its own scrolling. An empty title omits the heading. Callers can supply a border that wraps the footer and can name a titleless dialog for accessibility. The dialog clips its content to the inner edge of the themed frame, keeping its corner stroke and shadow visible.
- **Depends on** — Avalonia panels, focus, routed keyboard events, and UI dispatch. Backdrop and dialog shell styles come from `Theme/Components.axaml`.
- **Used by** — The Desktop window and its dialog, collection, settings, startup, and update flows. Desktop modal smoke checks call the host directly.

## `avalonia/LGLauncher.Components/ColourPicker.cs`

[Source](../../avalonia/LGLauncher.Components/ColourPicker.cs)

- **Purpose** — Edits an opaque RGB colour using icon-only preset swatches or a hex field with a square live preview sized to the field. The caller supplies the built-in and current colours. Preset names remain available to assistive technology and in tooltips. The picker accepts six hex digits with an optional leading `#`, reports invalid input on submission, and formats saved values as `#RRGGBB`.
- **Depends on** — Avalonia buttons, text entry, automation metadata, and the shared swatch and preset styles in `Theme/Inputs.axaml`.
- **Used by** — Appearance's Default accent colour modal. Desktop owns the dialog actions, persistence, and contrast adjustments.

## `avalonia/LGLauncher.Components/SmoothScroll.cs`

[Source](../../avalonia/LGLauncher.Components/SmoothScroll.cs)

- **Purpose** — Animates a scroll viewer toward a bounded offset and lets new input adjust a running animation. Reads Windows wheel line/character settings, handles zero and page-at-a-time settings, and respects the host’s motion policy and scroll chaining.
- **Depends on** — Avalonia routed input, popup roots, scroll viewers, animation-frame callbacks, plus a Windows-only `SystemParametersInfoW` call.
- **Used by** — Desktop shell wheel handling, Home overflow arrows, motion settings, and desktop scroll smoke checks.

## `avalonia/LGLauncher.Components/ViewMotion.cs`

[Source](../../avalonia/LGLauncher.Components/ViewMotion.cs)

- **Purpose** — Provides a reusable easing curve for revealing a new view. Supports opacity alone or opacity with a small vertical translation. Reordered tiles update their temporary transform on animation frames using the same cubic ease-out as view translation. The helper clears the transform after it reaches zero offset or when the host cancels the movement. The artwork palette uses the same duration as the fade. Context menus, the warning expander, and the sidebar sections share a cubic ease-out for their 180 ms reveal and 105 ms collapse.
- **Depends on** — Avalonia keyframes, transforms, and cancellation. The caller owns when to animate and how to reset after a cancelled view transition.
- **Used by** — Desktop navigation, sidebar sections and collection sorting, dialogs, startup, artwork, context menus, and the warning expander.

## `avalonia/LGLauncher.Components/WarningExpander.cs`

[Source](../../avalonia/LGLauncher.Components/WarningExpander.cs)

- **Purpose** — Clips warning details and adjusts the visible height as the panel opens and closes. Uses the context menu motion and settles immediately when Reduced Motion is enabled.
- **Depends on** — The `LG.WarningPanel` template and `ViewMotion.cs`. The button preserves native expander state and supports click and keyboard input across the whole header.
- **Used by** — The game Mods dialog.

## `avalonia/LGLauncher.Components/Theme/Components.axaml`

[Source](../../avalonia/LGLauncher.Components/Theme/Components.axaml)

- **Purpose** — Serves as the shared theme entrypoint. Defines widget templates, the button variants, and common panels, list styles, labels, modal surfaces, and motion. `lg-game-icon` clips Library and Managed installations icons with a radius equal to 20% of the shorter side. `lg-muted` sets regular weight for supporting text, including inside controls with heavier labels. `lg-tabs` and `lg-tab` style native Avalonia tabs with the accent selection underline and shared focus treatment. The underline fades only under `lg-motion`. `lg-warning-panel` provides the amber shell, warning icon, title and count, and a chevron with an evenly inset hover face that uses `LG.ControlRadius`. `Button.lg.destructive` shares the filled red button treatment between mod uninstall and cached-package deletion.
- **Depends on** — `Widgets.cs`, `SymbolIcon.cs`, Fluent Theme, and the sibling `Tokens.axaml`, `Icons.axaml`, and `Inputs.axaml` resources.
- **Used by** — Desktop and Gallery application styles. Shared style changes can affect both hosts and several features at once.

The Web tables use `web-table-row` for the row background and select it with the accent tint. `LG.WebFileRowItem` gives the file viewer a plain item presenter so the table row owns the highlight.

Shared single-line text fields center text and placeholders vertically. Shared text boxes use the darker `LG.FieldHover` fill and a white border on pointer hover. Under `lg-motion`, both brushes transition over 150 ms. Focused fields retain their focus treatment. Desktop derives the hover fill from the canvas for each non-Classic colourway.

## `avalonia/LGLauncher.Components/Theme/Inputs.axaml`

[Source](../../avalonia/LGLauncher.Components/Theme/Inputs.axaml)

- **Purpose** — Defines themed text fields, combo boxes, number spinners, preference rows and picker buttons, colour swatches, toggle motion, tooltips, context menus, and the shared `lg-progress` track. Number step buttons stretch inside a uniform inset on either side of the field. Appearance's list pickers and collection rule picker buttons open the shared context menu. The default accent uses the same trigger styling and opens a modal colour picker. Game search and collection rule suggestions share frame, row, and height transition styles. Progress and search result transitions run when the host enables motion.
- **Depends on** — Fluent control templates and named parts, `FrostedContextMenu.cs`, `SymbolIcon.cs`, and the sibling tokens and icons. Desktop supplies runtime font and motion values.
- **Used by** — Both theme hosts through `Components.axaml`. Consumer views opt in via control classes or the shared factories. When changing a Fluent template selector, verify it against the configured Avalonia version.

Caption-free `ToggleSwitch.lg-compact` controls lay the track and knob across Fluent's spacer column. This removes the trailing gap and aligns the track with neighbouring buttons without changing input handling or knob motion.

## `avalonia/LGLauncher.Components/Theme/Tokens.axaml`

Classic provides the directional gradient through `LG.MainBackground`. Other Desktop colourways replace that brush and the artwork fallback with solid palette colours. The game detail shade remains a gradient over artwork. The shared small, popup, and modal shadow resources and corner-radius tokens support live overrides from Desktop Appearance settings.

[Source](../../avalonia/LGLauncher.Components/Theme/Tokens.axaml)

- **Purpose** — Centralises shared colours, typography sizes, corner radii, padding, and common motion resources. `LG.DefaultAccentColor` is the built-in purple value for the initial palette and the default colour preset. `LG.Modified` supplies the blue foreground for in-place configuration edits in mod file previews.
- **Depends on** — Avalonia resource and primitive types. Desktop may replace runtime typography and motion resources to honour preferences.
- **Used by** — The other style files and feature XAML/code in Desktop and Gallery through dynamic resources.

## `avalonia/LGLauncher.Components/Theme/Icons.axaml`

[Source](../../avalonia/LGLauncher.Components/Theme/Icons.axaml)

- **Purpose** — Provides symbolic stroke geometry under the `LG.Icon.*` resource keys. Using keys keeps icon shapes consistent across navigation, actions, and menus.
- **Play** — `LG.Icon.Play` uses the Lucide 0.468.0 Play polygon. The game details button renders it through `SymbolIcon` with a round unfilled stroke. [Lucide Play source](https://github.com/lucide-icons/lucide/blob/0.468.0/icons/play.svg)
- **Mods** — The same `LG.Icon.Wrench` geometry appears in Settings navigation, on game details, and in the Mods list. The package actions use Lucide's refresh, download, and trash symbols. The warning panel uses `LG.Icon.Warning`. File previews use the plus, two-way replacement, and close symbols.
- **Depends on** — Avalonia geometry markup, the shared 24-unit drawing convention, and Lucide attribution.
- **Used by** — `SymbolIcon` consumers in control templates, Desktop and Gallery views, and `LauncherMenus.AddAction`.

## `avalonia/LGLauncher.Components/Lucide.LICENSE.txt`

[Source](../../avalonia/LGLauncher.Components/Lucide.LICENSE.txt)

- **Purpose** — Retains the upstream notices for the Lucide-derived symbols distributed with the launcher.
- **Depends on** — The upstream Lucide licence and attribution.
- **Used by** — `LGLauncher.Components.csproj` embeds the notice with the complete Lucide pack. `Theme/Icons.axaml` identifies its relationship to the geometry resources.
