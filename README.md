# .NET Multi-Project Updater

A cross-platform .NET 10 terminal application, built with SharpConsoleUI, for reviewing and safely applying NuGet package upgrades across multiple Git repositories.

## MVP workflow

The responsive, keyboard-driven TUI lets you:

1. Configure a projects folder and discover `.sln`, `.slnx`, and unreferenced standalone `.csproj` entries under Git repositories.
2. Choose whether to start an upgrade or manage ignored packages in a dedicated checkbox view built from the combined project inventory.
3. Select entries and combine direct and centrally managed packages by package ID.
4. Choose latest minor, latest major, or cycle package-by-package targets in one scrollable view.
5. Choose whether to update the current branch, optionally synchronize a selected base branch, optionally create any valid new update branch, and optionally commit and push successful changes.
6. Review exact target versions and Git behavior, approve read-only preflight results, then process ready repositories sequentially through only the selected Git stages, edit, restore, build, and test.

Major updates are called out during selection and review. Normal console output shows stages rather than command diagnostics; complete redacted output is retained in the run log.

## Requirements and run command

- .NET SDK 10
- Git available on `PATH`
- A modern terminal with 24-bit color support (Windows 10 1511+/Server 2016+, macOS 10.15+, or modern Linux)
- Restored project assets for read-only outdated-package queries (`dotnet restore` in target repositories when needed)

```sh
dotnet run --project src/DotnetUpdater/DotnetUpdater.csproj
```

Use `Tab`/`Shift+Tab` to move focus, arrow keys to navigate lists, `Space` to toggle solution checkboxes, and `Enter` to activate the focused item. `Esc` cancels a dialog; Ctrl+C requests cancellation between operations.

The app does not modify anything until every repository has been preflighted and the final approval action is explicitly activated.

## Supported declarations

- Literal `PackageReference Version="..."`
- Literal child `<Version>...</Version>` inside `PackageReference`
- Literal `PackageVersion` entries in the nearest in-repository `Directory.Packages.props`

Conditional declarations, MSBuild property expressions, version ranges, wildcards, missing declarations, and ambiguous duplicate declarations are reported as unsupported and left unchanged. Stable versions are selected; prereleases are not considered.

## Safety and recovery

- Pre-existing tracked and untracked work is placed in a named stash. It is never popped automatically.
- Pulls use `--ff-only`; pushes are never forced.
- Existing target branches are never overwritten or deleted.
- Package files are revalidated against reviewed old values immediately before editing.
- Only declaration paths from the approved plan are staged.
- Build or test failures leave package edits uncommitted on the branch being updated.
- Push failures leave the local commit intact.

When automatic commit and push is disabled, successful package edits remain uncommitted for the user to review. The final summary reports stash references, actual branch names, commit IDs and pushed branches when applicable, and the run-log path. Recover stashed work deliberately with `git stash apply <ref>` after inspecting the affected branch.

## Configuration and logs

Configuration is stored in the operating system's per-user application-data directory as `dotnet-updater/settings.json`. Logs are stored below the per-user local application-data directory in `dotnet-updater/logs`; if that location is unavailable, logging falls back to the system temporary directory. Files contain redacted command output and use user-only permissions on Unix-like systems where supported.

The ignored-package selection is saved in that settings file and restored at startup. Use the **Ignored packages** action to check or uncheck package IDs; the app does not ask for a comma-separated list.

`development` is the suggested base branch only when the user opts into base-branch synchronization; updating the current branch is the no-switch path. The remote defaults to `origin`. A malformed configuration file is preserved and reported instead of being overwritten.

## Build and tests

The unit and integration suites use MSTest with `MSTest.Sdk` and the default Microsoft.Testing.Platform runner, as configured in `global.json`.

```sh
dotnet build dotnet-updater.slnx -m:1
dotnet test --solution dotnet-updater.slnx --no-build --no-restore
```

The integration suite creates only temporary local repositories and a local bare remote. It verifies stashing untracked work, fast-forward synchronization, arbitrary new branch creation, selective staging, commit, and push. It never touches developer repositories or the public network.

## MVP boundaries

This release does not create pull requests, select prereleases, update SDKs/frameworks, repair failures, restore stashes, run repositories concurrently, or show inline build/test diagnostics. SharpConsoleUI is confined to the presentation layer; application services and immutable planning models remain independent of terminal controls.
