# Maintenance and Troubleshooting

## Routine maintenance

### Before submitting a change

Run from the repository root:

```sh
dotnet build dotnet-updater.slnx -m:1
dotnet test tests/DotnetUpdater.UnitTests/DotnetUpdater.UnitTests.csproj --no-build --no-restore
dotnet test tests/DotnetUpdater.IntegrationTests/DotnetUpdater.IntegrationTests.csproj --no-build --no-restore
```

The build treats warnings as errors. New code should remain nullable-safe, deterministic, and compatible with the latest analyzer level selected by the installed .NET 10 SDK.

### Test ownership

The unit suite covers:

- concurrency and NuGet target resolution;
- exact-version retrieval, including prereleases;
- semantic version ordering;
- configuration migration, normalization, and malformed-file preservation;
- `.slnx` membership and standalone-project discovery;
- direct and central package inventory;
- central declaration deduplication and forced downgrades;
- batch and validated incremental planning;
- stale-plan detection and XML editing;
- current-branch preflight behavior;
- execution ordering, fallback, rollback, and continuation; and
- package rule, decision, search, and progress view models.

The integration suite exercises a real local Git workflow: dirty-state detection, tracked and untracked stashing, base synchronization, update-branch creation, selective staging, commit, and push to a temporary local bare remote.

Add unit coverage for decision logic and failure handling. Add integration coverage when behavior depends on actual Git command semantics.

### Updating dependencies

Application dependencies are declared in `src/DotnetUpdater/DotnetUpdater.csproj`. Test SDK configuration is in `global.json`. After changing a version:

```sh
dotnet restore dotnet-updater.slnx
dotnet build dotnet-updater.slnx -m:1
dotnet test tests/DotnetUpdater.UnitTests/DotnetUpdater.UnitTests.csproj --no-build --no-restore
dotnet test tests/DotnetUpdater.IntegrationTests/DotnetUpdater.IntegrationTests.csproj --no-build --no-restore
```

Do not substitute `dotnet test --solution dotnet-updater.slnx` without rechecking the current toolchain behavior. With the present .NET 10/Microsoft.Testing.Platform configuration, the solution form can also start the main executable project.

Pay particular attention to changes in:

- `dotnet package list` JSON output;
- NuGet.Protocol source and credential behavior;
- SharpConsoleUI control and flow APIs;
- Microsoft.Testing.Platform command syntax; and
- Git output or exit-code semantics used by preflight.

### Adding a declaration type

Support for a new package declaration format normally requires coordinated changes to:

1. `PackageInventoryService` to identify the declaration safely;
2. `DeclarationKind` and possibly domain records;
3. `PackageEditor.Locate` to revalidate and edit the exact location;
4. `UpgradePlanner` if deduplication or target rules differ;
5. review/summary presentation; and
6. inventory, planner, editor, and execution tests.

Keep the core invariant: if a declaration cannot be located uniquely and checked against the reviewed old value, it must not be edited.

## Troubleshooting guide

### The application does not start or renders incorrectly

Symptoms:

- `.NET SDK is unavailable`;
- an unsupported target framework error;
- corrupted colors, borders, or input behavior; or
- output indicating redirected/non-interactive input.

Actions:

1. Run `dotnet --version` and confirm a .NET 10 SDK is active.
2. Run `git --version` and confirm Git is on `PATH`.
3. Use a modern interactive terminal with 24-bit color support.
4. Do not pipe the interactive application through another process.
5. Rebuild with `dotnet build dotnet-updater.slnx -m:1` and address all warnings/errors.

### No projects were found

The application exits with code `2` when it finds no selectable entries.

Check that:

- the selected folder exists and is readable;
- `.sln`, `.slnx`, or `.csproj` files exist below it;
- each file is inside a Git repository whose root is also below the selected folder;
- solution project paths are valid and remain inside the same repository; and
- projects are not available only through a skipped symbolic link or excluded directory.

Discovery warnings identify unreadable paths, broken solution members, and entries outside Git repositories.

### NuGet target resolution fails

Typical message:

```text
NuGet query failed; restore the project and verify its configured sources.
```

Run in the affected repository:

```sh
dotnet restore path/to/Project.csproj
dotnet package list --project path/to/Project.csproj --outdated --format json --no-restore
dotnet nuget list source
```

Then verify feed connectivity, credentials, and `NuGet.Config` inheritance. Automatic resolution needs at least one successful query and considers stable versions only.

### The exact-version picker cannot load versions

The picker queries all enabled NuGet sources effective for one discovered project. It reports a source error when all sources fail or no source is enabled, and it is also unavailable when successful sources return no matching versions. An unavailable secondary source is tolerated if another source succeeds.

Check:

- the package is currently discovered;
- the source is enabled;
- credentials are available to the current user;
- the source supports package version lookup; and
- the package ID exists on at least one effective source.

A persisted rule for a package that is not currently discovered can be cleared or ignored, but its version list cannot be refreshed until the package is discovered again.

### A package is reported as unsupported

Replace the declaration with a single unconditional literal version if project policy permits. For example:

```xml
<!-- Unsupported -->
<PackageReference Include="Example.Package" Version="$(ExampleVersion)" />

<!-- Supported -->
<PackageReference Include="Example.Package" Version="2.4.0" />
```

Alternatively, place a single literal `PackageVersion` entry in the nearest in-repository `Directory.Packages.props` and omit the version from `PackageReference`.

The updater intentionally does not evaluate MSBuild properties or conditions because the effective value can vary by target framework, configuration, platform, or import order.

### Preflight says a target branch already exists

