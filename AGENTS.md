# Repository Guidelines

## Project Structure & Module Organization

The solution is organized under `src/` by responsibility:

- `ICloudCalendar.Core` contains domain models, sync contracts, and scheduling policy.
- `ICloudCalendar.Infrastructure` implements CalDAV access, credential storage, and SQLite persistence.
- `ICloudCalendar.Web` hosts the ASP.NET Core service, background workers, API models, and static UI in `wwwroot/`.

Automated tests live in `tests/ICloudCalendar.Core.Tests`; test filenames generally match the production type or feature. Deployment scripts, systemd and desktop files, and the Plasma widget are under `packaging/`. Treat `packaging/app/` as generated publish output rather than source.

## Build, Test, and Development Commands

Use the .NET SDK selected by `global.json` (currently .NET 10):

```bash
dotnet restore ICloudCalendar.slnx
dotnet build ICloudCalendar.slnx --configuration Release --no-restore
pwsh tests/ICloudCalendar.Core.Tests/bin/Release/net10.0/playwright.ps1 install chromium
dotnet test ICloudCalendar.slnx --configuration Release --no-build
dotnet run --project src/ICloudCalendar.Web
dotnet format ICloudCalendar.slnx --no-restore --verify-no-changes
```

The first four commands mirror CI. Install Chromium after building and before the first full test run. The run command starts the local ASP.NET Core application.

## Coding Style & Naming Conventions

Use four-space indentation in C# and follow standard .NET naming: PascalCase for types, methods, and public members; camelCase for parameters and locals; `_camelCase` for private fields. Nullable references and implicit usings are enabled. Keep namespaces aligned with project folders and favor small, responsibility-focused types. Builds treat warnings as errors and enable the latest recommended analyzer rules; run `dotnet format` before submitting.

## Testing Guidelines

Tests use xUnit, Shouldly, and NSubstitute; browser smoke tests use Playwright. Name test classes `<Subject>Tests` and test methods as readable behavior statements, such as `NextDelayCapsBackoffAtFifteenMinutes`. Add unit tests for policy and domain changes, integration tests for CalDAV/SQLite boundaries, and browser coverage for user-visible workflows. No fixed coverage percentage is specified, but all tests must pass in Release mode.

## Commit & Pull Request Guidelines

This checkout does not expose Git history, so no repository-specific commit pattern can be verified. Use concise, imperative subjects (for example, `Handle expired CalDAV sync tokens`) and keep each commit focused. Pull requests should explain the behavior change, testing performed, and any security, migration, or packaging impact. Link relevant issues and include screenshots for web or Plasma UI changes. Update `RELEASE_NOTES.md` before publishing a semantic-version tag such as `v1.3.0`.

## Security & Configuration

Never commit Apple IDs, app-specific passwords, authorization headers, calendar content, keyring data, or local SQLite databases. Credentials belong only in the Linux Secret Service. Keep logs and test fixtures synthetic and redacted.
