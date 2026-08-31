using System.Collections.Immutable;
using System.IO.Compression;
using DotnetUpdater.Configuration;
using DotnetUpdater.Discovery;
using DotnetUpdater.Domain;
using DotnetUpdater.Execution;
using DotnetUpdater.Packages;
using DotnetUpdater.Planning;
using DotnetUpdater.Presentation;

namespace DotnetUpdater.UnitTests;

[TestClass]
public sealed class NuGetVersionServiceTests
{
    [TestMethod]
    public void DefaultConcurrencyTracksLogicalProcessorsWithinSafeBounds()
    {
        Assert.AreEqual(
            Math.Clamp(Environment.ProcessorCount, 2, 8),
            NuGetVersionService.DefaultMaxConcurrency);
    }

    [TestMethod]
    public async Task ResolveAllRunsWithBoundedConcurrencyAndPreservesInputOrder()
    {
        using var temp = new TempDirectory();
        var groups = Enumerable.Range(0, 8).Select(index =>
        {
            var packageId = $"Package.{index}";
            var project = Path.Combine(temp.Path, $"{packageId}.csproj");
            var occurrence = new PackageOccurrence(
                packageId,
                "1.0.0",
                project,
                new(project, packageId, "1.0.0", DeclarationKind.PackageReferenceAttribute, $"PackageReference:{packageId}:attribute"));
            return new[] { occurrence }.GroupBy(x => x.PackageId, StringComparer.OrdinalIgnoreCase).Single();
        }).ToArray();
        var runner = new ConcurrencyTrackingRunner(TimeSpan.FromMilliseconds(25));
        var progress = new RecordingProgress();

        var resolved = await new NuGetVersionService(runner).ResolveAllAsync(groups, progress, default, maxConcurrency: 3);

        Assert.AreEqual(3, runner.MaximumConcurrency);
        CollectionAssert.AreEqual(groups.Select(x => x.Key).ToArray(), resolved.Select(x => x.PackageId).ToArray());
        Assert.IsTrue(resolved.All(x => x.LatestMinor == new SemanticVersion(1, 9, 0)));
        Assert.IsTrue(resolved.All(x => x.LatestMajor == new SemanticVersion(2, 0, 0)));
        Assert.HasCount(groups.Length, progress.Messages);
        StringAssert.StartsWith(progress.Messages[^1], $"Resolved {groups.Length} of {groups.Length}:");
    }

    [TestMethod]
    public async Task ResolveKeepsLatestMinorTargetForEveryWorkingMajor()
    {
        using var temp = new TempDirectory();
        var projectOne = Path.Combine(temp.Path, "One.csproj");
        var projectTwo = Path.Combine(temp.Path, "Two.csproj");
        var occurrences = new[]
        {
            Occurrence(projectOne, "1.2.0"),
            Occurrence(projectTwo, "2.3.0")
        };
        var group = occurrences.GroupBy(x => x.PackageId, StringComparer.OrdinalIgnoreCase).Single();
        var runner = new RecordingRunner(request =>
        {
            var isMinor = request.Arguments.Contains("--highest-minor");
            var version = !isMinor ? "3.0.0"
                : request.Arguments.Contains(projectOne) ? "1.9.0" : "2.8.0";
            return new(0, $$"""{"id":"Example.Package","latestVersion":"{{version}}"}""", string.Empty);
        });

        var resolved = await new NuGetVersionService(runner).ResolveAsync(group, default);

        Assert.AreEqual(new SemanticVersion(1, 9, 0), resolved.LatestMinorByMajor[1]);
        Assert.AreEqual(new SemanticVersion(2, 8, 0), resolved.LatestMinorByMajor[2]);
        Assert.AreEqual(new SemanticVersion(2, 8, 0), resolved.LatestMinor);
        Assert.AreEqual(new SemanticVersion(3, 0, 0), resolved.LatestMajor);
    }

    [TestMethod]
    public async Task ExactVersionLookupReturnsEveryStableAndPrereleaseVersion()
    {
        var source = new RecordingAllVersionsSource(
            new(["2.0.0", "2.0.0-rc.1", "1.0.0"], null));
        var service = new NuGetVersionService(new RecordingRunner(_ => new(0, "", "")), source);

        var result = await service.GetAllVersionsAsync("/repo/App.csproj", "Example.Package", default);

        CollectionAssert.AreEqual(new[] { "2.0.0", "2.0.0-rc.1", "1.0.0" }, result.Versions.ToArray());
        Assert.AreEqual(("/repo/App.csproj", "Example.Package"), source.Request);
    }

