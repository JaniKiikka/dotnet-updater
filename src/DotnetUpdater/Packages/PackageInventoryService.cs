using System.Collections.Immutable;
using System.Xml.Linq;
using DotnetUpdater.Domain;
using DotnetUpdater.IO;

namespace DotnetUpdater.Packages;

public sealed record InventoryResult(
    ImmutableArray<PackageOccurrence> Occurrences,
    ImmutableArray<string> Warnings);

public sealed class PackageInventoryService(IRealPathContainment? containment = null)
{
    private readonly IRealPathContainment _containment = containment ?? new RealPathContainment();

    public InventoryResult Read(IEnumerable<SelectionEntry> selected, IReadOnlySet<string> ignoredPackages)
    {
        var occurrences = ImmutableArray.CreateBuilder<PackageOccurrence>();
        var warnings = ImmutableArray.CreateBuilder<string>();
        var projects = selected.SelectMany(entry => entry.ProjectPaths.Select((project, index) => new
            {
                Project = project,
                ResolvedProject = index < entry.ResolvedProjectPaths.Length
                    ? entry.ResolvedProjectPaths[index]
                    : project,
                entry.RepositoryRoot,
                entry.ResolvedRepositoryRoot,
                entry.ProjectsRoot,
                entry.ResolvedProjectsRoot
            }))
            .GroupBy(x => x.Project, PathComparer).Select(x => x.First()).OrderBy(x => x.Project, PathComparer);
        foreach (var item in projects)
        {
            try
            {
                var currentProject = ResolveStable(item.Project, item.ResolvedProject, "Project", warnings);
                var currentRepository = ResolveStable(item.RepositoryRoot, item.ResolvedRepositoryRoot, "Repository", warnings);
                var currentProjectsRoot = ResolveStable(item.ProjectsRoot, item.ResolvedProjectsRoot, "Projects folder", warnings);
                if (currentProject is null || currentRepository is null || currentProjectsRoot is null) continue;
                if (!_containment.IsWithin(currentProjectsRoot, currentRepository) ||
                    !_containment.IsWithin(currentProjectsRoot, currentProject) ||
                    !_containment.IsWithin(currentRepository, currentProject))
                {
                    warnings.Add($"{item.Project}: resolved target escapes the projects folder or repository ({currentProject}).");
                    continue;
                }
                ReadProject(item.Project, currentProject, item.RepositoryRoot, currentRepository,
                    currentProjectsRoot, ignoredPackages, occurrences, warnings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            { warnings.Add($"{item.Project}: {ex.Message}"); }
        }
        return new(occurrences.ToImmutable(), warnings.ToImmutable());
    }

    private void ReadProject(
        string projectDisplayPath,
        string projectExecutionPath,
        string repositoryDisplayRoot,
        string repositoryExecutionRoot,
        string projectsExecutionRoot,
        IReadOnlySet<string> ignored,
        ImmutableArray<PackageOccurrence>.Builder output,
        ImmutableArray<string>.Builder warnings)
    {
        var document = XDocument.Load(projectExecutionPath, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
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
                output.Add(Unsupported(projectDisplayPath, projectExecutionPath, id, "Conditional declarations are not supported."));
                continue;
            }
            if (versionElements.Length > 1)
            {
                output.Add(Unsupported(projectDisplayPath, projectExecutionPath, id, "Multiple version elements are ambiguous."));
                continue;
            }
            if (versionAttribute is not null || versionElement is not null)
            {
                var value = (versionAttribute?.Value ?? versionElement?.Value ?? string.Empty).Trim();
                if (!IsLiteral(value)) output.Add(Unsupported(projectDisplayPath, projectExecutionPath, id, "Version is a property, range, wildcard, or otherwise non-literal."));
                else output.Add(new PackageOccurrence(id, value, projectDisplayPath,
                    new PackageDeclaration(projectDisplayPath, id, value,
                        versionAttribute is not null ? DeclarationKind.PackageReferenceAttribute : DeclarationKind.PackageReferenceElement,
                        $"PackageReference:{id}:{(versionAttribute is not null ? "attribute" : "element")}")
                    { ResolvedPath = projectExecutionPath })
                { ResolvedProjectPath = projectExecutionPath });
                continue;
            }

            var central = FindCentralDeclaration(projectDisplayPath, repositoryDisplayRoot,
                repositoryExecutionRoot, projectsExecutionRoot, id, warnings);
            output.Add(central is null
                ? Unsupported(projectDisplayPath, projectExecutionPath, id, "No safely editable direct or central version declaration was found.")
                : new PackageOccurrence(id, central.CurrentVersion, projectDisplayPath, central)
                    { ResolvedProjectPath = projectExecutionPath });
        }
    }

    private PackageDeclaration? FindCentralDeclaration(
        string project,
        string repositoryRoot,
        string resolvedRepositoryRoot,
        string resolvedProjectsRoot,
        string packageId,
        ImmutableArray<string>.Builder warnings)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(project)!);
        while (directory is not null && IsWithin(repositoryRoot, directory.FullName))
        {
            var props = Path.Combine(directory.FullName, "Directory.Packages.props");
            if (File.Exists(props))
            {
                try
                {
                    var resolvedProps = _containment.ResolveExisting(props);
                    if (!resolvedProps.Succeeded)
                    {
                        warnings.Add($"{props}: real path could not be resolved. {resolvedProps.Error}");
                        return null;
                    }
                    if (!_containment.IsWithin(resolvedProjectsRoot, resolvedProps.ResolvedPath!) ||
                        !_containment.IsWithin(resolvedRepositoryRoot, resolvedProps.ResolvedPath!))
                    {
                        warnings.Add($"{props}: resolved target escapes the projects folder or repository ({resolvedProps.ResolvedPath}).");
                        return null;
                    }
                    var document = XDocument.Load(resolvedProps.ResolvedPath!, LoadOptions.PreserveWhitespace);
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
                    return new PackageDeclaration(props, packageId, value, DeclarationKind.CentralPackageVersion,
                        $"PackageVersion:{packageId}") { ResolvedPath = resolvedProps.ResolvedPath! };
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
                { warnings.Add($"{props}: {ex.Message}"); return null; }
            }
            directory = directory.Parent;
        }
        return null;
    }

