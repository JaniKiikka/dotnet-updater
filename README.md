# .NET Multi-Project Updater

**Vibe-coded .NET package updater made to update multiple repositories at once.**

A keyboard-driven .NET 10 app for reviewing and applying NuGet package updates across multiple Git repositories. It discovers projects, groups shared packages, and walks each repository through a careful update, build, test, and optional push.

## What it does

1. Finds `.sln`, `.slnx`, and standalone `.csproj` files inside Git repositories.
2. Lets you select projects and ignore packages you do not want to touch.
3. Groups direct and centrally managed packages by package ID.
4. Resolves package targets concurrently using a processor-aware worker count (between two and eight), then offers minor or major upgrades with major updates clearly marked.
5. Preflights every repository and shows the exact package and Git plan.
6. Applies approved updates one repository at a time, then restores, builds, and tests.

You decide whether to stay on the current branch, sync from a base branch, create an update branch, and commit or push successful changes. Nothing is modified before the final approval.

## Run it

You will need the .NET 10 SDK, Git on `PATH`, and a modern terminal with 24-bit color support. Target repositories may need a `dotnet restore` before the app can query outdated packages.

```sh
dotnet run --project src/DotnetUpdater/DotnetUpdater.csproj
```

Use `Tab` and `Shift+Tab` to move focus, the arrow keys to navigate, `Space` to toggle selections, and `Enter` to activate an item. `Esc` closes a dialog; `Ctrl+C` requests cancellation between operations.

## What it can update

- `PackageReference Version="..."`
- A literal `<Version>...</Version>` inside `PackageReference`
- `PackageVersion` entries in the nearest in-repository `Directory.Packages.props`

The updater sticks to stable versions. Conditional declarations, properties, ranges, wildcards, missing versions, and ambiguous duplicates are reported and left alone.

## Settings and logs

Settings live in the operating system's per-user application-data directory at `dotnet-updater/settings.json`. This includes the ignored-package list, which you can edit from **Ignored packages** in the app. Invalid configuration is preserved and reported rather than overwritten.

Logs are written under the per-user local application-data directory at `dotnet-updater/logs`, falling back to the system temporary directory when needed. Command output is redacted, and files use user-only permissions on Unix-like systems where supported.

The default remote is `origin`. `development` is only suggested as a base branch when you opt into branch synchronization; otherwise, the current branch stays put.

## Build and test

```sh
dotnet build dotnet-updater.slnx -m:1
dotnet test --solution dotnet-updater.slnx --no-build --no-restore
```

The MSTest unit and integration suites use the Microsoft.Testing.Platform runner configured in `global.json`. Integration tests work only with temporary local repositories and a local bare remote—never developer repositories or the public network.
