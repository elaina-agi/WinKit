# WinKit development instructions

## Scope and architecture

Windows-only desktop toolbox: .NET 10 LTS, C#, WPF. Keep the shell, tool modules and native file operations separate. Do not introduce dynamic plugins or third-party UI frameworks without a concrete need.

- `src/WinKit.Core`: headless native-safe file operations, no WPF dependencies.
- `src/WinKit.Desktop`: shell, module registration, views/view models and desktop services.
- `tests/WinKit.Core.Tests`: executable regression tests with no test-framework NuGet dependency.
- `scripts`: reproducible build/test/publish scripts. Generated SDK and artifacts are ignored by Git.

## Safety requirements

- Only exact `nul` (case insensitive) ordinary files are eligible. Never match `null`, `nul.txt`, folders or reparse points.
- Treat all input paths as untrusted. Do not follow links/junctions, even in the root's ancestors. Never escape the selected root.
- Enumeration must report partial failures and cancellation accurately.
- Immediately before deleting, verify the file identity from the scan and inspect the path chain. Delete through the validated native handle, not a shell command or ordinary `File.Delete`.
- Do not elevate automatically or clear read-only attributes. Permanent deletion requires explicit UI confirmation and defaults to no selected rows.
- Tests create and delete only their own temporary fixtures. Never scan/delete existing user problem files as a test.
- Logging failures must not prevent safe operations; never log success before the operation succeeds.

## Contribution and validation

Use `scripts/build.ps1`, `scripts/test.ps1`, and `scripts/publish.ps1` from PowerShell. Read `docs/ARCHITECTURE.md` before adding a tool. Run regression tests and WPF smoke checks before publishing. Review the final diff; exclude SDKs, bin/obj and outputs. Do not silently weaken checks to fix failing tests.

## UI

Chinese labels. System fonts. Calm light palette, blue primary actions, red destructive actions. Visible keyboard focus. Long paths must not destroy layout. Scanning must remain asynchronous and cancellable. Never force every future tool into the NUL scanner's workflow.
