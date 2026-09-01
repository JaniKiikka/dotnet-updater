# Getting Started

## Prerequisites

Install the following before building or running the application:

- .NET 10 SDK;
- Git available on `PATH`;
- a modern interactive terminal with 24-bit color support; and
- network or authenticated feed access required by the target repositories' NuGet sources.

For commit-and-push workflows, also configure:

- a Git user name and email;
- the expected remote, `origin` by default; and
- credentials that permit fetch and push.

Confirm the basic toolchain:

```sh
dotnet --version
git --version
```

The application targets `net10.0`. `global.json` configures MSTest SDK 4.3.3 and the Microsoft.Testing.Platform runner, but does not pin a specific .NET 10 SDK patch.

## Clone and restore

```sh
git clone <repository-url> dotnet-updater
cd dotnet-updater
dotnet restore dotnet-updater.slnx
```

Restoring downloads the application dependencies, including SharpConsoleUI, NuGet.Protocol, and AngleSharp, as well as test dependencies.

## Build

```sh
dotnet build dotnet-updater.slnx -m:1
```

The shared build settings enable nullable reference types, implicit usings, deterministic output, the latest analysis level, and warnings as errors. `-m:1` matches the repository's documented build command and makes build output easier to diagnose.

For a release build:

```sh
dotnet build dotnet-updater.slnx -c Release -m:1
```

## Run

Start the terminal interface from the repository root:

```sh
dotnet run --project src/DotnetUpdater/DotnetUpdater.csproj
```

Show the limited command-line help:

```sh
dotnet run --project src/DotnetUpdater/DotnetUpdater.csproj -- --help
```

The only current command-line option is `-h`/`--help`. All operational choices are made in the terminal UI.

### Keyboard controls

| Key | Action |
|---|---|
| `Tab` / `Shift+Tab` | Move focus forward or backward. |
| Arrow keys | Move through lists and controls. |
| `Space` | Toggle a selected checkbox or package choice where applicable. |
| `Enter` | Activate the focused item. |
| `Esc` | Close or cancel the current dialog. |
| `Ctrl+C` | Request cancellation at the next safe operation boundary. |

## First-use workflow

1. Start the application.
2. Enter the folder that contains the Git repositories to scan. The path is saved for future runs.
3. Review any discovery warnings.
4. Choose **Upgrade packages** or open **Package rules** first.
5. Select solutions and standalone projects.
6. Review unsupported declarations. They will be excluded automatically.
7. Choose latest minor, latest major, validated incremental, or per-package selection.
8. Choose whether to synchronize a base branch, create an update branch, and commit/push.
9. Review the immutable plan.
10. Approve read-only preflight.
11. Review which repositories are ready and give final approval.
12. Review the final summary. Record any stash references and use the log path for diagnostics.

For a cautious first run, choose the current branch, create a new update branch, leave changes uncommitted, and use validated incremental mode. Inspect the working tree afterward before committing manually.

## Prepare target repositories

The automatic NuGet query uses `--no-restore`. If version resolution reports an error, restore the affected solution or project once from its own repository:

```sh
dotnet restore path/to/Target.sln
```

Also confirm that the repository's effective `NuGet.Config` sources and credentials are usable:

```sh
dotnet nuget list source
dotnet package list --project path/to/Target.csproj --outdated --format json --no-restore
```

The updater runs validation against each selected solution or standalone project, not directly against every member `.csproj` of a selected solution.

## Settings and logs

Settings are stored below the operating system's per-user application-data directory:

```text
dotnet-updater/settings.json
```

Typical locations include `%APPDATA%\dotnet-updater\settings.json` on Windows and `$XDG_CONFIG_HOME/dotnet-updater/settings.json` or `~/.config/dotnet-updater/settings.json` on many Linux systems.

Example normalized settings:

```json
{
  "projectsFolder": "/work/projects",
  "ignoredPackages": [
    "Legacy.Package"
  ],
  "forcedPackageVersions": [
    {
      "packageId": "Example.Package",
      "version": "2.1.0-beta.2"
    }
  ],
  "developmentBranch": "development",
  "remoteName": "origin"
}
```

Prefer editing package rules in the application. Invalid configuration is preserved on disk and reported; it is not overwritten automatically.

Logs are stored below the per-user local application-data directory:

```text
dotnet-updater/logs/run-<timestamp>-<id>.log
```

If that directory cannot be created, the application falls back to the system temporary directory under `dotnet-updater/logs`.

## Run tests

Build first, then run both test projects without restoring or rebuilding:

```sh
dotnet build dotnet-updater.slnx -m:1
dotnet test tests/DotnetUpdater.UnitTests/DotnetUpdater.UnitTests.csproj --no-build --no-restore
dotnet test tests/DotnetUpdater.IntegrationTests/DotnetUpdater.IntegrationTests.csproj --no-build --no-restore
```

Run only one suite when iterating:

```sh
dotnet test tests/DotnetUpdater.UnitTests/DotnetUpdater.UnitTests.csproj --no-build --no-restore
dotnet test tests/DotnetUpdater.IntegrationTests/DotnetUpdater.IntegrationTests.csproj --no-build --no-restore
```

The integration suite creates temporary local repositories and a local bare remote. It does not use developer repositories or the public network, but it requires the Git executable and a writable temporary directory.

Use the explicit test-project commands rather than `dotnet test --solution dotnet-updater.slnx`. With the repository's current .NET 10/Microsoft.Testing.Platform setup, the solution form can also start the non-test executable project.

## Optional publish output

Create a framework-dependent release build:

```sh
dotnet publish src/DotnetUpdater/DotnetUpdater.csproj -c Release -o artifacts/dotnet-updater
```

Run the published assembly with a .NET 10 runtime:

```sh
dotnet artifacts/dotnet-updater/dotnet-updater.dll
```

The project does not currently define packaged releases or platform-specific self-contained publish profiles.