    [TestMethod]
    public async Task ConfiguredSourceReadsAllVersionsFromEffectiveLocalNuGetConfig()
    {
        using var temp = new TempDirectory();
        var feed = Directory.CreateDirectory(Path.Combine(temp.Path, "feed")).FullName;
        var project = Path.Combine(temp.Path, "App.csproj");
        File.WriteAllText(project, "<Project />");
        File.WriteAllText(Path.Combine(temp.Path, "NuGet.Config"), $$"""
            <configuration>
              <packageSources>
                <clear />
                <add key="local-test" value="{{feed}}" />
              </packageSources>
            </configuration>
            """);
        CreatePackage(feed, "Example.Package", "1.0.0");
        CreatePackage(feed, "Example.Package", "2.0.0-rc.1");
        CreatePackage(feed, "Example.Package", "2.0.0");

        var result = await new ConfiguredNuGetVersionSource().GetAllAsync(
            project,
            "Example.Package",
            default);

        Assert.IsNull(result.Error);
        CollectionAssert.AreEqual(
            new[] { "2.0.0", "2.0.0-rc.1", "1.0.0" },
            result.Versions.ToArray());
    }

    private static void CreatePackage(string feed, string packageId, string version)
    {
        using var archive = ZipFile.Open(
            Path.Combine(feed, $"{packageId}.{version}.nupkg"),
            ZipArchiveMode.Create);
        var entry = archive.CreateEntry($"{packageId}.nuspec");
        using var writer = new StreamWriter(entry.Open());
        writer.Write($$"""
            <?xml version="1.0"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{{packageId}}</id>
                <version>{{version}}</version>
                <authors>Tests</authors>
                <description>Test package</description>
              </metadata>
            </package>
            """);
    }

    private static PackageOccurrence Occurrence(string project, string version) =>
        new("Example.Package", version, project,
            new(project, "Example.Package", version, DeclarationKind.PackageReferenceAttribute,
                "PackageReference:Example.Package:attribute"));
}

[TestClass]
public sealed class DomainTests
{
    [TestMethod]
    public void GitWorkflowRecognizesDirectCurrentBranchUpdates()
    {
        Assert.IsTrue(new GitWorkflowOptions("origin", null, null, false).UpdatesCurrentBranch);
        Assert.IsFalse(new GitWorkflowOptions("origin", null, "release-update", false).UpdatesCurrentBranch);
    }

    [TestMethod]
    public void SemanticVersionsOrderStableVersionsAfterPrereleases()
    {
        Assert.IsTrue(SemanticVersion.TryParse("7.2.1", out var stable));
        Assert.IsTrue(SemanticVersion.TryParse("7.2.1-beta.1", out var preview));
        Assert.IsGreaterThan(0, stable.CompareTo(preview));
        Assert.IsFalse(SemanticVersion.TryParse("[7.0,8.0)", out _));
    }

    [TestMethod]
    public void RepositoryStateMachineRejectsTransitionsAfterFailure()
    {
        var machine = new RepositoryStateMachine();
        machine.MoveTo(RunStage.Preflight);
        machine.MoveTo(RunStage.Stash);
        machine.MoveTo(RunStage.Failed);

        Assert.ThrowsExactly<InvalidOperationException>(() => machine.MoveTo(RunStage.Push));
    }
}

[TestClass]
public sealed class ConfigurationStoreTests
{
    [TestMethod]
    public async Task SaveAndLoadNormalizeConfiguration()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        var store = new JsonConfigurationStore(new FixedPath(path));

        await store.SaveAsync(new(
            temp.Path,
            ["Serilog", "serilog", "  NUnit  "],
            [new("  Example.Package ", " 2.1.0-beta.1 "), new("example.package", "2.1.0-beta.2")],
            "",
            ""), default);
        var loaded = await store.LoadAsync(default);

        Assert.IsNull(loaded.Warning);
        CollectionAssert.AreEqual(new[] { "NUnit", "Serilog" }, loaded.Configuration.IgnoredPackages.ToArray());
        Assert.AreEqual("Example.Package", loaded.Configuration.ForcedPackageVersions.Single().PackageId);
        Assert.AreEqual("2.1.0-beta.2", loaded.Configuration.ForcedPackageVersions.Single().Version);
        Assert.AreEqual("development", loaded.Configuration.DevelopmentBranch);
        Assert.AreEqual("origin", loaded.Configuration.RemoteName);
        Assert.IsFalse(Directory.EnumerateFiles(temp.Path, "*.tmp").Any());
    }

    [TestMethod]
    public async Task LoadPreservesMalformedConfiguration()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        await File.WriteAllTextAsync(path, "{not-json");
        var store = new JsonConfigurationStore(new FixedPath(path));

        var loaded = await store.LoadAsync(default);

        Assert.IsNotNull(loaded.Warning);
        Assert.AreEqual("{not-json", await File.ReadAllTextAsync(path));
    }

    [TestMethod]
    public async Task LoadAcceptsSettingsWrittenBeforeForcedVersionsExisted()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        await File.WriteAllTextAsync(path, $$"""
            {
              "projectsFolder": "{{temp.Path}}",
              "ignoredPackages": ["Serilog"],
              "developmentBranch": "development",
              "remoteName": "origin"
            }
            """);
        var store = new JsonConfigurationStore(new FixedPath(path));

        var loaded = await store.LoadAsync(default);

        Assert.IsNull(loaded.Warning);
        CollectionAssert.AreEqual(new[] { "Serilog" }, loaded.Configuration.IgnoredPackages.ToArray());
        Assert.IsEmpty(loaded.Configuration.ForcedPackageVersions);
    }
}

