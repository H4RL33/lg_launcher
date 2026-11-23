# OptiScaler DXGI spoofing

> Historical implementation plan. See the [current mod management reference](../../source/desktop/mods.md) for shipped behaviour.

- Reuse the existing settings selector for Automatic, Enabled, and Disabled in both setup paths. Default to the verified recipe's choice where one exists and to Automatic for guided setup. Tell the user which mode the preview will apply.
- Read and change only the `Dxgi` entry under `[Spoofing]` in the OptiScaler INI. Preserve unrelated keys, whitespace and encoding. Decline missing, ambiguous, or unsupported configurations.
- Allow editing a launcher-managed installation with the game closed. Revalidate the game, owned file, and config snapshot at save time. Use the existing file transaction and keep original backups and edited-config protections.
- Verify install choices using the approved archive, edits and rollback with disposable managed installs, existing user edits, concurrency checks, and the setup and management UI at supported widths.
