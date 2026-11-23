# OptiScaler compatibility catalog updates

> Historical design. See the [current compatibility and mod management reference](../../source/desktop/mods.md) for shipped behaviour.

LG Launcher users should receive current OptiScaler wiki reports between executable releases. The launcher shows compatibility immediately on startup, including when offline. The existing game install and uninstall flows continue to use approved packages and tested recipes.

## Approaches

- Refreshing the bundled snapshot before each launcher release would keep the binary self-contained. Users would wait for the next executable release for any subsequent corrections.
- Fetching a generated data file at runtime meets the independent update requirement. Use a dedicated branch in the existing LG Launcher GitHub publication repository so catalog commits can ship on their own schedule. The executable updater remains on the existing releases endpoint.

## Publication

A scheduled GitHub Actions workflow reads the official OptiScaler wiki Markdown at a recorded Git revision. It publishes changed data to the catalog branch after the importer validates the table structure and output. Failed imports leave the published catalog available. The file carries a format version, an increasing catalog version, and the upstream revision. A rollback publishes the earlier entries with a new catalog version. [OptiScaler wiki history](https://github.com/optiscaler/OptiScaler/wiki/Compatibility-List/_history) · [Current launcher update feed](../../source/desktop/updates.md)

## Launcher behavior

Load the last validated catalog from the launcher's local `mods` directory. Use the bundled snapshot on first run or if the cache is unreadable. Check the fixed HTTPS endpoint in the background, at startup when a check is due and at most once a day while the launcher stays open. Provide **Refresh compatibility** on the OptiScaler card in Mods settings. Its tooltip shows the active catalog version and last successful check. The status bar reports the result of a manual check. A successful refresh updates the selected game's compatibility status.

Validate the schema, version, entry sizes, supported report labels, and wiki link targets before replacing the cache. Publish the validated cache atomically. A failed fetch, invalid response, or older catalog leaves the active copy available. The remote format carries game identity, reported inputs, notes, and wiki page references. It can update the display with **Reported compatible** or **See compatibility notes**. The launcher continues to determine setup eligibility with its bundled restrictions, tested recipes, approved package digests, and local checks.

## Verification

Use a fixture server or injected HTTP handler to exercise first run, successful updates, offline startup, timeout, malformed or oversized data, unsupported formats, an older version, and cache write failure. Verify that refreshing updates visible compatibility notes and that package checks and existing installations still work. Use a fixture library to verify Mods settings and game dialogs.