[TestClass]
public sealed class DiscoveryServiceTests
{
    [TestMethod]
    public void ScanReadsSolutionMembershipAndFindsStandaloneProjects()
    {
        using var temp = new TempDirectory();
        var repo = Directory.CreateDirectory(Path.Combine(temp.Path, "repo")).FullName;
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        Directory.CreateDirectory(Path.Combine(repo, "src"));
        Directory.CreateDirectory(Path.Combine(repo, "other"));
        File.WriteAllText(Path.Combine(repo, "src", "App.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(repo, "other", "Loose.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(repo, "App.slnx"), "<Solution><Project Path=\"src/App.csproj\" /></Solution>");
        Directory.CreateDirectory(Path.Combine(repo, "obj"));
        File.WriteAllText(Path.Combine(repo, "obj", "Ignored.csproj"), "<Project />");

        var result = new DiscoveryService().Scan(temp.Path);

        Assert.HasCount(2, result.Entries);
        Assert.IsTrue(result.Entries.Any(x => x.Kind == EntryKind.SolutionXml && x.ProjectPaths.Length == 1));
        Assert.IsTrue(result.Entries.Any(x => x.Kind == EntryKind.StandaloneProject && x.Path.EndsWith("Loose.csproj", StringComparison.Ordinal)));
    }
}

[TestClass]
public sealed class PackageInventoryServiceTests
{
    [TestMethod]
    public void ReadSupportsDirectAndCentralVersionsAndReportsUnsupportedVersions()
    {
        using var temp = new TempDirectory();
        var project = Path.Combine(temp.Path, "App.csproj");
        File.WriteAllText(Path.Combine(temp.Path, "Directory.Packages.props"), """
            <Project><ItemGroup><PackageVersion Include="Central.One" Version="3.2.1" /></ItemGroup></Project>
            """);
        File.WriteAllText(project, """
            <Project><ItemGroup>
              <PackageReference Include="Direct.One" Version="1.2.3" />
              <PackageReference Include="Direct.Two"><Version>2.0.0</Version></PackageReference>
              <PackageReference Include="Central.One" />
              <PackageReference Include="Unsupported" Version="$(UnsupportedVersion)" />
              <PackageReference Include="Ignored" Version="1.0.0" />
            </ItemGroup></Project>
            """);
        var entry = new SelectionEntry(project, temp.Path, EntryKind.StandaloneProject, [project]);

        var result = new PackageInventoryService().Read(
            [entry],
            new HashSet<string>(["ignored"], StringComparer.OrdinalIgnoreCase));

        Assert.HasCount(4, result.Occurrences);
        Assert.AreEqual(
            DeclarationKind.CentralPackageVersion,
            result.Occurrences.Single(x => x.PackageId == "Central.One").Declaration.Kind);
        Assert.IsNotNull(result.Occurrences.Single(x => x.PackageId == "Unsupported").UnsupportedReason);
    }
}

[TestClass]
public sealed class UpgradePlannerTests
{
    [TestMethod]
    public void CreateDeduplicatesSharedCentralDeclarations()
    {
        using var temp = new TempDirectory();
        var declaration = new PackageDeclaration(
            Path.Combine(temp.Path, "Directory.Packages.props"),
            "Example.Package",
            "6.4.0",
            DeclarationKind.CentralPackageVersion,
            "PackageVersion:Example.Package");
        var project1 = Path.Combine(temp.Path, "One.csproj");
        var project2 = Path.Combine(temp.Path, "Two.csproj");
        var occurrences = ImmutableArray.Create(
            new PackageOccurrence("Example.Package", "6.4.0", project1, declaration),
            new PackageOccurrence("example.package", "6.4.0", project2, declaration));
        var group = new PackageGroup("Example.Package", occurrences, new(6, 9, 0), new(7, 1, 0), null);
        var selected = new[]
        {
            new SelectionEntry(Path.Combine(temp.Path, "All.slnx"), temp.Path, EntryKind.SolutionXml, [project1, project2])
        };
        var decisions = new Dictionary<string, PackageDecision>(StringComparer.OrdinalIgnoreCase)
        {
            ["example.package"] = new("example.package", UpgradeChoice.LatestMajor, "7.1.0")
        };

        var plan = new UpgradePlanner().Create(
            selected,
            [group],
            decisions,
            new("origin", " development ", " version-update ", false),
            DateTimeOffset.UnixEpoch);

        Assert.HasCount(1, plan.Repositories.Single().Edits);
        Assert.AreEqual("7.1.0", plan.Repositories.Single().Edits.Single().TargetVersion);
        Assert.AreEqual("development", plan.Git.BaseBranch);
        Assert.AreEqual("version-update", plan.Git.TargetBranch);
    }

