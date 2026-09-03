namespace DotnetUpdater.UnitTests;

[TestClass]
public sealed class PackageEditorTests
{
    [TestMethod]
    public void ApplyUpdatesCurrentPlansAndValidateRejectsStalePlans()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "App.csproj");
        File.WriteAllText(path, "<Project>\n  <ItemGroup><PackageReference Include=\"Thing\" Version=\"1.0.0\" /></ItemGroup>\n</Project>\n");
        var edit = new DeclarationEdit(
            temp.Path,
            path,
            "Thing",
            "1.0.0",
            "1.2.0",
            DeclarationKind.PackageReferenceAttribute,
            "PackageReference:Thing:attribute");
        var editor = new PackageEditor();

        var result = editor.Apply([edit]);

        Assert.IsTrue(result.Succeeded);
        StringAssert.Contains(File.ReadAllText(path), "Version=\"1.2.0\"");
        Assert.IsFalse(editor.Validate([edit]).IsValid);
    }
}
