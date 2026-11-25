# Library views and navigation

[Desktop source index](README.md)

## `avalonia/LGLauncher.Desktop/Launcher.Navigation.cs`

Keeps up to 64 in-process view entries for games, collections, and RSS articles. It drops the oldest entry when adding a view at the limit. It records the outer page scroll position and pagination offset, discards forward history on a new route, skips removed entries, and restores focus-related UI state by closing search and cancelling artwork edits or animation as appropriate. Opening game details from an article detaches the reader so its image requests and decoded bitmaps are released. The history entry keeps the article data for a return visit. Navigation refreshes the selected game's [mod status](mods.md). When Browser or Files is active, the same mouse handler directs Back and Forward to the current tab’s or folder view’s history. The folder view also records jumps from Downloads, so Back returns to the transfer list and Forward reopens the destination. [Web navigation](web.md)

- **Depends on** the main window's page and profile state, `Game`, `GameCollection`, RSS article and subscription types, Avalonia mouse buttons, the shared renderer, and search and artwork helpers.
- **Used by** the main window and page actions, search results, settings navigation, RSS reader, and collections. Desktop history checks exercise mouse Back and Forward and restoration of the saved position.

[Navigation history source](../../../avalonia/LGLauncher.Desktop/Launcher.Navigation.cs) · [RSS](rss.md) · [Verification](verification.md)

## `avalonia/LGLauncher.Desktop/Launcher.Home.cs`

Builds Home from profile data. It places the optional RSS shelf ahead of up to three recently played games, the collections shelf, and the sorted library. The Library heading uses the shared sort picker and synchronizes its order with the sidebar. Changing the sort replaces only the items in the virtualized library grid. Its rows and the Recently played shelf fit up to the configured number of game columns using the saved minimum tile width and the available space. The reusable shelf helper exposes accessible arrow controls when a row overflows. The Collections and RSS shelves hide their horizontal scrollbars and support horizontal scrolling with arrows or a horizontal input gesture. The RSS shelf supplies its own header content with settings and refresh actions.

- **Depends on** the main window's page body, card factory and UI helpers, the recent RSS and collection card builders, and the shared `SmoothScroll` helper.
- **Used by** the main view dispatcher, the RSS partial for its Home shelf, and the smoke tests for overflow, row sizing, and scroll behaviour.

[Home source](../../../avalonia/LGLauncher.Desktop/Launcher.Home.cs) · [RSS](rss.md) · [Verification](verification.md)

## `avalonia/LGLauncher.Desktop/Launcher.Collections.cs`

Builds collection cards and artwork mosaics and implements the searchable picker for manual membership. The picker edits a working selection and changes game memberships only after confirmation. Dynamic cards derive their members from collection rules and expose the rule summary in their tooltips.

Home and the collection browser draw up to four mosaic tiles from the collection’s current games. Games with custom portraits take priority. Each tile uses the same custom portrait, Steam portrait, and Steam header fallback as its game card. Saving or clearing a portrait refreshes visible mosaics and applies to later visits.

- **Depends on** `GameCollection.Includes` and `Game.CollectionIds`, the collection rule editor, shared card and context menu controls, the modal helpers, and artwork loading for the mosaics.
- **Used by** the collection browser, `Launcher.Home.cs`, the game context menu, and smoke verification for membership, filtering, and artwork mosaics.

[Collection view source](../../../avalonia/LGLauncher.Desktop/Launcher.Collections.cs) · [Preferences and dialogs](settings.md) · [Steam and artwork](steam.md)

The collapsible Library and Collections lists have separate sort controls and Add new actions. Home’s Library, Collections, and collection members use the shared choice picker. Games support title in either direction, playtime, last launched, newest added, and newest released. Collections support name in either direction, cumulative playtime, newest created, and quantity of games. Unknown dates sort after known dates. The Home library and collection browser share their sort choices with the corresponding sidebar list. Discoveries remains available through the status bar.