    [TestMethod]
    public void ForcedExactVersionCanIntentionallyDowngradeAndIsMarkedInPlan()
    {
        using var temp = new TempDirectory();
        var project = Path.Combine(temp.Path, "App.csproj");
        var declaration = new PackageDeclaration(
            project,
            "Example.Package",
            "3.0.0",
            DeclarationKind.PackageReferenceAttribute,
            "PackageReference:Example.Package:attribute");
        var group = new PackageGroup(
            "Example.Package",
            [new("Example.Package", "3.0.0", project, declaration)],
            null,
            null,
            null);
        var entry = new SelectionEntry(project, temp.Path, EntryKind.StandaloneProject, [project]);
        var decisions = new Dictionary<string, PackageDecision>(StringComparer.OrdinalIgnoreCase)
        {
            ["Example.Package"] = new("Example.Package", UpgradeChoice.ExactVersion, "2.0.0-rc.1")
        };

        var edit = new UpgradePlanner().Create(
            [entry],
            [group],
            decisions,
            new("origin", null, null, false),
            DateTimeOffset.UnixEpoch).Repositories.Single().Edits.Single();

        Assert.AreEqual("2.0.0-rc.1", edit.TargetVersion);
        Assert.IsTrue(edit.IsForced);
    }

    [TestMethod]
    public void ValidatedIncrementalPlanSeparatesMicrosoftPackagesAndKeepsMinorFallbacks()
    {
        using var temp = new TempDirectory();
        var project = Path.Combine(temp.Path, "App.csproj");
        var microsoft = Occurrence(project, "Microsoft.Extensions.Example", "1.0.0");
        var thirdParty = Occurrence(project, "Serilog", "3.0.0");
        var groups = new[]
        {
            new PackageGroup(microsoft.PackageId, [microsoft], new(1, 8, 0), new(2, 0, 0), null),
            new PackageGroup(thirdParty.PackageId, [thirdParty], new(3, 4, 0), new(4, 0, 0), null)
        };
        var entry = new SelectionEntry(project, temp.Path, EntryKind.StandaloneProject, [project]);

        var plan = new UpgradePlanner().CreateValidatedIncremental(
            [entry], groups, new Dictionary<string, string>(),
            new("origin", null, null, false), DateTimeOffset.UnixEpoch);

        Assert.AreEqual(UpgradeStrategy.ValidatedIncremental, plan.Strategy);
        var updates = plan.Repositories.Single().ValidatedUpdates;
        Assert.IsTrue(updates.Single(x => x.PackageId == microsoft.PackageId).IsFirstParty);
        var serilog = updates.Single(x => x.PackageId == thirdParty.PackageId);
        Assert.IsFalse(serilog.IsFirstParty);
        Assert.AreEqual("4.0.0", serilog.PreferredEdits.Single().TargetVersion);
        Assert.AreEqual("3.4.0", serilog.FallbackEdits.Single().TargetVersion);
    }

    [TestMethod]
    public void ValidatedIncrementalFallbackStaysOnEachRepositoriesWorkingMajor()
    {
        using var temp = new TempDirectory();
        var repoOne = Directory.CreateDirectory(Path.Combine(temp.Path, "one")).FullName;
        var repoTwo = Directory.CreateDirectory(Path.Combine(temp.Path, "two")).FullName;
        var projectOne = Path.Combine(repoOne, "One.csproj");
        var projectTwo = Path.Combine(repoTwo, "Two.csproj");
        var group = new PackageGroup(
            "Example.Package",
            [Occurrence(projectOne, "Example.Package", "1.2.0"), Occurrence(projectTwo, "Example.Package", "2.3.0")],
            new(2, 8, 0),
            new(3, 0, 0),
            null)
        {
            LatestMinorByMajor = new Dictionary<int, SemanticVersion>
            {
                [1] = new(1, 9, 0),
                [2] = new(2, 8, 0)
            }.ToImmutableDictionary()
        };
        var entries = new[]
        {
            new SelectionEntry(projectOne, repoOne, EntryKind.StandaloneProject, [projectOne]),
            new SelectionEntry(projectTwo, repoTwo, EntryKind.StandaloneProject, [projectTwo])
        };

        var plan = new UpgradePlanner().CreateValidatedIncremental(
            entries, [group], new Dictionary<string, string>(),
            new("origin", null, null, false), DateTimeOffset.UnixEpoch);

        Assert.AreEqual("1.9.0", plan.Repositories.Single(x => x.RepositoryRoot == repoOne)
            .ValidatedUpdates.Single().FallbackEdits.Single().TargetVersion);
        Assert.AreEqual("2.8.0", plan.Repositories.Single(x => x.RepositoryRoot == repoTwo)
            .ValidatedUpdates.Single().FallbackEdits.Single().TargetVersion);
    }

