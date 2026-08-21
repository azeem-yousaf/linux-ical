# iCloud Calendar for Linux

A fast, local-first calendar experience for iCloud, built on .NET 10, with a responsive agenda and a native KDE Plasma 6 widget for CachyOS.

## Architecture direction

- Apple Calendar is accessed server-side through CalDAV using an Apple app-specific password.
- Incremental CalDAV sync tokens avoid repeatedly downloading entire calendars; changed event bodies are fetched in efficient batched `calendar-multiget` requests, and expired tokens recover with an atomic full refresh in the same attempt.
- The calendar list is rediscovered every six hours, so calendars added or removed in iCloud reconcile automatically without reconnecting.
- Remote pages are merged and committed atomically, so a failed request cannot leave a half-synced calendar.
- The browser UI and CachyOS widget read the same local projection; passwords are never persisted in browser storage or SQLite.
- Events can be created in any connected calendar from the browser UI and are written directly to iCloud over CalDAV.
- The app checks GitHub Releases on startup and shows a download link when a newer semantic version is available.
- The browser and widget can request an immediate sync, while the background loop and local views refresh every second.

## Run locally

Install the .NET 10 SDK, then:

```bash
dotnet restore ICloudCalendar.slnx
dotnet build ICloudCalendar.slnx --configuration Release
pwsh tests/ICloudCalendar.Core.Tests/bin/Release/net10.0/playwright.ps1 install chromium
dotnet test ICloudCalendar.slnx
dotnet run --project src/ICloudCalendar.Web
```

The xUnit suite includes a headless Playwright test that starts the real local web process, drives Chromium through the agenda and credential-management UI, and fails on browser console or page errors. It uses synthetic API responses and never requires an Apple Account password.

Open the URL printed by ASP.NET Core and choose **Connect iCloud**. Account setup, app-specific password replacement, adding another account, and complete disconnection are all available from the UI. Passwords are verified against Apple and stored only in the Linux Secret Service.

## Releases

Every push and pull request to `main` installs Playwright Chromium and runs the full unit, integration, and browser suite. Pushing a semantic version tag such as `v0.1.0` runs the same gate, creates self-contained Linux x64 and ARM64 archives, generates SHA-256 checksums, and publishes a GitHub Release only if all tests and packages succeed.

Each release includes the Plasma 6 **iCloud Agenda** widget, a user-level systemd service, and `install.sh`. On CachyOS with KDE Plasma, extract the archive and run:

```bash
./install.sh
```

No root access is required. The installer expects `secret-tool` (provided by `libsecret`) and `kpackagetool6`, then starts the local-only calendar service, adds **iCloud Calendar** to the application menu, and installs the widget for the current user.

## Security baseline

Do not commit an Apple ID or app-specific password. App-specific passwords are stored only in the Linux Secret Service (`secret-tool`); account and calendar metadata live in SQLite. Logs redact authorization headers and calendar content by default. Disconnecting from the UI removes both the keyring entry and all locally cached data for that account.

## Current status

Implemented: validated event model, paginated incremental-sync orchestration with invalid-token recovery, atomic SQLite apply/checkpoint boundary, conflict collapse, CalDAV reads and event creation, RFC 5545 recurrence/exception projection with daylight-saving handling, periodic projection rebuilds, indexed local agenda reads, complete UI account management and manual sync, end-time ranges in both agenda views, GitHub update notifications, a Plasma 6 widget package, and a test-gated GitHub release workflow. Active and idle clients synchronize and refresh every second, with bounded backoff during outages.
