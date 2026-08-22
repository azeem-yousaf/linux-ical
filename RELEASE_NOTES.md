# Linux iCloud Calendar v1.3.2

## What changed since v1.3.1

- Consolidated the view selector, current period, navigation, calendar filter, and add-event action into a compact desktop toolbar, giving the calendar more vertical space.
- Added a persistent calendar visibility selector so each connected calendar can be shown or hidden without disabling synchronization.
- Corrected all-day event placement across local timezones east and west of UTC while preserving timed-event instant conversion and daylight-saving behavior.
- Added properly sized PNG and web-app manifest icons so Chromium app windows use the calendar logo in title bars and taskbars.
- Expanded browser and application regression coverage for the toolbar layout, responsive breakpoints, calendar filtering, icons, and manifest.

## Updating

Use **Update now** in the application’s update banner. You can still download the package manually, extract it, and run `./install.sh`. Your existing account configuration and local calendar database are preserved.

Packages are provided for Linux x64 and ARM64. Verify downloads with the included `SHA256SUMS` file.