    private static PackageOccurrence Occurrence(string project, string packageId, string version) =>
        new(packageId, version, project,
            new(project, packageId, version, DeclarationKind.PackageReferenceAttribute,
                $"PackageReference:{packageId}:attribute"));
}

[TestClass]
public sealed class PackageEditorTests
{
    [TestMethod]
    public void ApplyUpdatesCurrentPlansAndValidateRejectsStalePlans()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "App.csproj");
        File.WriteAllText(path, "<Project>\n  <ItemGroup><PackageReference Include=\"Thing\" Version=\"1.0.0\" /></ItemGroup>\n</Project>\n");
        var edit = new DeclarationEdit(
            temp.Path,
            path,
            "Thing",
            "1.0.0",
            "1.2.0",
            DeclarationKind.PackageReferenceAttribute,
            "PackageReference:Thing:attribute");
        var editor = new PackageEditor();

        var result = editor.Apply([edit]);

        Assert.IsTrue(result.Succeeded);
        StringAssert.Contains(File.ReadAllText(path), "Version=\"1.2.0\"");
        Assert.IsFalse(editor.Validate([edit]).IsValid);
    }
}

[TestClass]
public sealed class PreflightServiceTests
{
    [TestMethod]
    public async Task CurrentBranchWorkflowDoesNotRequireRemoteOrBranchPreparation()
    {
        using var temp = new TempDirectory();
        var declarationPath = Path.Combine(temp.Path, "Directory.Packages.props");
        var target = Path.Combine(temp.Path, "Repo.slnx");
        File.WriteAllText(declarationPath, "<Project><ItemGroup><PackageVersion Include=\"Thing\" Version=\"1.0.0\" /></ItemGroup></Project>");
        File.WriteAllText(target, "<Solution />");
        var repository = new RepositoryPlan(temp.Path, [target],
            [new(temp.Path, declarationPath, "Thing", "1.0.0", "2.0.0", DeclarationKind.CentralPackageVersion, "PackageVersion:Thing")]);
        var plan = new UpgradePlan(new("origin", null, null, false), [repository], DateTimeOffset.UnixEpoch);
        var fake = new RecordingRunner(request => request.Arguments.SequenceEqual(["rev-parse", "--is-inside-work-tree"])
            ? new(0, "true\n", string.Empty)
            : request.Arguments.SequenceEqual(["branch", "--show-current"])
                ? new(0, "work-in-progress\n", string.Empty)
                : new(0, string.Empty, string.Empty));

        var result = (await new PreflightService(fake, new PackageEditor()).InspectAsync(plan, default)).Single();

        Assert.IsTrue(result.IsReady);
        Assert.IsFalse(fake.Requests.Any(request => request.Arguments.Contains("ls-remote")));
        Assert.IsFalse(fake.Requests.Any(request => request.Arguments.Contains("show-ref")));
        Assert.IsFalse(fake.Requests.Any(request => request.Arguments.Contains("check-ref-format")));
    }
}

[TestClass]
public sealed class RunCoordinatorTests
{
    [TestMethod]
    public async Task ExecutePreservesCommandOrdering()
    {
        using var temp = new TempDirectory();
        var declarationPath = Path.Combine(temp.Path, "Directory.Packages.props");
        var target = Path.Combine(temp.Path, "Repo.slnx");
        File.WriteAllText(declarationPath, "<Project><ItemGroup><PackageVersion Include=\"Thing\" Version=\"1.0.0\" /></ItemGroup></Project>");
        File.WriteAllText(target, "<Solution />");
        var edit = new DeclarationEdit(
            temp.Path,
            declarationPath,
            "Thing",
            "1.0.0",
            "2.0.0",
            DeclarationKind.CentralPackageVersion,
            "PackageVersion:Thing");
        var repository = new RepositoryPlan(temp.Path, [target], [edit]);
        var plan = new UpgradePlan(
            new("origin", "development", "versions/TEST-1", true),
            [repository],
            DateTimeOffset.UnixEpoch);
        var fake = new RecordingRunner(request =>
        {
            var command = $"{request.FileName} {string.Join(' ', request.Arguments)}";
            if (command == "git switch --create versions/TEST-1")
                StringAssert.Contains(File.ReadAllText(declarationPath), "1.0.0");
            if (command == "git diff --cached --quiet")
                return new(1, string.Empty, string.Empty);
            if (command == "git rev-parse HEAD")
                return new(0, new string('a', 40) + "\n", string.Empty);
            return new(0, string.Empty, string.Empty);
        });
        var logger = new FileRunLogger(Path.Combine(temp.Path, "logs"));
        var coordinator = new RunCoordinator(fake, new GitService(fake), new PackageEditor(), logger);
        var ready = new Dictionary<string, RepositoryPreflight>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
        {
            [temp.Path] = new(temp.Path, true, [])
        };

        var results = await coordinator.ExecuteAsync(plan, ready, null, default);

        Assert.AreEqual(RunStage.Passed, results.Single().Status);
        CollectionAssert.AreEqual(new[]
        {
            "git status --porcelain=v1 --untracked-files=all",
            "git switch development",
            "git fetch origin",
            "git pull --ff-only origin development",
            "git switch --create versions/TEST-1",
            $"dotnet restore {target}",
            $"dotnet build {target} --no-restore",
            $"dotnet test {target} --no-build --no-restore",
            "git add -- Directory.Packages.props",
            "git diff --cached --quiet",
            "git commit --message versions/TEST-1 .NET nuget package update",
            "git rev-parse HEAD",
            "git push --set-upstream origin versions/TEST-1"
        }, fake.Requests.Select(request => $"{request.FileName} {string.Join(' ', request.Arguments)}").ToArray());
    }

