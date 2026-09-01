using System.Collections.Immutable;
using System.Text;
using System.Xml.Linq;
using DotnetUpdater.Domain;
using DotnetUpdater.IO;

namespace DotnetUpdater.Execution;

public sealed record EditValidation(bool IsValid, string? Error);
public sealed record EditResult(bool Succeeded, ImmutableArray<string> ChangedPaths, string? Error);

public sealed class PackageEditor(IRealPathContainment? containment = null)
{
    private readonly IRealPathContainment _containment = containment ?? new RealPathContainment();

    public EditValidation Validate(IEnumerable<DeclarationEdit> edits)
    {
        foreach (var edit in edits)
        {
            try
            {
                if (!TryResolveForMutation(edit, out var executionPath, out var containmentError))
                    return new(false, containmentError);
                var document = XDocument.Load(executionPath, LoadOptions.PreserveWhitespace);
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
        var temporaryFiles = new List<PendingMove>();
        try
        {
            foreach (var fileGroup in edits.GroupBy(x => x.ResolvedDeclarationPath, PathComparer))
            {
                var firstEdit = fileGroup.First();
                if (!TryResolveForMutation(firstEdit, out var path, out var containmentError))
                    return new(false, [], containmentError);
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
                if (!TryResolveForMutation(firstEdit, out path, out containmentError))
                    return new(false, [], containmentError);
                var temporary = path + $".{Guid.NewGuid():N}.tmp";
                File.WriteAllText(temporary, changed, new UTF8Encoding(hasBom));
                temporaryFiles.Add(new(temporary, path, firstEdit));
            }
            foreach (var item in temporaryFiles)
            {
                if (!TryResolveForMutation(item.Edit, out var destination, out var containmentError))
                    return new(false, [], containmentError);
                File.Move(item.Temporary, destination, true);
            }
            return new(true, temporaryFiles.Select(x => x.Destination).ToImmutableArray(), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        { return new(false, [], ex.Message); }
        finally
        {
            foreach (var item in temporaryFiles) if (File.Exists(item.Temporary)) File.Delete(item.Temporary);
        }
    }

    private bool TryResolveForMutation(
        DeclarationEdit edit,
        out string executionPath,
        out string error)
    {
        executionPath = string.Empty;
        error = string.Empty;
        if (!TryResolveStable(edit.ProjectsRoot, edit.ResolvedProjectsRoot, "projects folder", out var projectsRoot, out error) ||
            !TryResolveStable(edit.RepositoryRoot, edit.ResolvedRepositoryRoot, "repository", out var repositoryRoot, out error) ||
            !TryResolveStable(edit.DeclarationPath, edit.ResolvedDeclarationPath, "declaration", out executionPath, out error))
            return false;

        if (!_containment.IsWithin(projectsRoot, executionPath) ||
            !_containment.IsWithin(repositoryRoot, executionPath) ||
            !_containment.IsWithin(projectsRoot, repositoryRoot))
        {
            error = $"{edit.DeclarationPath}: resolved target escapes the reviewed projects folder or repository ({executionPath}).";
            return false;
        }
        return true;
    }

    private bool TryResolveStable(
        string displayPath,
        string plannedResolvedPath,
        string kind,
        out string resolvedPath,
        out string error)
    {
        resolvedPath = string.Empty;
        var current = _containment.ResolveExisting(displayPath);
        if (!current.Succeeded)
        {
            error = $"{displayPath}: {kind} real path could not be resolved. {current.Error}";
            return false;
        }
        if (!_containment.PathsEqual(current.ResolvedPath!, plannedResolvedPath))
        {
            error = $"{displayPath}: {kind} target changed after planning ({plannedResolvedPath} -> {current.ResolvedPath}).";
            return false;
        }
        resolvedPath = current.ResolvedPath!;
        error = string.Empty;
        return true;
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
    private sealed record PendingMove(string Temporary, string Destination, DeclarationEdit Edit);
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
