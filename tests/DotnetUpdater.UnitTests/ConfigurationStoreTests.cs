namespace DotnetUpdater.UnitTests;

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
              "projectsFolder": {{JsonSerializer.Serialize(temp.Path)}},
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