    [TestMethod]
    public async Task ExecuteCanUpdateCurrentBranchWithoutSwitchCommitOrPush()
    {
        using var temp = new TempDirectory();
        var declarationPath = Path.Combine(temp.Path, "Directory.Packages.props");
        var target = Path.Combine(temp.Path, "Repo.slnx");
        File.WriteAllText(declarationPath, "<Project><ItemGroup><PackageVersion Include=\"Thing\" Version=\"1.0.0\" /></ItemGroup></Project>");
        File.WriteAllText(target, "<Solution />");
        var repository = new RepositoryPlan(temp.Path, [target],
            [new(temp.Path, declarationPath, "Thing", "1.0.0", "2.0.0", DeclarationKind.CentralPackageVersion, "PackageVersion:Thing")]);
        var plan = new UpgradePlan(new("origin", null, null, false), [repository], DateTimeOffset.UnixEpoch);
        var fake = new RecordingRunner(request => request.Arguments.SequenceEqual(["branch", "--show-current"])
            ? new(0, "work-in-progress\n", string.Empty)
            : new(0, string.Empty, string.Empty));
        var logger = new FileRunLogger(Path.Combine(temp.Path, "logs"));
        var coordinator = new RunCoordinator(fake, new GitService(fake), new PackageEditor(), logger);
        var ready = new Dictionary<string, RepositoryPreflight>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
        {
            [temp.Path] = new(temp.Path, true, [])
        };

        var result = (await coordinator.ExecuteAsync(plan, ready, null, default)).Single();

        Assert.AreEqual(RunStage.Passed, result.Status);
        Assert.AreEqual("work-in-progress", result.BranchName);
        Assert.IsNull(result.CommitId);
        Assert.IsNull(result.RemoteBranch);
        CollectionAssert.AreEqual(new[]
        {
            "git branch --show-current",
            "git status --porcelain=v1 --untracked-files=all",
            $"dotnet restore {target}",
            $"dotnet build {target} --no-restore",
            $"dotnet test {target} --no-build --no-restore"
        }, fake.Requests.Select(request => $"{request.FileName} {string.Join(' ', request.Arguments)}").ToArray());
    }

    [TestMethod]
    public async Task ValidatedIncrementalBuildsBaselineThenUsesMinorFallback()
    {
        using var temp = new TempDirectory();
        var project = Path.Combine(temp.Path, "App.csproj");
        File.WriteAllText(project, """
            <Project><ItemGroup>
              <PackageReference Include="Microsoft.Extensions.Example" Version="1.0.0" />
              <PackageReference Include="Third.Party" Version="1.0.0" />
            </ItemGroup></Project>
            """);
        var microsoftEdit = Edit(temp.Path, project, "Microsoft.Extensions.Example", "1.0.0", "2.0.0");
        var thirdMajor = Edit(temp.Path, project, "Third.Party", "1.0.0", "2.0.0");
        var thirdMinor = Edit(temp.Path, project, "Third.Party", "1.0.0", "1.5.0");
        var repository = new RepositoryPlan(temp.Path, [project], [microsoftEdit, thirdMajor])
        {
            ValidatedUpdates =
            [
                new("Microsoft.Extensions.Example", true, false, [microsoftEdit], []),
                new("Third.Party", false, false, [thirdMajor], [thirdMinor])
            ]
        };
        var plan = new UpgradePlan(new("origin", null, null, false), [repository], DateTimeOffset.UnixEpoch)
        {
            Strategy = UpgradeStrategy.ValidatedIncremental
        };
        var fake = new RecordingRunner(request =>
        {
            if (request.Arguments.SequenceEqual(["branch", "--show-current"]))
                return new(0, "work-in-progress\n", string.Empty);
            if (request.FileName == "dotnet" && request.Arguments.FirstOrDefault() == "build" &&
                File.ReadAllText(project).Contains("Third.Party\" Version=\"2.0.0", StringComparison.Ordinal))
                return new(1, string.Empty, "major build failed");
            return new(0, string.Empty, string.Empty);
        });
        var logger = new FileRunLogger(Path.Combine(temp.Path, "logs"));
        var coordinator = new RunCoordinator(fake, new GitService(fake), new PackageEditor(), logger);
        var ready = Ready(temp.Path);

        var result = (await coordinator.ExecuteAsync(plan, ready, null, default)).Single();

        Assert.AreEqual(RunStage.Passed, result.Status);
        Assert.AreEqual(PackageUpdateStatus.UpdatedWithFallback,
            result.PackageResults.Single(x => x.PackageId == "Third.Party").Status);
        var final = File.ReadAllText(project);
        StringAssert.Contains(final, "Microsoft.Extensions.Example\" Version=\"2.0.0");
        StringAssert.Contains(final, "Third.Party\" Version=\"1.5.0");
        Assert.HasCount(4, fake.Requests.Where(x => x.FileName == "dotnet" && x.Arguments.First() == "restore"));
        Assert.HasCount(3, fake.Requests.Where(x => x.FileName == "dotnet" && x.Arguments.First() == "test"));
    }

