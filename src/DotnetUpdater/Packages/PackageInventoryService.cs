using System.Collections.Immutable;
using System.Xml.Linq;
using DotnetUpdater.Domain;

namespace DotnetUpdater.Packages;

public sealed record InventoryResult(
    ImmutableArray<PackageOccurrence> Occurrences,
    ImmutableArray<string> Warnings);

public sealed class PackageInventoryService
{
    public InventoryResult Read(IEnumerable<SelectionEntry> selected, IReadOnlySet<string> ignoredPackages)
    {
        var occurrences = ImmutableArray.CreateBuilder<PackageOccurrence>();
        var warnings = ImmutableArray.CreateBuilder<string>();
        var projects = selected.SelectMany(entry => entry.ProjectPaths.Select(project => (Project: project, entry.RepositoryRoot)))
            .GroupBy(x => x.Project, PathComparer).Select(x => x.First()).OrderBy(x => x.Project, PathComparer);
        foreach (var item in projects)
        {
            try { ReadProject(item.Project, item.RepositoryRoot, ignoredPackages, occurrences, warnings); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            { warnings.Add($"{item.Project}: {ex.Message}"); }
        }
        return new(occurrences.ToImmutable(), warnings.ToImmutable());
    }

    private static void ReadProject(
        string project,
        string repositoryRoot,
        IReadOnlySet<string> ignored,
        ImmutableArray<PackageOccurrence>.Builder output,
        ImmutableArray<string>.Builder warnings)
    {
        var document = XDocument.Load(project, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        var references = document.Descendants().Where(x => x.Name.LocalName == "PackageReference").ToArray();
        foreach (var reference in references)
        {
            var id = (reference.Attribute("Include") ?? reference.Attribute("Update"))?.Value?.Trim();
            if (string.IsNullOrEmpty(id) || ignored.Contains(id)) continue;
            var condition = reference.Attribute("Condition") ?? reference.Ancestors().Select(x => x.Attribute("Condition")).FirstOrDefault(x => x is not null);
            var versionAttribute = reference.Attribute("Version");
            var versionElements = reference.Elements().Where(x => x.Name.LocalName == "Version").ToArray();
            var versionElement = versionElements.SingleOrDefault();
            if (condition is not null)
            {
                output.Add(Unsupported(project, id, "Conditional declarations are not supported."));
                continue;
            }
            if (versionElements.Length > 1)
            {
                output.Add(Unsupported(project, id, "Multiple version elements are ambiguous."));
                continue;
            }
            if (versionAttribute is not null || versionElement is not null)
            {
                var value = (versionAttribute?.Value ?? versionElement?.Value ?? string.Empty).Trim();
                if (!IsLiteral(value)) output.Add(Unsupported(project, id, "Version is a property, range, wildcard, or otherwise non-literal."));
                else output.Add(new(id, value, project,
                    new(project, id, value, versionAttribute is not null ? DeclarationKind.PackageReferenceAttribute : DeclarationKind.PackageReferenceElement,
                        $"PackageReference:{id}:{(versionAttribute is not null ? "attribute" : "element")}")));
                continue;
            }

            var central = FindCentralDeclaration(project, repositoryRoot, id, warnings);
            output.Add(central is null
                ? Unsupported(project, id, "No safely editable direct or central version declaration was found.")
                : new PackageOccurrence(id, central.CurrentVersion, project, central));
        }
    }

    private static PackageDeclaration? FindCentralDeclaration(string project, string repositoryRoot, string packageId, ImmutableArray<string>.Builder warnings)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(project)!);
        while (directory is not null && IsWithin(repositoryRoot, directory.FullName))
        {
            var props = Path.Combine(directory.FullName, "Directory.Packages.props");
            if (File.Exists(props))
            {
                try
                {
                    var document = XDocument.Load(props, LoadOptions.PreserveWhitespace);
                    var matches = document.Descendants().Where(x => x.Name.LocalName == "PackageVersion")
                        .Where(x => string.Equals((x.Attribute("Include") ?? x.Attribute("Update"))?.Value?.Trim(), packageId, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    if (matches.Length != 1) return null;
                    var match = matches[0];
                    if (match.Attribute("Condition") is not null || match.Ancestors().Any(x => x.Attribute("Condition") is not null)) return null;
                    var attribute = match.Attribute("Version");
                    var versionElements = match.Elements().Where(x => x.Name.LocalName == "Version").ToArray();
                    if (versionElements.Length > 1) return null;
                    var element = versionElements.SingleOrDefault();
                    var value = (attribute?.Value ?? element?.Value ?? string.Empty).Trim();
                    if (!IsLiteral(value)) return null;
                    return new(props, packageId, value, DeclarationKind.CentralPackageVersion, $"PackageVersion:{packageId}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
                { warnings.Add($"{props}: {ex.Message}"); return null; }
            }
            directory = directory.Parent;
        }
        return null;
    }

    private static PackageOccurrence Unsupported(string project, string id, string reason) =>
        new(id, string.Empty, project, new(project, id, string.Empty, DeclarationKind.PackageReferenceAttribute, $"unsupported:{id}"), reason);

    private static bool IsLiteral(string value) => !string.IsNullOrWhiteSpace(value) &&
        !value.Contains("$(", StringComparison.Ordinal) && !value.Contains('*') &&
        !value.StartsWith('[') && !value.StartsWith('(') && SemanticVersion.TryParse(value, out _);

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static bool IsWithin(string root, string path) => PathComparer.Equals(Path.GetFullPath(root), Path.GetFullPath(path)) ||
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
