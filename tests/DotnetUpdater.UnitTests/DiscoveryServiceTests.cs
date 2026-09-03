namespace DotnetUpdater.UnitTests;

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
