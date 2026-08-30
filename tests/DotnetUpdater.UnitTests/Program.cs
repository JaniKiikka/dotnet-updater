using System.Collections.Immutable;
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

        await store.SaveAsync(new(temp.Path, ["Serilog", "serilog", "  NUnit  "], "", ""), default);
        var loaded = await store.LoadAsync(default);

        Assert.IsNull(loaded.Warning);
        CollectionAssert.AreEqual(new[] { "NUnit", "Serilog" }, loaded.Configuration.IgnoredPackages.ToArray());
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
}

[TestClass]
public sealed class PresentationViewModelTests
{
    [TestMethod]
    public void IgnoredPackageViewCombinesDiscoveredAndSavedPackagesCaseInsensitively()
    {
        var viewModel = new IgnoredPackagesViewModel(
            [" Serilog ", "NUnit", "serilog"],
            ["nunit", "Moq"]);

        CollectionAssert.AreEqual(new[] { "Moq", "NUnit", "Serilog" }, viewModel.Items.Select(x => x.PackageId).ToArray());
        Assert.IsFalse(viewModel.Items.Single(x => x.PackageId == "Serilog").IsIgnored);
        Assert.IsTrue(viewModel.Items.Single(x => x.PackageId == "NUnit").IsIgnored);
        Assert.IsFalse(viewModel.Items.Single(x => x.PackageId == "Moq").IsDiscovered);
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
