# Core Features and Internal API

## User-facing features

### Repository-aware discovery

`DiscoveryService` groups selectable solutions and standalone projects by their containing Git repository. Solution members are validated to ensure they exist and remain inside the repository. Broken references and projects outside Git repositories become warnings rather than fatal scan errors.

Example input tree:

```text
/work/projects/
├── service-a/.git/
│   ├── ServiceA.slnx
│   └── src/ServiceA/ServiceA.csproj
└── tool-b/.git/
    └── ToolB.csproj
```

Conceptual discovery result:

```text
service-a -> ServiceA.slnx -> src/ServiceA/ServiceA.csproj
tool-b    -> ToolB.csproj  -> ToolB.csproj
```

### Package inventory and grouping

Package occurrences are grouped by package ID across the selected entries. This lets one user decision cover the same dependency in multiple projects or repositories while preserving declaration-specific current versions.

Ignored packages are removed before version resolution. Unsupported declarations are kept as diagnostic occurrences so the UI can explain why they are excluded.

### Stable automatic targets and exact-version rules

- **Latest minor** stays within the highest major already present among the selected occurrences. An occurrence on an older major can move to that already-selected higher major.
- **Latest major** selects the latest stable version.
- **Exact version** can select stable or prerelease versions and can intentionally downgrade.
- **No update** leaves all occurrences of the package unchanged.

Forced exact versions and ignored packages persist between runs. An ignored rule wins over a forced rule during configuration normalization, so a package cannot be both ignored and forced.

### Immutable review and preflight

The plan lists each repository, validation target, declaration, old version, new version, major jump, forced rule, Git branch action, and delivery choice. Preflight then checks:

- repository, validation target, and declaration paths;
- `git` and `dotnet` availability;
- Git working-tree status;
- branch name syntax and ref collisions;
- base branch availability;
- remote access when required;
- detached `HEAD` for direct current-branch updates; and
- the continued presence of every reviewed XML value.

A repository with preflight issues is skipped. Other ready repositories may still run after final approval.

### Validation and delivery

Each validation cycle performs, in order, for every selected validation target:

```text
dotnet restore <target>
dotnet build <target> --no-restore
dotnet test <target> --no-build --no-restore
```

If delivery is enabled, only package declaration paths returned by the editor are staged. The generated commit message is:

```text
<branch-name> .NET nuget package update
```

## Command-line contract

The application has no non-interactive update command. Its supported command-line behavior is:

| Invocation | Result |
|---|---|
| `dotnet-updater` | Open the interactive terminal workflow. |
| `dotnet-updater -h` | Print help and exit successfully. |
| `dotnet-updater --help` | Print help and exit successfully. |

### Exit codes

| Code | Meaning |
|---:|---|
| `0` | Workflow completed without a failed repository, was declined at final approval, or had nothing eligible to change. |
| `1` | A repository failed or an unexpected UI/workflow error occurred. |
| `2` | Discovery found no selectable solution or standalone project inside a Git repository. |
| `130` | The workflow was cancelled, including a `Ctrl+C` cancellation. |

## Internal service API

The project does not publish a supported binary SDK, but its service boundaries are useful for maintenance, testing, and future automation.

### `DiscoveryService.Scan`

```csharp
DiscoveryResult Scan(string projectsFolder)
```

Input:

```csharp
var result = new DiscoveryService().Scan("/work/projects");
```

Output shape:

```csharp
public sealed record DiscoveryResult(
    ImmutableArray<RepositoryInfo> Repositories,
    ImmutableArray<DiscoveryWarning> Warnings);
```

`result.Entries` flattens all repository entries. Paths are canonical absolute paths.

### `PackageInventoryService.Read`

```csharp
InventoryResult Read(
    IEnumerable<SelectionEntry> selected,
    IReadOnlySet<string> ignoredPackages)
```

Example:

```csharp
var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "Legacy.Package"
};

InventoryResult inventory = new PackageInventoryService().Read(
    selectedEntries,
    ignored);
```

Output:

```csharp
public sealed record InventoryResult(
    ImmutableArray<PackageOccurrence> Occurrences,
    ImmutableArray<string> Warnings);
```

An occurrence with a non-null `UnsupportedReason` is diagnostic-only and must not become a `DeclarationEdit`.

### `NuGetVersionService.ResolveAllAsync`

```csharp
Task<ImmutableArray<PackageGroup>> ResolveAllAsync(
    IReadOnlyList<IGrouping<string, PackageOccurrence>> sources,
    IProgress<string>? progress,
    CancellationToken cancellationToken)
```

Example:

```csharp
var groups = inventory.Occurrences
    .Where(x => x.UnsupportedReason is null)
    .GroupBy(x => x.PackageId, StringComparer.OrdinalIgnoreCase)
    .ToArray();

var resolved = await versionService.ResolveAllAsync(
    groups,
    new Progress<string>(Console.WriteLine),
    cancellationToken);
```

Representative `PackageGroup`:

