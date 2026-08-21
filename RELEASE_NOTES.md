# Linux iCloud Calendar v1.2.0

## What changed since v1.1.0

- Added an **Update now** action directly alongside **View release** in the update banner.
- Downloads the correct x64 or ARM64 package automatically.
- Verifies the package against the release’s published SHA-256 checksum before installation.
- Rejects unexpected download locations and unsafe archive paths.
- Restarts the local calendar service automatically and reloads the app when updating finishes.
- Leaves the existing installation running when downloading or verification fails.
- Added curated, version-matched release notes so every release clearly explains what changed.

## Updating

Use **Update now** in the application’s update banner. You can still download the package manually, extract it, and run `./install.sh`. Your existing account configuration and local calendar database are preserved.

Packages are provided for Linux x64 and ARM64. Verify downloads with the included `SHA256SUMS` file.
