# Project Overview

## Summary

.NET Multi-Project Updater is a keyboard-driven .NET 10 terminal application for reviewing and applying NuGet package updates across multiple local Git repositories in one run.

Teams that maintain many .NET solutions commonly repeat the same work in every repository: find outdated packages, choose safe target versions, edit project files, restore, build, test, and optionally create and push a Git commit. This application centralizes that process while keeping the user in control of package choices and Git behavior.

The application does not modify a repository until it has:

1. discovered eligible projects and declarations;
2. resolved update targets;
3. shown an immutable upgrade plan for review;
4. completed a read-only preflight; and
5. received final approval.

## Primary use cases

- Update direct NuGet dependencies across several repositories.
- Update shared package versions stored in `Directory.Packages.props` without applying duplicate edits.
- Restrict automatic updates to the latest stable minor or latest stable major version.
- Select an update policy independently for each package.
- Persistently ignore a package or force it to an exact stable or prerelease version.
- Validate package changes with `dotnet restore`, `dotnet build`, and `dotnet test`.
- Optionally synchronize a base branch, create an update branch, commit only package declaration files, and push the result.
- Isolate failing third-party updates by validating them one at a time.

## Supported project inputs

The configured projects folder is scanned recursively for:

- traditional solution files (`.sln`);
- XML solution files (`.slnx`); and
- standalone project files (`.csproj`) that are not already referenced by a discovered solution.

Only entries inside a Git working tree are selectable. A repository root must also remain within the configured projects folder. Discovery skips common generated or tool-owned directories such as `.git`, `bin`, `obj`, `node_modules`, `.vs`, `.idea`, `.vscode`, `.cache`, and `.nuget`. Symbolic links are not traversed.

## Supported package declarations

The updater safely edits literal semantic versions in these forms:

```xml
<PackageReference Include="Example.Package" Version="1.2.3" />
```

```xml
<PackageReference Include="Example.Package">
  <Version>1.2.3</Version>
</PackageReference>
```

```xml
<!-- Nearest Directory.Packages.props within the repository -->
<PackageVersion Include="Example.Package" Version="1.2.3" />
```

For central package management, the application walks upward from the project and uses the nearest `Directory.Packages.props` that is still inside the repository.

The following declarations are reported but not edited:

- conditional package declarations or declarations under a conditional parent;
- MSBuild properties such as `$(ExampleVersion)`;
- version ranges and wildcards;
- missing versions;
- multiple or ambiguous version elements;
- declarations that cannot be mapped to one editable direct or central version; and
- transitive dependencies that have no direct declaration.

## Update strategies

### Latest minor

Selects the latest stable minor release within the highest major version already present in the selected occurrences of that package. Occurrences on an older major may therefore move to that already-selected higher major. Validated incremental fallback is more conservative: it retains a separate minor target for each occurrence's working major.

### Latest major

Selects the latest stable version available to the package. The review highlights major-version changes.

### Select packages

Lets the user cycle each package through no update, latest minor, and latest major. A persistent forced-version rule cannot be overridden in this screen.

### Validated incremental

Optimizes for fault isolation:

1. Restore, build, and test the unmodified repository as a baseline.
2. Update `Microsoft.*`, `Azure.*`, and `System.*` packages as one first-party batch and validate it.
3. Update each third-party package to its preferred latest major version and validate it.
4. If a non-forced third-party major update fails, restore its original version and try the latest minor version on the same working major.
5. If the fallback also fails, restore the original version, record the package failure, and continue with the next third-party package.

A forced exact version has no automatic fallback. A failed first-party batch is rolled back and fails that repository's run rather than continuing package by package.

## Git and repository safety model

- Repositories are processed sequentially, making the active repository and failure scope clear.
- Existing tracked and untracked changes are stashed before branch or package operations.
- The stash reference is reported in the final summary but is not restored automatically.
- Base-branch synchronization uses `git fetch` followed by `git pull --ff-only`; no merge commit is created.
- A requested update branch must not already exist locally or in known remote-tracking refs. The live remote is also checked when base synchronization or commit-and-push makes remote access necessary.
- Before editing, reviewed XML values are checked again. A stale or ambiguous plan is rejected.
- When committing, only package declaration files changed by the plan are staged.
- A failed push leaves the local commit available.
- Process output is written to a redacted log. Credentials in authenticated URLs and common token query parameters are masked.

## Technology stack

| Area | Technology |
|---|---|
| Runtime | .NET 10 / C# |
| Terminal UI | SharpConsoleUI |
| Project and package XML | LINQ to XML |
| NuGet lookup | `dotnet package list` and NuGet.Protocol |
| Source control | Git command-line client |
| Tests | MSTest SDK with Microsoft.Testing.Platform |

## Scope boundaries

This is a local interactive application, not a web service. It exposes no HTTP API and does not host a background agent. It operates on repositories below a user-selected local folder and uses each project's effective NuGet configuration and credentials.
