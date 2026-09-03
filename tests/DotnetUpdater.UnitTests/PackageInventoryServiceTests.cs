namespace DotnetUpdater.UnitTests;

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