    private string? ResolveStable(
        string displayPath,
        string plannedResolvedPath,
        string kind,
        ImmutableArray<string>.Builder warnings)
    {
        var current = _containment.ResolveExisting(displayPath);
        if (!current.Succeeded)
        {
            warnings.Add($"{displayPath}: {kind.ToLowerInvariant()} real path could not be resolved. {current.Error}");
            return null;
        }
        if (!_containment.PathsEqual(current.ResolvedPath!, plannedResolvedPath))
        {
            warnings.Add($"{displayPath}: {kind.ToLowerInvariant()} target changed after discovery ({plannedResolvedPath} -> {current.ResolvedPath}).");
            return null;
        }
        return current.ResolvedPath;
    }

    private static PackageOccurrence Unsupported(string project, string resolvedProject, string id, string reason) =>
        new PackageOccurrence(id, string.Empty, project,
            new PackageDeclaration(project, id, string.Empty, DeclarationKind.PackageReferenceAttribute,
                $"unsupported:{id}") { ResolvedPath = resolvedProject }, reason)
        { ResolvedProjectPath = resolvedProject };

    private static bool IsLiteral(string value) => !string.IsNullOrWhiteSpace(value) &&
        !value.Contains("$(", StringComparison.Ordinal) && !value.Contains('*') &&
        !value.StartsWith('[') && !value.StartsWith('(') && SemanticVersion.TryParse(value, out _);

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static bool IsWithin(string root, string path) => PathComparer.Equals(Path.GetFullPath(root), Path.GetFullPath(path)) ||
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
