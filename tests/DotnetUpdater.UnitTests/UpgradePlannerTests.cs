namespace DotnetUpdater.UnitTests;

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
    public void AllNoUpdateDecisionsProduceNoRepositories()
    {
        var (entry, group) = SinglePackage("1.0.0", new(1, 1, 0), new(2, 0, 0));
        var decisions = new Dictionary<string, PackageDecision>
        {
            [group.PackageId] = new(group.PackageId, UpgradeChoice.NoUpdate, null)
        };

        var plan = new UpgradePlanner().Create(
            [entry], [group], decisions, new("origin", null, null, false), DateTimeOffset.UnixEpoch);

        Assert.IsEmpty(plan.Repositories);
    }

    [TestMethod]
    public void AlreadyCurrentAutomaticTargetProducesNoRepositories()
    {
        var (entry, group) = SinglePackage("2.0.0", new(2, 0, 0), new(2, 0, 0));
        var decision = UpgradePlanner.AutomaticDecision(group, UpgradeChoice.LatestMajor);

        var plan = new UpgradePlanner().Create(
            [entry], [group], new Dictionary<string, PackageDecision> { [group.PackageId] = decision },
            new("origin", null, null, false), DateTimeOffset.UnixEpoch);

        Assert.IsEmpty(plan.Repositories);
    }

    [TestMethod]
    public void UnavailableAutomaticTargetProducesNoRepositories()
    {
        var (entry, original) = SinglePackage("1.0.0", null, null);
        var group = original with { ResolutionError = "NuGet source unavailable" };
        var decision = UpgradePlanner.AutomaticDecision(group, UpgradeChoice.LatestMajor);

        var plan = new UpgradePlanner().Create(
            [entry], [group], new Dictionary<string, PackageDecision> { [group.PackageId] = decision },
            new("origin", null, null, false), DateTimeOffset.UnixEpoch);

        Assert.AreEqual(UpgradeChoice.NoUpdate, decision.Choice);
        Assert.IsEmpty(plan.Repositories);
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

    private static (SelectionEntry Entry, PackageGroup Group) SinglePackage(
        string current, SemanticVersion? latestMinor, SemanticVersion? latestMajor)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "App.csproj");
        var occurrence = Occurrence(project, "Example.Package", current);
        return (
            new SelectionEntry(project, root, EntryKind.StandaloneProject, [project]),
            new PackageGroup(occurrence.PackageId, [occurrence], latestMinor, latestMajor, null));
    }
}
