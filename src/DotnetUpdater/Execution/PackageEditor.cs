using System.Collections.Immutable;
using System.Text;
using System.Xml.Linq;
using DotnetUpdater.Domain;

namespace DotnetUpdater.Execution;

public sealed record EditValidation(bool IsValid, string? Error);
public sealed record EditResult(bool Succeeded, ImmutableArray<string> ChangedPaths, string? Error);

public sealed class PackageEditor
{
    public EditValidation Validate(IEnumerable<DeclarationEdit> edits)
    {
        foreach (var edit in edits)
        {
            try
            {
                var document = XDocument.Load(edit.DeclarationPath, LoadOptions.PreserveWhitespace);
                var located = Locate(document, edit);
                if (located.Error is not null) return new(false, located.Error);
                if (!string.Equals(located.Value, edit.OldVersion, StringComparison.Ordinal))
                    return new(false, $"{edit.DeclarationPath}: reviewed value {edit.OldVersion} changed to {located.Value}.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            { return new(false, $"{edit.DeclarationPath}: {ex.Message}"); }
        }
        return new(true, null);
    }

    public EditResult Apply(IEnumerable<DeclarationEdit> source)
    {
        var edits = source.ToArray();
        var validation = Validate(edits);
        if (!validation.IsValid) return new(false, [], validation.Error);
        var temporaryFiles = new List<(string Temporary, string Destination)>();
        try
        {
            foreach (var fileGroup in edits.GroupBy(x => x.DeclarationPath, PathComparer))
            {
                var path = fileGroup.Key;
                var original = File.ReadAllText(path);
                var hasBom = File.ReadAllBytes(path).AsSpan().StartsWith(Encoding.UTF8.Preamble);
                var document = XDocument.Parse(original, LoadOptions.PreserveWhitespace);
                foreach (var edit in fileGroup)
                {
                    var located = Locate(document, edit);
                    if (located.Error is not null) return new(false, [], located.Error);
                    if (!string.Equals(located.Value, edit.OldVersion, StringComparison.Ordinal))
                        return new(false, [], $"{edit.DeclarationPath}: reviewed value {edit.OldVersion} changed to {located.Value}.");
                    if (located.Attribute is not null) located.Attribute.Value = edit.TargetVersion;
                    else if (located.Element is not null) located.Element.Value = edit.TargetVersion;
                    else return new(false, [], located.Error);
                }
                var changed = document.ToString(SaveOptions.DisableFormatting);
                if (original.EndsWith("\r\n", StringComparison.Ordinal) && !changed.EndsWith("\r\n", StringComparison.Ordinal)) changed += "\r\n";
                else if (original.EndsWith('\n') && !changed.EndsWith('\n')) changed += "\n";
                var temporary = path + $".{Guid.NewGuid():N}.tmp";
                File.WriteAllText(temporary, changed, new UTF8Encoding(hasBom));
                temporaryFiles.Add((temporary, path));
            }
            foreach (var item in temporaryFiles) File.Move(item.Temporary, item.Destination, true);
            return new(true, temporaryFiles.Select(x => x.Destination).ToImmutableArray(), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        { return new(false, [], ex.Message); }
        finally
        {
            foreach (var item in temporaryFiles) if (File.Exists(item.Temporary)) File.Delete(item.Temporary);
        }
    }

    private static LocatedVersion Locate(XDocument document, DeclarationEdit edit)
    {
        var localName = edit.Kind == DeclarationKind.CentralPackageVersion ? "PackageVersion" : "PackageReference";
        var elements = document.Descendants().Where(x => x.Name.LocalName == localName)
            .Where(x => string.Equals((x.Attribute("Include") ?? x.Attribute("Update"))?.Value?.Trim(), edit.PackageId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (elements.Length != 1) return new(null, null, null, $"{edit.DeclarationPath}: declaration for {edit.PackageId} is missing or ambiguous.");
        var element = elements[0];
        if (edit.Kind is DeclarationKind.PackageReferenceAttribute or DeclarationKind.CentralPackageVersion)
        {
            var attribute = element.Attribute("Version");
            if (attribute is not null) return new(attribute.Value.Trim(), attribute, null, null);
            if (edit.Kind == DeclarationKind.CentralPackageVersion)
            {
                var children = element.Elements().Where(x => x.Name.LocalName == "Version").ToArray();
                if (children.Length == 1) return new(children[0].Value.Trim(), null, children[0], null);
                if (children.Length > 1) return new(null, null, null, $"{edit.DeclarationPath}: version location for {edit.PackageId} is ambiguous.");
            }
        }
        else
        {
            var children = element.Elements().Where(x => x.Name.LocalName == "Version").ToArray();
            if (children.Length == 1) return new(children[0].Value.Trim(), null, children[0], null);
            if (children.Length > 1) return new(null, null, null, $"{edit.DeclarationPath}: version location for {edit.PackageId} is ambiguous.");
        }
        return new(null, null, null, $"{edit.DeclarationPath}: version location for {edit.PackageId} changed.");
    }

    private sealed record LocatedVersion(string? Value, XAttribute? Attribute, XElement? Element, string? Error);
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
