namespace DotnetUpdater.UnitTests;

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