    [TestMethod]
    public async Task ValidatedIncrementalRestoresRejectedPackageAndContinues()
    {
        using var temp = new TempDirectory();
        var project = Path.Combine(temp.Path, "App.csproj");
        File.WriteAllText(project, """
            <Project><ItemGroup>
              <PackageReference Include="Broken.Package" Version="1.0.0" />
              <PackageReference Include="Working.Package" Version="1.0.0" />
            </ItemGroup></Project>
            """);
        var brokenMajor = Edit(temp.Path, project, "Broken.Package", "1.0.0", "2.0.0");
        var brokenMinor = Edit(temp.Path, project, "Broken.Package", "1.0.0", "1.5.0");
        var working = Edit(temp.Path, project, "Working.Package", "1.0.0", "2.0.0");
        var repository = new RepositoryPlan(temp.Path, [project], [brokenMajor, working])
        {
            ValidatedUpdates =
            [
                new("Broken.Package", false, false, [brokenMajor], [brokenMinor]),
                new("Working.Package", false, false, [working], [])
            ]
        };
        var plan = new UpgradePlan(new("origin", null, null, false), [repository], DateTimeOffset.UnixEpoch)
        {
            Strategy = UpgradeStrategy.ValidatedIncremental
        };
        var fake = new RecordingRunner(request =>
        {
            if (request.Arguments.SequenceEqual(["branch", "--show-current"]))
                return new(0, "work-in-progress\n", string.Empty);
            if (request.FileName == "dotnet" && request.Arguments.FirstOrDefault() == "test" &&
                !File.ReadAllText(project).Contains("Broken.Package\" Version=\"1.0.0", StringComparison.Ordinal))
                return new(1, string.Empty, "tests failed");
            return new(0, string.Empty, string.Empty);
        });
        var coordinator = new RunCoordinator(fake, new GitService(fake), new PackageEditor(),
            new FileRunLogger(Path.Combine(temp.Path, "logs")));

        var result = (await coordinator.ExecuteAsync(plan, Ready(temp.Path), null, default)).Single();

        Assert.AreEqual(RunStage.Passed, result.Status);
        Assert.AreEqual(PackageUpdateStatus.Failed,
            result.PackageResults.Single(x => x.PackageId == "Broken.Package").Status);
        var final = File.ReadAllText(project);
        StringAssert.Contains(final, "Broken.Package\" Version=\"1.0.0");
        StringAssert.Contains(final, "Working.Package\" Version=\"2.0.0");
        Assert.IsFalse(result.ChangedPackages.Any(x => x.PackageId == "Broken.Package"));
    }

    private static DeclarationEdit Edit(string root, string project, string packageId, string oldVersion, string targetVersion) =>
        new(root, project, packageId, oldVersion, targetVersion, DeclarationKind.PackageReferenceAttribute,
            $"PackageReference:{packageId}:attribute");

    private static Dictionary<string, RepositoryPreflight> Ready(string root) =>
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
        {
            [root] = new(root, true, [])
        };
}

[TestClass]
public sealed class PresentationViewModelTests
{
    [TestMethod]
    public void PackageRulesClearlyCombineDiscoveredIgnoredAndForcedPackages()
    {
        var declaration = new PackageDeclaration("/repo/App.csproj", "Serilog", "4.0.0",
            DeclarationKind.PackageReferenceAttribute, "package");
        var occurrences = new[]
        {
            new PackageOccurrence("Serilog", "4.0.0", "/repo/App.csproj", declaration),
            new PackageOccurrence("NUnit", "4.1.0", "/repo/App.csproj", declaration)
        };
        var viewModel = new PackageRulesViewModel(
            occurrences,
            ["nunit"],
            [new("Moq", "5.0.0-preview.1")]);

        CollectionAssert.AreEqual(new[] { "Moq", "NUnit", "Serilog" }, viewModel.Items.Select(x => x.PackageId).ToArray());
        Assert.AreEqual(PackageRuleState.Normal, viewModel.Items.Single(x => x.PackageId == "Serilog").State);
        Assert.AreEqual(PackageRuleState.Ignored, viewModel.Items.Single(x => x.PackageId == "NUnit").State);
        Assert.AreEqual(PackageRuleState.Forced, viewModel.Items.Single(x => x.PackageId == "Moq").State);
        Assert.IsFalse(viewModel.Items.Single(x => x.PackageId == "Moq").IsDiscovered);
        StringAssert.Contains(viewModel.Items.Single(x => x.PackageId == "NUnit").DisplayText, "[IGNORED]");
        StringAssert.Contains(viewModel.Items.Single(x => x.PackageId == "Moq").DisplayText, "[FORCED");
    }