```csharp
new PackageGroup(
    PackageId: "Example.Package",
    Occurrences: occurrences,
    LatestMinor: new SemanticVersion(2, 8, 0),
    LatestMajor: new SemanticVersion(3, 1, 0),
    ResolutionError: null)
{
    LatestMinorByMajor = ImmutableDictionary<int, SemanticVersion>.Empty
        .Add(1, new(1, 9, 0))
        .Add(2, new(2, 8, 0))
};
```

Use `GetAllVersionsAsync(projectPath, packageId, token)` for an exact-version picker. Its `PackageVersionLookup.Versions` includes stable and prerelease versions from all successfully queried enabled sources.

### `UpgradePlanner.Create`

```csharp
UpgradePlan Create(
    IEnumerable<SelectionEntry> selectedEntries,
    IEnumerable<PackageGroup> groups,
    IReadOnlyDictionary<string, PackageDecision> decisions,
    GitWorkflowOptions git,
    DateTimeOffset createdAt)
```

Example decision and Git workflow:

```csharp
var decisions = new Dictionary<string, PackageDecision>(
    StringComparer.OrdinalIgnoreCase)
{
    ["Example.Package"] = new(
        "Example.Package",
        UpgradeChoice.LatestMinor,
        "2.8.0")
};

var git = new GitWorkflowOptions(
    RemoteName: "origin",
    BaseBranch: "development",
    TargetBranch: "dependency-updates/september",
    CommitAndPush: true);

UpgradePlan plan = planner.Create(
    selectedEntries,
    resolvedGroups,
    decisions,
    git,
    DateTimeOffset.UtcNow);
```

`CreateValidatedIncremental` accepts forced versions rather than per-package decisions and produces `ValidatedPackageUpdate` records with preferred and fallback edits.

### `PreflightService.InspectAsync`

```csharp
Task<ImmutableArray<RepositoryPreflight>> InspectAsync(
    UpgradePlan plan,
    CancellationToken cancellationToken)
```

Example output:

```csharp
new RepositoryPreflight(
    RepositoryRoot: "/work/projects/service-a",
    IsReady: false,
    Issues:
    [
        new PreflightIssue(
            "/work/projects/service-a",
            "Target branch dependency-updates/september already exists.")
    ]);
```

Preflight is intended to be read-only. Remote branch inspection can still require network access and authentication.

### `PackageEditor.Validate` and `Apply`

```csharp
EditValidation Validate(IEnumerable<DeclarationEdit> edits)
EditResult Apply(IEnumerable<DeclarationEdit> edits)
```

Example edit:

```csharp
var edit = new DeclarationEdit(
    RepositoryRoot: "/work/projects/service-a",
    DeclarationPath: "/work/projects/service-a/Directory.Packages.props",
    PackageId: "Example.Package",
    OldVersion: "2.4.0",
    TargetVersion: "2.8.0",
    Kind: DeclarationKind.CentralPackageVersion,
    Locator: "PackageVersion:Example.Package");

EditValidation validation = editor.Validate([edit]);
EditResult result = validation.IsValid
    ? editor.Apply([edit])
    : new EditResult(false, [], validation.Error);
```

`Apply` returns the unique changed paths needed for selective Git staging.

### `RunCoordinator.ExecuteAsync`

```csharp
Task<ImmutableArray<RepositoryRunResult>> ExecuteAsync(
    UpgradePlan plan,
    IReadOnlyDictionary<string, RepositoryPreflight> preflight,
    IProgress<ProgressEvent>? progress,
    CancellationToken cancellationToken)
```

Example:

```csharp
var readyByRoot = preflightResults.ToDictionary(
    x => x.RepositoryRoot,
    StringComparer.Ordinal);

var results = await coordinator.ExecuteAsync(
    plan,
    readyByRoot,
    new Progress<ProgressEvent>(e =>
        Console.WriteLine($"{e.RepositoryRoot}: {e.Stage} - {e.Message}")),
    cancellationToken);
```

Key output fields:

- `Status` and `FailedStage` identify the repository outcome.
- `StashReference` identifies preserved pre-existing work.
- `BranchName`, `CommitId`, and `RemoteBranch` identify Git output.
- `ChangedPackages` lists accepted edits.
- `PackageResults` distinguishes updated, fallback-updated, and rejected packages in validated incremental mode.
- `LogPath` points to the retained command log.

## Configuration API

`JsonConfigurationStore` persists an `AppConfiguration` record. `SaveAsync` normalizes entries and replaces the settings file through a temporary file. `LoadAsync` returns both a configuration and an optional warning, allowing the UI to continue with defaults without destroying malformed user data.

Normalization rules include:

- trimming paths, package IDs, versions, branch names, and remote names;
- case-insensitive sorting and deduplication of ignored package IDs;
- accepting only forced versions parseable by `SemanticVersion`;
- keeping the last forced version for a duplicate package ID;
- removing forced rules for ignored packages; and
- defaulting an empty development branch to `development` and an empty remote to `origin`.
