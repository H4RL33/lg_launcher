# Library views and navigation

[Desktop source index](README.md)

## `avalonia/LGLauncher.Desktop/Launcher.Navigation.cs`

Keeps up to 64 in-process view entries for games, collections, and RSS articles. It drops the oldest entry when adding a view at the limit. It records the outer page scroll position and pagination offset, discards forward history on a new route, skips removed entries, and restores focus-related UI state by closing search and cancelling artwork edits or animation as appropriate. Opening game details from an article detaches the reader so its image requests and decoded bitmaps are released. The history entry keeps the article data for a return visit. Navigation refreshes the selected game's [mod status](mods.md).

- **Depends on** the main window's page and profile state, `Game`, `GameCollection`, RSS article and subscription types, Avalonia mouse buttons, the shared renderer, and search and artwork helpers.
- **Used by** the main window and page actions, search results, settings navigation, RSS reader, and collections. Desktop history checks exercise mouse Back and Forward and restoration of the saved position.

[Navigation history source](../../../avalonia/LGLauncher.Desktop/Launcher.Navigation.cs) · [RSS](rss.md) · [Verification](verification.md)

## `avalonia/LGLauncher.Desktop/Launcher.Home.cs`

Builds Home from profile data. It places the optional RSS shelf ahead of up to three recently played games, the collections shelf, and the sorted library. The library groups portrait tiles into virtualized rows while the reusable shelf helper exposes accessible arrow controls that scroll horizontally when a row overflows. The Collections and RSS shelves hide their horizontal scrollbars and support horizontal scrolling with arrows or a horizontal input gesture. The RSS shelf supplies its own header content with settings and refresh actions.

- **Depends on** the main window's page body, card factory and UI helpers, the recent RSS and collection card builders, and the shared `SmoothScroll` helper.
- **Used by** the main view dispatcher, the RSS partial for its Home shelf, and the smoke tests for overflow, row sizing, and scroll behaviour.

[Home source](../../../avalonia/LGLauncher.Desktop/Launcher.Home.cs) · [RSS](rss.md) · [Verification](verification.md)

## `avalonia/LGLauncher.Desktop/Launcher.Collections.cs`

Builds collection cards and artwork mosaics and implements the searchable picker for manual membership. The picker edits a working selection and changes game memberships only after confirmation. Dynamic cards derive their members from collection rules and expose the rule summary in their tooltips.

Home and the collection browser draw up to four mosaic tiles from the collection’s current games. Games with custom portraits take priority. Each tile uses the same custom portrait, Steam portrait, and Steam header fallback as its game card. Saving or clearing a portrait refreshes visible mosaics and applies to later visits.

- **Depends on** `GameCollection.Includes` and `Game.CollectionIds`, the collection rule editor, shared card and context menu controls, the modal helpers, and artwork loading for the mosaics.
- **Used by** the collection browser, `Launcher.Home.cs`, the game context menu, and smoke verification for membership, filtering, and artwork mosaics.

[Collection view source](../../../avalonia/LGLauncher.Desktop/Launcher.Collections.cs) · [Preferences and dialogs](settings.md) · [Steam and artwork](steam.md)

The main window lists collections by name below the sidebar's Library section. Their rows share the game row's sizing and icon slot. Selecting a collection opens its member view through normal navigation. The member view uses the shared choice picker for Title A–Z, Last launched, and Most played.

With motion enabled, games on the destination page that were also visible on the previous page move from their former positions. Changing the sort order still returns to page one. Reduce Motion applies the new order immediately.

## `avalonia/LGLauncher.Desktop/Launcher.CollectionIcons.cs`

Assigns sidebar symbols and adds Change icon to the context menus on collection rows and artwork cards. A saved Lucide name takes priority. Manual collections use the library symbol by default. Dynamic collections take the first recognized positive genre or Steam tag rule, including migrated genre filters. Common rules have curated mappings and other rule values can match Lucide names. The brain symbol is the fallback.

The modal searches the bundled names, aliases, and keywords. Its virtualized rows take their column count from the scroll viewport and leave an inset for the vertical scrollbar, so every icon fits across the available width. Use Down from the search field to enter the grid, arrow keys or Home and End to move through results, and Enter or Space to select. Reset to default clears the pending override so the sidebar uses its default symbol. Save changes persists the choice and updates the sidebar. Save errors leave the modal open and preserve the previous collection value.

- **Depends on** `GameCollection.IconName` and `EffectiveRules`, the `LucideIconCatalog` and shared icon, button, and dialog components, Avalonia item virtualization, and the main window's profile store and sidebar renderer.
- **Used by** collection sidebar rows and artwork card menus, and `Launcher.Verification.CollectionIcons.cs`.

[Collection icon source](../../../avalonia/LGLauncher.Desktop/Launcher.CollectionIcons.cs) · [Shared components](../components.md)

## `avalonia/LGLauncher.Desktop/Launcher.CollectionRules.cs`

Builds the create and edit dialog for static or dynamic collections and displays readable rule descriptions. Dynamic rules select a genre or Steam tag, a comparison operator, and a value, with match-all or match-any behaviour. The editor converts legacy genre filters into rule rows and restores the original model if the save fails.

- **Depends on** `GameCollection`, `CollectionRule`, the shared modal and select controls, compact row layout, the common dialog field helpers, and the main window's save and navigation methods.
- **Used by** the main collection page, Home's collection actions, collection card menus and tooltips, and `Launcher.Verification.CollectionRules.cs`.

[Collection rules source](../../../avalonia/LGLauncher.Desktop/Launcher.CollectionRules.cs) · [Preferences and dialogs](settings.md)

The editor uses the shared choice picker for matching, rule attributes, and operators. Attribute and operator columns fit their content and align across rows. The value field moves below the pickers in a compact dialog.

Rule values offer searchable suggestions from the library's genres or current Steam tags. The list deduplicates casing variants, reuses the collection picker's fuzzy matcher, and uses the game search dropdown's frame, row sizing, and height animation. Picking a result fills the field with its stored spelling. Typed partials and previously saved values remain usable.

## `avalonia/LGLauncher.Desktop/Launcher.Search.cs`

Implements the main window's library search dropdown. It debounces typing and parses the query once per results refresh before matching titles and genres. It maintains selection on refresh, handles keyboard or pointer activation, and closes the dropdown on outside clicks, focus loss, or page changes. The editor also uses its result height calculation for rule suggestions.

- **Depends on** named search controls in `Launcher.axaml`, Avalonia's dispatcher and input events, the title/genre matcher, `LibraryEntry` sidebar artwork, and `SelectGame` history navigation.
- **Used by** the main window constructor and render dispatcher, genre chips, navigation and artwork editing, and the search results and animation smoke checks.

[Search source](../../../avalonia/LGLauncher.Desktop/Launcher.Search.cs) · [Application shell](app.md) · [Steam and artwork](steam.md)