Both sidebar headings use the same semibold title style and a smaller, light-weight sort label on the same vertical centre. Opening or closing a heading animates the clipped list and its Add new button with the Mods warning panel’s easing. Reduced Motion changes the height immediately.

With motion enabled, games on a collection member page that were also visible on the previous page move from their former positions. Changing the sort order still returns to page one. Reduce Motion applies the new order immediately.

## `avalonia/LGLauncher.Desktop/Launcher.CollectionIcons.cs`

Assigns sidebar symbols and adds Change icon to the context menus on collection rows and artwork cards. A saved Lucide name takes priority. Manual collections use the library symbol by default. Dynamic collections take the first recognized positive genre or Steam tag rule, including migrated genre filters. Common rules have curated mappings and other rule values can match Lucide names. The brain symbol is the fallback.

The modal searches the bundled names, aliases, and keywords. Its virtualized rows take their column count from the scroll viewport and leave an inset for the vertical scrollbar, so every icon fits across the available width. Use Down from the search field to enter the grid, arrow keys or Home and End to move through results, and Enter or Space to select. Reset to default clears the pending override so the sidebar uses its default symbol. Save changes persists the choice and updates the sidebar. Save errors leave the modal open and preserve the previous collection value.

- **Depends on** `GameCollection.IconName` and `EffectiveRules`, the `LucideIconCatalog` and shared icon, button, and dialog components, Avalonia item virtualization, and the main window's profile store and sidebar renderer.
- **Used by** collection sidebar rows and artwork card menus, and `Launcher.Verification.CollectionIcons.cs`.

[Collection icon source](../../../avalonia/LGLauncher.Desktop/Launcher.CollectionIcons.cs) · [Shared components](../components.md)

## `avalonia/LGLauncher.Desktop/Launcher.CollectionRules.cs`

Builds the create and edit dialog for manual or dynamic collections and displays readable rule descriptions. Dynamic rules select a genre, Steam tag, developer, publisher, or Modded status, a comparison operator, and a value, with match-all or match-any behaviour. Modded values are Yes and No. The editor converts legacy genre filters into rule rows and restores the original model if the save fails.

- **Depends on** `GameCollection`, `CollectionRule`, the shared modal and select controls, compact row layout, the common dialog field helpers, and the main window's save and navigation methods.
- **Used by** the main collection page, Home's collection actions, collection card menus and tooltips, and `Launcher.Verification.CollectionRules.cs`.

[Collection rules source](../../../avalonia/LGLauncher.Desktop/Launcher.CollectionRules.cs) · [Preferences and dialogs](settings.md)

The editor uses the shared choice picker for matching, rule attributes, and operators. Attribute and operator columns fit their content and align across rows. The value field moves below the pickers in a compact dialog.

Rule values offer searchable suggestions from the library or the Yes and No options. The list deduplicates casing variants, reuses the collection picker's fuzzy matcher, and uses the game search dropdown's frame, row sizing, and height animation. Picking a result fills the field with its stored spelling. Text attributes accept typed partials and previously saved values.

## `avalonia/LGLauncher.Desktop/Launcher.Search.cs`

Implements the main window's library search dropdown. It debounces typing and parses a query once per refresh. Plain text matches titles and genres. A leading `genre:`, `tag:`, `developer:`, or `publisher:` filters by a partial name. `year:` and `scale:` use exact values. The placeholder rotates every 30 seconds while the search box is empty and outside focus. The dropdown preserves selection on refresh and closes on outside clicks, focus loss, or page changes.

- **Depends on** named search controls in `Launcher.axaml`, Avalonia's dispatcher and input events, the title/genre matcher, `LibraryEntry` sidebar artwork, and `SelectGame` history navigation.
- **Used by** the main window constructor and render dispatcher, genre chips, navigation and artwork editing, and the search results and animation smoke checks.

[Search source](../../../avalonia/LGLauncher.Desktop/Launcher.Search.cs) · [Application shell](app.md) · [Steam and artwork](steam.md)
