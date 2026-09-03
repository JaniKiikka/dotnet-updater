namespace DotnetUpdater.UnitTests;

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