The updater creates branches with `git switch --create`, so the target must be new. Choose a different branch name, or cancel and manage the existing branch manually. Preflight checks local heads, remote-tracking refs, and—when required—the remote itself.

### Preflight cannot inspect the remote

Confirm the configured remote and credentials:

```sh
git remote -v
git ls-remote --heads origin
```

The remote defaults to `origin`. There is currently no UI field for changing the remote or default development branch; those values can be changed carefully in `settings.json`.

Current-branch updates that neither synchronize a base nor commit/push do not require remote inspection.

### Preflight rejects a reviewed package value

Typical message:

```text
reviewed value 1.2.3 changed to 1.2.4
```

The project file changed after inventory and review. Cancel the run and start a new scan so the plan is rebuilt from current files. Do not bypass this check; it protects against overwriting another process or developer's edit.

### Baseline validation fails

Validated incremental mode first proves that the repository restores, builds, and tests without dependency changes. A baseline failure means the repository was already unhealthy or its environment is incomplete.

Open the retained log, run the failing command in the repository, and resolve the underlying build/test/feed problem. No package edit has been accepted at this point.

### Restore, build, or test fails after an update

Behavior depends on the strategy:

- **Batch modes:** the repository fails at that stage. Applied package edits remain in the working tree for investigation.
- **Validated incremental, first-party batch:** the batch is rolled back and the repository fails.
- **Validated incremental, third-party package:** the preferred edit is rolled back. A non-forced major update may be retried at the latest minor; a rejected package is restored and the run continues.

Use the final summary to identify the failed stage and package-level result. Use the log for full command output.

### Commit fails

Common causes include missing Git identity, commit hooks, signing configuration, or repository policy.

Check:

```sh
git config user.name
git config user.email
git status
git diff --cached
```

Only package declaration files should be staged. Existing unrelated work was stashed before execution.

### Push fails

The local commit remains available. Inspect the final summary for its commit ID, then resolve authentication, branch protection, connectivity, or non-fast-forward policy and push manually:

```sh
git push --set-upstream <remote> <branch>
```

### My previous work disappeared

If the repository was dirty, the updater stashed tracked and untracked changes. It does not restore them automatically. The final summary includes the stash reference.

Inspect before applying:

```sh
git stash list
git stash show --stat <stash-reference>
git stash show --patch <stash-reference>
```

Restore only after inspecting the current branch and working tree:

```sh
git stash apply <stash-reference>
```

Use `apply` rather than `pop` when you want the stash to remain recoverable until conflicts are resolved.

### `Ctrl+C` does not stop immediately

This is expected while a Git or `dotnet` child process is running. Commands are cancellation boundaries: the application waits for the current command to finish and then observes cancellation. This prioritizes a known final repository state over immediate termination.

### The settings file is malformed

The application reports a configuration warning and continues with defaults. It deliberately preserves the malformed file. Back it up, repair the JSON, or move it aside manually. A valid file must contain an object compatible with the example in [Getting Started](03-getting-started.md#settings-and-logs).

### Where is the detailed error output?

The final summary shows the log path. Logs contain every invoked command, standard output, and standard error. The application redacts common URL/query credential patterns, but logs may still contain proprietary project paths, package names, source names, or build output; handle them as internal development artifacts.

## Known limitations

- Only `.sln`, `.slnx`, and `.csproj` entry types are discovered. Other project types are not selectable directly.
- Only direct `PackageReference` and nearest central `PackageVersion` declarations with literal semantic versions are editable.
- MSBuild properties, imported version files other than `Directory.Packages.props`, conditions, ranges, wildcards, and ambiguous declarations are unsupported.
- Automatic update lookup selects stable versions only. Prereleases require a forced exact-version rule.
- A grouped package lookup uses a representative project for each current major, and the exact-version picker uses one discovered project. Repositories with materially different effective NuGet sources may expose different package availability; restore/build/test validation remains the final check.
- Semantic version handling is intentionally lightweight and is not a complete replacement for all NuGet version-range semantics.
- There is no headless/batch command-line interface, HTTP API, scheduler, or CI-specific mode.
- Repository execution is sequential. Large repository sets or validated incremental runs can take substantial time because each accepted attempt runs restore, build, and test.
- The same validation command is run for every selected solution or standalone project; overlapping selections can repeat work.
- Existing work is stashed but never restored automatically.
- Failed batch validation leaves package edits in the working tree. Automated rollback is specific to validated incremental paths.
- A created branch is not deleted automatically after a later failure.
- A failed push is not retried automatically.
- Remote and default development branch settings are persisted in JSON but are not currently edited through a dedicated settings screen.
- Log redaction covers common credential patterns, not every possible secret format.

## FAQ

### Does the updater change transitive packages?

No. It changes only supported direct or centrally managed declarations. Transitive versions may change indirectly when restore recalculates the dependency graph.

### Can one forced version downgrade a package?

Yes. Exact forced versions are applied even when lower than the current version. The review marks them as forced.

### Can different repositories stay on different majors?

Validated incremental fallback tracks a minor target for each working major, so different repositories can retain different majors after a failed preferred update. Standard latest-minor mode instead targets the highest major already present among the selected occurrences; it may bring older occurrences up to that major.

### Are all repositories changed if one fails preflight?

No. Preflight results are per repository. After final approval, only ready repositories run; blocked repositories are recorded as skipped.

### Does a failing repository stop later repositories?

No. Ordinary repository failures are returned as results, and the coordinator continues to the next planned repository. Explicit cancellation stops the sequence at a safe boundary.

### Is the initial review enough to start changes?

No. The first approval authorizes read-only preflight. A second approval is required before ready repositories are mutated.