    [TestMethod]
    public void PackageVersionSearchIncludesAndFiltersPrereleases()
    {
        var result = PackageVersionSearch.Filter(
            ["3.0.0", "3.0.0-rc.2", "2.5.0-beta.1", "2.4.0"],
            "beta");

        CollectionAssert.AreEqual(new[] { "2.5.0-beta.1" }, result.ToArray());
    }

    [TestMethod]
    public void ManualPackageDecisionCyclesUpdateChoicesAndResolvesTargets()
    {
        var occurrence = new PackageOccurrence(
            "Example.Package",
            "6.4.0",
            "/repo/App.csproj",
            new("/repo/App.csproj", "Example.Package", "6.4.0", DeclarationKind.PackageReferenceAttribute, "package"));
        var group = new PackageGroup("Example.Package", [occurrence], new(6, 9, 0), new(7, 1, 0), null);
        var viewModel = new PackageDecisionViewModel(group);

        Assert.AreEqual(UpgradeChoice.NoUpdate, viewModel.Choice);
        viewModel.Cycle();
        Assert.AreEqual(UpgradeChoice.LatestMinor, viewModel.Choice);
        Assert.AreEqual("6.9.0", viewModel.ToDecision().TargetVersion);
        viewModel.Cycle();
        Assert.AreEqual(UpgradeChoice.LatestMajor, viewModel.Choice);
        Assert.AreEqual("7.1.0", viewModel.ToDecision().TargetVersion);
        viewModel.Cycle();
        Assert.AreEqual(UpgradeChoice.NoUpdate, viewModel.Choice);
    }

    [TestMethod]
    public void RepositoryProgressRetainsOneTextLabeledRowPerRepository()
    {
        var progress = new RepositoryProgressViewModel(["/repos/one", "/repos/two"]);
        progress.Apply(new("/repos/one", RunStage.Build, "Building One.slnx"));
        progress.Apply(new("/repos/two", RunStage.Failed, "Failed at Test"));

        var text = PresentationText.Progress(progress.Snapshot());

        StringAssert.Contains(text, "one");
        StringAssert.Contains(text, "Building");
        StringAssert.Contains(text, "two");
        StringAssert.Contains(text, "Failed");
    }
}

internal sealed class FixedPath(string path) : IConfigurationPathProvider
{
    public string GetPath() => path;
}

internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        "dotnet-updater-tests",
        Guid.NewGuid().ToString("N"));

    public TempDirectory() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, true);
        }
        catch (IOException)
        {
        }
    }
}

internal sealed class RecordingRunner(Func<ProcessRequest, ProcessResult> response) : IProcessRunner
{
    public List<ProcessRequest> Requests { get; } = [];

    public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(response(request));
    }
}

internal sealed class ConcurrencyTrackingRunner(TimeSpan delay) : IProcessRunner
{
    private readonly object _gate = new();
    private int _active;

    public int MaximumConcurrency { get; private set; }

    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _active++;
            MaximumConcurrency = Math.Max(MaximumConcurrency, _active);
        }

        try
        {
            await Task.Delay(delay, cancellationToken);
            var project = request.Arguments.SkipWhile(x => x != "--project").Skip(1).First();
            var packageId = Path.GetFileNameWithoutExtension(project);
            var latest = request.Arguments.Contains("--highest-minor") ? "1.9.0" : "2.0.0";
            var json = $$"""{"projects":[{"frameworks":[{"topLevelPackages":[{"id":"{{packageId}}","latestVersion":"{{latest}}"}]}]}]}""";
            return new(0, json, string.Empty);
        }
        finally
        {
            lock (_gate) _active--;
        }
    }
}

internal sealed class RecordingProgress : IProgress<string>
{
    private readonly object _gate = new();
    private readonly List<string> _messages = [];

    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_gate) return _messages.ToArray();
        }
    }

    public void Report(string value)
    {
        lock (_gate) _messages.Add(value);
    }
}

internal sealed class RecordingAllVersionsSource(PackageVersionLookup response) : IAllPackageVersionsSource
{
    public (string ProjectPath, string PackageId)? Request { get; private set; }

    public Task<PackageVersionLookup> GetAllAsync(
        string projectPath,
        string packageId,
        CancellationToken cancellationToken)
    {
        Request = (projectPath, packageId);
        return Task.FromResult(response);
    }
}
