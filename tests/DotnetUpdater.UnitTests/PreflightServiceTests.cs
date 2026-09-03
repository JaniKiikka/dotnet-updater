namespace DotnetUpdater.UnitTests;

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
