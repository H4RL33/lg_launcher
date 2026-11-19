# LG Launcher releases

Download `LG Launcher.exe` and its SHA-256 checksum from the [latest stable release](https://github.com/H4RL33/lg_launcher/releases/latest). The Windows build requires the x64 .NET 10 Runtime.

LG Launcher checks this repository's public [latest release endpoint](https://api.github.com/repos/H4RL33/lg_launcher/releases/latest) when it starts.

## Publishing

Build and verify the executable in the launcher workspace. Commit the executable, a GNU-format SHA-256 checksum named `LG Launcher.exe.sha256`, and `RELEASE-NOTES.md` on the `release-assets/vX.Y.Z` staging branch. Push that branch before pushing the matching `vX.Y.Z` tag from `main`. The release workflow checks the checksum, uploads the files to a draft, and publishes the release.
