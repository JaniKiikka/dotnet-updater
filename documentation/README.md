# .NET Multi-Project Updater Documentation

This documentation is intended primarily for a developer joining the project. The overview and workflow descriptions are also suitable for technical stakeholders who need to understand the application's behavior and safety model.

## Documentation map

1. [Project overview](01-project-overview.md) — purpose, scope, supported inputs, and safety guarantees.
2. [Architecture and design](02-architecture-and-design.md) — components, data flow, design choices, and workflow diagrams.
3. [Getting started](03-getting-started.md) — prerequisites, setup, build, run, test, and first-use instructions.
4. [Core features and internal API](04-core-features-and-api.md) — user workflows and the principal C# service interfaces.
5. [Maintenance and troubleshooting](05-maintenance-and-troubleshooting.md) — maintenance tasks, diagnostics, common errors, and limitations.

## Quick start

From the repository root:

```sh
dotnet restore dotnet-updater.slnx
dotnet build dotnet-updater.slnx -m:1
dotnet run --project src/DotnetUpdater/DotnetUpdater.csproj
```

The application is an interactive terminal UI. Run `dotnet run --project src/DotnetUpdater/DotnetUpdater.csproj -- --help` for the command-line help text.

## Source layout

```text
dotnet-updater/
├── src/DotnetUpdater/
│   ├── Configuration/   # Persistent user settings
│   ├── Discovery/       # Repository, solution, and project discovery
│   ├── Domain/          # Immutable records, enums, and workflow state
│   ├── Execution/       # Preflight, XML edits, Git, processes, and orchestration
│   ├── Packages/        # Package inventory and NuGet version resolution
│   ├── Planning/        # Immutable upgrade-plan construction
│   └── Presentation/    # SharpConsoleUI terminal workflow
├── tests/
│   ├── DotnetUpdater.UnitTests/
│   └── DotnetUpdater.IntegrationTests/
├── documentation/
├── dotnet-updater.slnx
└── global.json
```

