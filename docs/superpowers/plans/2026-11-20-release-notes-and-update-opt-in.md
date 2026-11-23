# Release Notes and Automatic Update Opt-In Implementation Plan

> Historical implementation plan. See the [current updater reference](../../source/desktop/updates.md) for shipped behaviour.

**Goal** Show the installed release notes in About, expose notes for a newer stable release when available, and offer automatic updates on the first launch of a new profile.

- [x] Keep update checks independent from the installed notes. In `UpdateFeed`, validate the installed tag, bound release bodies, and retain the version and body of any accepted newer release. Exercise these paths with fixture responses.
- [x] Use `BuildAbout`, the shared section style, and the RSS text renderer to present notes below the update actions. Theme Avalonia's native tabs with launcher tokens and use them only when the feed identifies a newer release. Verify switching, loading and unavailable notes, and visual fit at supported widths.
- [x] Use the existing first-launch usage notice and modal host to request explicit permission for automatic installation. Reuse normal preference persistence and leave existing profiles as configured. Verify the default, dismissal, and continued persistence after an affirmative choice.
- [x] Update the source documentation and UI catalog. Run the formatter, Windows smoke checks, and fixture screenshots before preparing any release.
