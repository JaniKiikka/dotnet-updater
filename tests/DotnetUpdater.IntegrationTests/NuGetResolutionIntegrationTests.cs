namespace DotnetUpdater.IntegrationTests;

[TestClass]
public sealed class NuGetResolutionIntegrationTests
{
    [TestMethod]
    public async Task DifferentLocalFeedsResolveTheHighestVersionAvailableToBothRepositories()
    {
        var temp = Path.Combine(Path.GetTempPath(), "dotnet-updater-nuget-integration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        try
        {
            var one = CreateRepository(temp, "one", ["1.0.0", "2.0.0", "3.0.0"]);
            var two = CreateRepository(temp, "two", ["1.0.0", "2.0.0"]);
            var logger = new FileRunLogger(Path.Combine(temp, "logs"));
            var runner = new CountingRunner(new ProcessRunner(logger));
            await MustRunAsync(runner, one.Root, "dotnet", "restore", one.Project);
            await MustRunAsync(runner, two.Root, "dotnet", "restore", two.Project);
            runner.Reset();
            var occurrences = new[]
            {
                Occurrence(one.Project),
                Occurrence(two.Project)
            };

            var resolved = await new NuGetVersionService(runner).ResolveAsync(
                occurrences.GroupBy(x => x.PackageId, StringComparer.OrdinalIgnoreCase).Single(),
                default);

            Assert.AreEqual(4, runner.RequestCount, "Each of the two contexts should run the two query modes once.");
            Assert.AreEqual(new SemanticVersion(2, 0, 0), resolved.LatestMajor);
            Assert.AreEqual(new SemanticVersion(1, 0, 0), resolved.LatestMinor);
            Assert.IsNull(resolved.ResolutionError);
        }
        finally
        {
            try { Directory.Delete(temp, true); }
            catch (IOException) { }
        }
    }

    private static (string Root, string Project) CreateRepository(
        string parent,
        string name,
        IEnumerable<string> versions)
    {
        var root = Directory.CreateDirectory(Path.Combine(parent, name)).FullName;
        var feed = Directory.CreateDirectory(Path.Combine(root, "feed")).FullName;
        foreach (var version in versions) CreatePackage(feed, version);
        var project = Path.Combine(root, "App.csproj");
        File.WriteAllText(project, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><PackageReference Include="Example.Package" Version="1.0.0" /></ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(root, "NuGet.Config"), $$"""
            <configuration>
              <packageSources>
                <clear />
                <add key="local" value="{{feed}}" />
              </packageSources>
            </configuration>
            """);
        return (root, project);
    }

    private static void CreatePackage(string feed, string version)
    {
        using var archive = ZipFile.Open(
            Path.Combine(feed, $"Example.Package.{version}.nupkg"),
            ZipArchiveMode.Create);
        var nuspec = archive.CreateEntry("Example.Package.nuspec");
        using (var writer = new StreamWriter(nuspec.Open()))
            writer.Write($$"""
                <?xml version="1.0"?>
                <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
                  <metadata>
                    <id>Example.Package</id>
                    <version>{{version}}</version>
                    <authors>Tests</authors>
                    <description>Integration test package</description>
                  </metadata>
                </package>
                """);
        archive.CreateEntry("lib/net10.0/_._");
    }

    private static PackageOccurrence Occurrence(string project) =>
        new("Example.Package", "1.0.0", project,
            new(project, "Example.Package", "1.0.0", DeclarationKind.PackageReferenceAttribute,
                "PackageReference:Example.Package:attribute"));

    private static async Task<ProcessResult> MustRunAsync(
        IProcessRunner runner,
        string workingDirectory,
        string file,
        params string[] arguments)
    {
        var result = await runner.RunAsync(new(file, arguments, workingDirectory), default);
        Assert.IsTrue(result.Succeeded, $"{file} {string.Join(' ', arguments)} failed: {result.StandardError}");
        return result;
    }

    private sealed class CountingRunner(IProcessRunner inner) : IProcessRunner
    {
        private int requestCount;
        public int RequestCount => requestCount;
        public void Reset() => requestCount = 0;

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requestCount);
            return inner.RunAsync(request, cancellationToken);
        }
    }
}
