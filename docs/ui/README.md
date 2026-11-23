# UI catalog

[catalog.json](catalog.json) is the searchable inventory of LG Launcher's current UI. Use it before changing an interface or choosing a control. Search by what the UI should do, then open the source file and its article in the [source guide](../source/README.md) before deciding where the change belongs.

## Find an existing control

1. Search the catalog for the user action, control type, style class, or resource key. For example, try `Confirm`, `ContextMenu`, `lg-select`, or `LG.Icon.Refresh`.
2. Check the entry's scope and follow its `related` IDs. A shared control often has a matching XAML theme and a desktop pattern that shows how the launcher uses it.
3. Open the recorded source and check the component's current contract. The catalog explains how an element is used. Source defines its properties and behaviour.
4. Prefer an existing control, style, or token. Follow the UI gap instructions in [AGENTS.md](../../AGENTS.md) when the inventory has no suitable element.

After the change, launch the development build and inspect the working UI at multiple window sizes. Follow the [visual verification checklist](../../AGENTS.md#verify-ui-changes-visually).

## Scope

| Scope | What it covers |
| --- | --- |
| `shared-avalonia` | Reusable types, control themes, selectors, palette and layout tokens, and symbolic icons in `LGLauncher.Components`. Both desktop and gallery load the theme. |
| `desktop` | Production shell surfaces, window decoration, view-level composition, settings controls, runtime theme state, and desktop-only components. |
| `gallery` | Sample layouts and actions in the separate component gallery. Use the gallery to see shared components with example data. |

The shared Avalonia theme is loaded after `FluentTheme` through [Components.axaml](../../avalonia/LGLauncher.Components/Theme/Components.axaml). It imports [Inputs.axaml](../../avalonia/LGLauncher.Components/Theme/Inputs.axaml), [Tokens.axaml](../../avalonia/LGLauncher.Components/Theme/Tokens.axaml), and [Icons.axaml](../../avalonia/LGLauncher.Components/Theme/Icons.axaml). Base Avalonia and Fluent styles belong to the dependency. Entries cover LG Launcher's own themes, overrides, and compositions.

## Query from the repository root

**PowerShell**

```powershell
$catalog = Get-Content -Raw docs/ui/catalog.json | ConvertFrom-Json
$catalog.entries |
    Where-Object { $_.kind -eq 'component' -and $_.scope -eq 'shared-avalonia' } |
    Select-Object id, summary, source

$catalog.entries |
    Where-Object { $_.classes -contains 'lg-select' -or $_.symbols -contains 'PreferenceChoice' } |
    Select-Object id, kind, source
```

**jq**

```sh
jq -r '.entries[] | select(.kind == "icon") | [.resourceKey, .summary] | @tsv' docs/ui/catalog.json
jq -r --arg term 'modal' '.entries[] | select([.id, .summary, (.symbols // [])[]] | join(" ") | test($term; "i")) | [.id, .source] | @tsv' docs/ui/catalog.json
```

**ripgrep**

```sh
rg -n -i -C 4 'frosted|lg-menu|lg\.icon\.refresh' docs/ui/catalog.json
```

## Common starting points

| Need | Catalog entries to look up |
| --- | --- |
| Button, navigation row, or genre chip | `theme.button`, `style.button.*`, `pattern.desktop.common-builders` |
| Numeric field, dropdown, colour picker, or switch | `style.number-input`, `style.choice-picker`, `component.colour-picker`, `style.toggle-switch`, `pattern.desktop.preference-controls` |
| Confirmation, notice, or edit form | `component.modal-host`, `component.modal-dialog`, `pattern.desktop.dialogs` |
| Local file upload | `component.media-file-field`, `pattern.desktop.game-media` |
| Context menu or text editing actions | `helper.launcher-menus`, `component.frosted-context-menu`, `style.menu-item` |
| Game tile, collection card, or search result | `component.game-card`, `component.collection-card`, `component.library-row`, `pattern.desktop.library-search` |
| Icon or palette colour | Search `resourceKey` for `LG.Icon.*` or filter entries with `kind == "token"`. |
| Artwork-backed surfaces or reduced motion | `pattern.desktop.artwork-renderer`, `style.desktop.artwork-glass`, `state.desktop.motion`, `state.desktop.opaque` |
| Mod setup, downloads, or installed-mod actions | `pattern.desktop.mods`, `style.warning-panel`, `style.progress-bar` |
| About release notes | `pattern.desktop.release-notes`, `style.tabs` |

## Record format and maintenance

The catalog is UTF-8 JSON with `schemaVersion: 1` and an `entries` array. Each record has an ID, kind, scope, repository-relative source path, and summary. Additional fields identify selectors, classes, resource keys, type names, code symbols, and consumers. Symbols can include associated named controls and helpers from the view or its code-behind. `related` contains IDs in this same catalog.

Update the entry in the same change when you add, rename, move, or remove a documented UI element. Keep IDs stable for the same role, keep paths relative to the repository root, and use the exact selectors and resource keys from source. For a selector family, list the state and template-part selectors together so callers can find the whole style. Record production patterns under `desktop` and move them under `shared-avalonia` only when a shared implementation exists.

`lint.ps1` checks catalog paths, entry IDs, links between entries, and the spelling of symbols, selectors, classes, and resource keys against the UI source.
