# Development and release workflow

[Developer documentation](README.md) · [Build and test guide](source/build-and-tests.md)

| Branch | Purpose | How changes arrive |
| --- | --- | --- |
| `dev` | Ordinary development and unslated work | Squash a focused topic pull request into `dev`. |
| `next` | Integration testing for the next release | Open a pull request from a topic based on `next` with only selected `dev` commits. |
| `main` | Stable source and release tags | Merge a versioned release pull request from this repository's `next` using a merge commit. |

## Develop on dev

1. Fetch `origin` and branch from `origin/dev`. Agents use `codex/<short-topic>` for their working branches. Verify the active branch before editing and keep unrelated working-tree changes intact.
2. Keep commits focused so they can be promoted independently. Include relevant documentation and regression coverage. Use the [build and test guide](source/build-and-tests.md) to choose checks.
3. Open the pull request against `dev`. Mark the release scope **Unslated** by default. Squash to a descriptive commit so each completed change has a clear promotion unit.

## Promote selected work to next

1. Once a maintainer selects a change, cut a topic branch from `origin/next`. Cherry-pick the approved commits from `dev` with `git cherry-pick -x <commit>` in dependency order. Keep the rest of `dev` out of the promotion.
2. Open a pull request against `next`. List the original `dev` commits, dependencies, and checks in the pull request. Windows integration runs desktop, portable updater, and Setup smoke tests.
3. When integration finds a bug in a selected change, fix it on `dev` and promote the fix through the same path. Prepare version and release notes once the release scope is agreed.

## Publish from main

1. Bring `next` up to date with `main`, set the approved three-part version, and open `next` → `main` with the title `chore(release): vX.Y.Z`. Include the tested `next` commit and the user-facing release notes.
2. Wait for the required **Release integration** check and review the results before creating the merge commit. Follow the [stable release procedure](source/build-and-tests.md#stable-windows-releases) to stage the assets and tag the resulting `main` commit as `vX.Y.Z`. Obtain authorization before publishing a release.
3. Merge `main` back into `next` and `dev` after publication so both branches receive the released version and history. Preserve unslated changes on `dev` when resolving overlaps.

The [main branch ruleset](https://github.com/H4RL33/lg_launcher/rules) requires a pull request, a merge commit, and a successful **Release integration** check from GitHub Actions with the current `main`. It blocks force pushes and deletion and applies to administrators. Required approvals are set to zero so the maintainer can merge their own release pull request after CI passes.

## Commit, pull request, and release templates

Use `type(scope): imperative summary` for commit subjects, pull request titles, and merge subjects. Common types are `feat`, `fix`, `docs`, `test`, `refactor`, `perf`, `build`, `ci`, `chore`, and `revert`. Use a concrete scope such as `library`, `artwork`, `updater`, or `release`. Examples include `fix(artwork): keep shared Steam downloads alive` and `test(updater): cover restart timeouts`.

- **Commits** — Add the reason for the change and verification results in the body. For interactive commits, the [commit message template](../.gitmessage) can be enabled with `git config --local commit.template .gitmessage`.
- **Pull requests** — Fill in Summary, Verification, and Release scope from the [pull request template](../.github/pull_request_template.md). Include an issue or originating commit when applicable.
- **Release notes** — Use `# LG Launcher vX.Y.Z`, then short user-facing entries under `## Added`, `## Changed`, and `## Fixed` as applicable. Include upgrade instructions when users need to take action. Save the notes as `RELEASE-NOTES.md` on the release staging branch.
