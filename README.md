# LG Launcher releases

Download the Windows setup executable or the portable `LG-Launcher.exe` from the [latest stable release](https://github.com/H4RL33/lg_launcher/releases/latest). The Windows build requires the x64 .NET 10 Runtime.

LG Launcher checks this repository's public [latest release endpoint](https://api.github.com/repos/H4RL33/lg_launcher/releases/latest) when it starts.

## Publishing

Build and verify the executable in the launcher workspace. Commit it as `LG-Launcher.exe` with a GNU-format SHA-256 checksum named `LG-Launcher.exe.sha256` and `RELEASE-NOTES.md` on the `release-assets/vX.Y.Z` staging branch. Push that branch before pushing the matching `vX.Y.Z` tag from `main`. The Windows release workflow verifies the portable executable, builds and smoke-tests `LG-Launcher-Setup-X.Y.Z.exe` from it, and uploads the portable executable, its checksum, and setup to a draft before publishing.

After publishing, the workflow checks all three public downloads and deletes the staging branch if it still points to the commit it verified. If the release is already published and the staging branch is still present, a retry checks the existing downloads before attempting cleanup again.

To test packaging against a staged build, push a `verify-packaging-vX.Y.Z` tag from `main`. Add a numeric suffix such as `-2` for another run. This runs the same Windows build and smoke test and leaves the staging branch available for publication. To run this check again after cleanup, restore the staging branch first.

For historical releases that lack setup, stage a tested `LG-Launcher-Setup-X.Y.Z.exe` with its GNU-format SHA-256 checksum on `installer-assets/vX.Y.Z`, then push the matching `installer-vX.Y.Z` tag from `main`.
