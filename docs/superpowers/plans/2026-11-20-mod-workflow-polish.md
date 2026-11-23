# Mod workflow polish

> Historical implementation plan. See the [current mod management reference](../../source/desktop/mods.md) for shipped behaviour.

- Reuse the Steam refresh symbol for package checks. Show an up-to-date message only when a cached package matches the current upstream asset.
- Reuse the splash bar in the game setup download and installation dialogs. Report transaction progress through the existing install service and retain the dialog until the operation finishes.
- Show additions, replacements, and removals with symbols and named theme colours. Explain the backup policy and link to upstream usage guidance. Warn about the shared Insert hotkey when the plan or record includes REFramework.
- Give installed dependencies their own Mods rows and details. Route removal through the owning OptiScaler transaction so its file and backup records govern restoration.
- Verify the update result, icon actions, download and install progress, dependency rows, and preview at the supported window widths in disposable profiles.
