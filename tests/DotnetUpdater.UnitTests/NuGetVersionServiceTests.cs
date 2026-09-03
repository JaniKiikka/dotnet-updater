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

        var source = new ContextualAllVersionsSource((_, _) =>
            new(["3.0.0", "2.8.0", "1.9.0"], null));

        var resolved = await new NuGetVersionService(runner, source).ResolveAsync(group, default);

        Assert.AreEqual(new SemanticVersion(1, 9, 0), resolved.LatestMinorByMajor[1]);
        Assert.AreEqual(new SemanticVersion(2, 8, 0), resolved.LatestMinorByMajor[2]);
        Assert.AreEqual(new SemanticVersion(2, 8, 0), resolved.LatestMinor);
        Assert.AreEqual(new SemanticVersion(3, 0, 0), resolved.LatestMajor);
    }

    [TestMethod]
    public async Task HundredPackagesInOneProjectRunExactlyOneProcessPerQueryMode()
    {
        using var temp = new TempDirectory();
        var project = Path.Combine(temp.Path, "App.csproj");
        var groups = Enumerable.Range(0, 100).Select(index =>
        {
            var packageId = $"Package.{index}";
            return new[] { Occurrence(project, packageId, "1.0.0") }
                .GroupBy(x => x.PackageId, StringComparer.OrdinalIgnoreCase).Single();
        }).ToArray();
        var runner = new RecordingRunner(request =>
        {
            var latest = request.Arguments.Contains("--highest-minor") ? "1.9.0" : "2.0.0";
            var packages = string.Join(',', groups.Select(group =>
                $$"""{"id":"{{group.Key}}","latestVersion":"{{latest}}"}"""));
            return new(0, $$"""{"topLevelPackages":[{{packages}}]}""", string.Empty);
        });

        var resolved = await new NuGetVersionService(runner).ResolveAllAsync(groups, null, default);

        Assert.HasCount(2, runner.Requests);
        Assert.HasCount(1, runner.Requests.Where(x => x.Arguments.Contains("--highest-minor")));
        Assert.HasCount(1, runner.Requests.Where(x => !x.Arguments.Contains("--highest-minor")));
        Assert.IsTrue(resolved.All(x => x.LatestMinor == new SemanticVersion(1, 9, 0)));
        Assert.IsTrue(resolved.All(x => x.LatestMajor == new SemanticVersion(2, 0, 0)));
    }

    [TestMethod]
    public async Task CrossContextTargetsAreCommonAndIndependentOfOccurrenceOrder()
    {
        using var temp = new TempDirectory();
        var projectOne = Path.Combine(temp.Path, "one", "App.csproj");
        var projectTwo = Path.Combine(temp.Path, "two", "App.csproj");
        var occurrences = new[]
        {
            Occurrence(projectOne, "Example.Package", "1.0.0"),
            Occurrence(projectTwo, "Example.Package", "1.0.0")
        };
        var runner = new RecordingRunner(request =>
        {
            var projectArgument = request.Arguments.Select((value, index) => (value, index))
                .Single(x => x.value == "--project").index;
            var project = request.Arguments[projectArgument + 1];
            var latest = request.Arguments.Contains("--highest-minor")
                ? project == projectOne ? "1.9.0" : "1.8.0"
                : project == projectOne ? "3.0.0" : "2.0.0";
            return new(0, $$"""{"id":"Example.Package","latestVersion":"{{latest}}"}""", string.Empty);
        });
        var source = new ContextualAllVersionsSource((project, _) => project == projectOne
            ? new(["3.0.0", "2.0.0", "1.9.0", "1.8.0", "1.0.0"], null)
            : new(["2.0.0", "1.8.0", "1.0.0"], null));

        var forward = await new NuGetVersionService(runner, source).ResolveAsync(
            occurrences.GroupBy(x => x.PackageId, StringComparer.OrdinalIgnoreCase).Single(), default);
        var reverse = await new NuGetVersionService(runner, source).ResolveAsync(
            occurrences.Reverse().GroupBy(x => x.PackageId, StringComparer.OrdinalIgnoreCase).Single(), default);

        Assert.AreEqual(new SemanticVersion(1, 8, 0), forward.LatestMinor);
        Assert.AreEqual(new SemanticVersion(2, 0, 0), forward.LatestMajor);
        Assert.AreEqual(forward.LatestMinor, reverse.LatestMinor);
        Assert.AreEqual(forward.LatestMajor, reverse.LatestMajor);
        Assert.IsNull(forward.ResolutionError);
    }

    [TestMethod]
    public async Task NoCommonVersionProducesAUsefulWarningAndNoTarget()
    {
        using var temp = new TempDirectory();
        var projectOne = Path.Combine(temp.Path, "one", "App.csproj");
        var projectTwo = Path.Combine(temp.Path, "two", "App.csproj");
        var occurrences = new[]
        {
            Occurrence(projectOne, "Example.Package", "1.0.0"),
            Occurrence(projectTwo, "Example.Package", "1.0.0")
        };
        var runner = new RecordingRunner(_ =>
            new(0, "{\"id\":\"Example.Package\",\"latestVersion\":\"2.0.0\"}", string.Empty));
        var source = new ContextualAllVersionsSource((project, _) => project == projectOne
            ? new(["1.0.0", "2.0.0"], null)
            : new(["1.1.0", "2.1.0"], null));

        var resolved = await new NuGetVersionService(runner, source).ResolveAsync(
            occurrences.GroupBy(x => x.PackageId, StringComparer.OrdinalIgnoreCase).Single(), default);

        Assert.IsNull(resolved.LatestMinor);
        Assert.IsNull(resolved.LatestMajor);
        StringAssert.Contains(resolved.ResolutionError, "No common stable version");
    }

    [TestMethod]
    public async Task UnavailableContextProducesAUsefulWarningAndNoTarget()
    {
        using var temp = new TempDirectory();
        var projectOne = Path.Combine(temp.Path, "one", "App.csproj");
        var projectTwo = Path.Combine(temp.Path, "two", "App.csproj");
        var occurrences = new[]
        {
            Occurrence(projectOne, "Example.Package", "1.0.0"),
            Occurrence(projectTwo, "Example.Package", "1.0.0")
        };
        var runner = new RecordingRunner(_ =>
            new(0, "{\"id\":\"Example.Package\",\"latestVersion\":\"2.0.0\"}", string.Empty));
        var source = new ContextualAllVersionsSource((project, _) => project == projectOne
            ? new(["1.0.0", "2.0.0"], null)
            : new([], "All configured NuGet sources failed: offline feed"));

        var resolved = await new NuGetVersionService(runner, source).ResolveAsync(
            occurrences.GroupBy(x => x.PackageId, StringComparer.OrdinalIgnoreCase).Single(), default);

        Assert.IsNull(resolved.LatestMinor);
        Assert.IsNull(resolved.LatestMajor);
        StringAssert.Contains(resolved.ResolutionError, projectTwo);
        StringAssert.Contains(resolved.ResolutionError, "offline feed");
    }

    [TestMethod]
    public async Task CancelledSnapshotIsEvictedAndCanBeRetried()
    {
        using var temp = new TempDirectory();
        var project = Path.Combine(temp.Path, "App.csproj");
        var group = new[] { Occurrence(project, "Example.Package", "1.0.0") }
            .GroupBy(x => x.PackageId, StringComparer.OrdinalIgnoreCase).Single();
        var runner = new CancelOnceRunner();
        var service = new NuGetVersionService(runner);

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => service.ResolveAsync(group, default));
        var resolved = await service.ResolveAsync(group, default);

        Assert.AreEqual(3, runner.RequestCount);
        Assert.AreEqual(new SemanticVersion(1, 9, 0), resolved.LatestMinor);
        Assert.AreEqual(new SemanticVersion(2, 0, 0), resolved.LatestMajor);
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

    private static PackageOccurrence Occurrence(string project, string packageId, string version) =>
        new(packageId, version, project,
            new(project, packageId, version, DeclarationKind.PackageReferenceAttribute,
                $"PackageReference:{packageId}:attribute"));
}

