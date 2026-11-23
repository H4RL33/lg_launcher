# OptiScaler Compatibility Catalog Updates Implementation Plan

> Historical implementation plan. See the [current compatibility and mod management reference](../../source/desktop/mods.md) for shipped behaviour.

> For agentic workers, use the Superpowers executing-plans skill and verify each task before publication.

**Goal** — Deliver current OptiScaler wiki reports to the launcher between executable releases.

**Architecture** — A scheduled GitHub Actions workflow converts the upstream wiki into an advisory JSON file on the release repository's catalog branch. A desktop store downloads and caches that file independently of install eligibility, which still uses the bundled catalog and local checks.

**Spec** — [Catalog update design](../specs/2026-11-20-optiscaler-catalog-updates-design.md)

## Tasks

- [x] Add an importer and fixture checks for the official wiki tables. Generate schema version 1 at `optiscaler-v1.json` with a catalog version, wiki Git revision, and report entries. Verify that unsupported layouts and major row losses leave the previous catalog available.
- [x] Add the publisher workflow to the release repository's default branch. Publish only valid updates to `compatibility-catalog`. Confirm the public HTTPS endpoint and the result of the first workflow run.
- [x] Extend `Mods/ModVerification.cs` for a bounded catalog download, version handling, restart and offline fallback, and an invalid response. Implement `Mods/ModCatalogStore.cs` with the verified cache and report matching. Run the mod fixture harness.
- [x] Add a manual refresh button to the OptiScaler card using the existing action row. Update game Mods displays with matching reports while using `ModCompatibility` for setup. Capture the layout at the supported widths and verify the updated report and offline status.
- [x] Update the source articles and UI catalog. Run lint, the Windows smoke checks, and mod fixtures. Verify the launcher accepts the first published catalog.
