namespace DotnetUpdater.UnitTests;

using SharpConsoleUI;
using SharpConsoleUI.Configuration;
using SharpConsoleUI.Drivers;
using SharpConsoleUI.Flows;

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
    public void RepositoryProgressRetainsOrderedMixedStatesAndCountsFailures()
    {
        var progress = new RepositoryProgressViewModel([
            "/repos/zeta",
            "/repos/active",
            "/repos/passed",
            "/repos/failed",
            "/repos/skipped",
            "/repos/queued"
        ]);
        progress.Apply(new("/repos/zeta", RunStage.Cancelled, "Cancelled by user"));
        progress.Apply(new("/repos/passed", RunStage.Passed, "Passed"));
        progress.Apply(new("/repos/failed", RunStage.Failed, "Failed at Test"));
        progress.Apply(new("/repos/skipped", RunStage.Skipped, "Preflight blocked"));
        progress.Apply(new("/repos/active", RunStage.ApplyUpdates, "Could not update Old.Package", "Old.Package",
            PackageUpdateStatus.Failed));
        progress.Apply(new("/repos/active", RunStage.Build, "Example.Package 8.0.0: building App.slnx", "Example.Package"));

        var snapshot = progress.Snapshot();
        var text = PresentationText.Progress(snapshot);

        CollectionAssert.AreEqual(
            new[] { "zeta", "active", "passed", "failed", "skipped", "queued" },
            snapshot.Repositories.Select(x => Path.GetFileName(x.RepositoryRoot)).ToArray());
        Assert.AreEqual(1, snapshot.PassedCount);
        Assert.AreEqual(1, snapshot.FailedCount);
        Assert.AreEqual(1, snapshot.SkippedCount);
        Assert.AreEqual(1, snapshot.QueuedCount);
        Assert.AreEqual(1, snapshot.CancelledCount);
        Assert.AreEqual(1, snapshot.FailedPackageCount);
        Assert.AreEqual("Example.Package", snapshot.Active?.PackageId);
        StringAssert.Contains(text, "[PASSED] passed");
        StringAssert.Contains(text, "[FAILED] failed");
        StringAssert.Contains(text, "[SKIPPED] skipped");
        StringAssert.Contains(text, "[QUEUED] queued");
        StringAssert.Contains(text, "[CANCELLED] zeta");
        StringAssert.Contains(text, "Phase:[/] Building");
        StringAssert.Contains(text, "Package:[/] Example.Package");
        StringAssert.Contains(text, "Command/status:[/] Example.Package 8.0.0: building App.slnx");
        StringAssert.Contains(text, "Failed packages:[/] 1");
    }

    [TestMethod]
    public void RepositoryProgressDoesNotOverwriteTerminalRowsAndCancelsIncompleteRows()
    {
        var progress = new RepositoryProgressViewModel(["/repos/one", "/repos/two", "/repos/three"]);
        progress.Apply(new("/repos/one", RunStage.Failed, "Tests failed"));
        progress.Apply(new("/repos/one", RunStage.Build, "Late event must be ignored"));
        progress.Apply(new("/repos/two", RunStage.Build, "Building"));
        progress.CancelIncomplete();

        var snapshot = progress.Snapshot();

        Assert.AreEqual(RunStage.Failed, snapshot.Repositories[0].Stage);
        Assert.AreEqual("Tests failed", snapshot.Repositories[0].Message);
        Assert.AreEqual(RunStage.Cancelled, snapshot.Repositories[1].Stage);
        Assert.AreEqual(RunStage.Cancelled, snapshot.Repositories[2].Stage);
        Assert.AreEqual(2, snapshot.CancelledCount);
        Assert.IsNull(snapshot.Active);
    }

    [TestMethod]
    public void RepositoryProgressRendersRetainedRowsAtEightyByTwentyFour()
    {
        using var driver = new HeadlessConsoleDriver(80, 24);
        var windowSystem = new ConsoleWindowSystem(
            driver,
            new ConsoleWindowSystemOptions(InstallSynchronizationContext: false));
        var progress = new RepositoryProgressViewModel([
            "/repos/one", "/repos/two", "/repos/three", "/repos/four", "/repos/five"
        ]);
        var content = new RepositoryProgressContent(windowSystem, progress);
        var window = new Window(windowSystem)
        {
            Width = 80,
            Height = 24,
            Title = "Repository progress"
        };
        window.AddControl(content.BuildContent(new FlowChrome("Repository progress", widthHint: 80, heightHint: 24)));
        windowSystem.AddWindow(window);

        content.Report(new("/repos/one", RunStage.Passed, "Passed"));
        content.Report(new("/repos/two", RunStage.Failed, "Tests failed"));
        content.Report(new("/repos/three", RunStage.Skipped, "Preflight blocked"));
        content.Report(new("/repos/four", RunStage.Build, "Building App.slnx", "Example.Package"));

        var rendered = System.Text.RegularExpressions.Regex.Replace(
            string.Join('\n', window.RenderAndGetVisibleContent(null)),
            "\u001b\\[[0-9;]*m",
            string.Empty);

        StringAssert.Contains(rendered, "[PASSED] one");
        StringAssert.Contains(rendered, "[FAILED] two");
        StringAssert.Contains(rendered, "[SKIPPED] three");
        StringAssert.Contains(rendered, "[RUNNING] four");
        StringAssert.Contains(rendered, "[QUEUED] five");
        StringAssert.Contains(rendered, "Command/status: Building App.slnx");
    }
}
