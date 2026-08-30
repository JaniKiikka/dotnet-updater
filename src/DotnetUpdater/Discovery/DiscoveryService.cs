using System.Collections.Immutable;
using System.Xml.Linq;
using DotnetUpdater.Domain;

namespace DotnetUpdater.Discovery;

public sealed class DiscoveryService
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
        { ".git", "bin", "obj", "node_modules", ".vs", ".idea", ".vscode", ".cache", ".nuget" };

    public DiscoveryResult Scan(string projectsFolder)
    {
        var warnings = ImmutableArray.CreateBuilder<DiscoveryWarning>();
        var root = Canonicalize(projectsFolder);
        if (!Directory.Exists(root))
            return new([], [new(root, "Projects folder does not exist or is inaccessible.")]);

        var candidates = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    if (new FileInfo(file).LinkTarget is not null) continue;
                    var extension = Path.GetExtension(file);
                    if (extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
                        extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase) ||
                        extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
                        candidates.Add(Canonicalize(file));
                }

                foreach (var child in Directory.EnumerateDirectories(directory).OrderByDescending(x => x, PathComparer))
                {
                    var info = new DirectoryInfo(child);
                    if (ExcludedDirectories.Contains(info.Name) || info.LinkTarget is not null)
                        continue;
                    var canonical = Canonicalize(child);
                    if (IsWithin(root, canonical)) pending.Push(canonical);
                    else warnings.Add(new(child, "Skipped path outside the projects folder."));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add(new(directory, ex.Message));
            }
        }

        candidates.Sort(PathComparer);
        var projectCandidates = candidates.Where(x => x.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).ToHashSet(PathComparer);
        var referencedProjects = new HashSet<string>(PathComparer);
        var entries = new List<SelectionEntry>();

        foreach (var solution in candidates.Where(x => x.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)))
        {
            var repositoryRoot = FindGitRoot(Path.GetDirectoryName(solution)!, root);
            if (repositoryRoot is null)
            {
                warnings.Add(new(solution, "Solution is not inside a Git repository."));
                continue;
            }
            var members = ReadSolutionProjects(solution, warnings)
                .Select(path => Canonicalize(Path.Combine(Path.GetDirectoryName(solution)!, path)))
                .Distinct(PathComparer).OrderBy(x => x, PathComparer).ToImmutableArray();
            var valid = ImmutableArray.CreateBuilder<string>();
            foreach (var member in members)
            {
                if (!IsWithin(repositoryRoot, member) || !File.Exists(member))
                    warnings.Add(new(solution, $"Broken or out-of-repository project reference: {member}"));
                else
                {
                    valid.Add(member);
                    referencedProjects.Add(member);
                }
            }
            entries.Add(new(solution, repositoryRoot,
                solution.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ? EntryKind.SolutionXml : EntryKind.Solution,
                valid.ToImmutable()));
        }

        foreach (var project in projectCandidates.Except(referencedProjects, PathComparer).OrderBy(x => x, PathComparer))
        {
            var repositoryRoot = FindGitRoot(Path.GetDirectoryName(project)!, root);
            if (repositoryRoot is null) warnings.Add(new(project, "Project is not inside a Git repository."));
            else entries.Add(new(project, repositoryRoot, EntryKind.StandaloneProject, [project]));
        }

        var repositories = entries.GroupBy(x => x.RepositoryRoot, PathComparer)
            .OrderBy(x => x.Key, PathComparer)
            .Select(x => new RepositoryInfo(x.Key, x.OrderBy(e => e.Path, PathComparer).ToImmutableArray()))
            .ToImmutableArray();
        return new(repositories, warnings.OrderBy(x => x.Path, PathComparer).ToImmutableArray());
    }

    private static IEnumerable<string> ReadSolutionProjects(string solution, ImmutableArray<DiscoveryWarning>.Builder warnings)
    {
        try
        {
            if (solution.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
                return XDocument.Load(solution).Descendants().Where(x => x.Name.LocalName == "Project")
                    .Select(x => x.Attribute("Path")?.Value).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToArray();

            return File.ReadLines(solution).Select(TryReadSlnProject).Where(x => x is not null).Cast<string>().ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            warnings.Add(new(solution, $"Could not read solution metadata: {ex.Message}"));
            return [];
        }
    }

    private static string? TryReadSlnProject(string line)
    {
        if (!line.TrimStart().StartsWith("Project(", StringComparison.Ordinal)) return null;
        var equals = line.IndexOf('=');
        if (equals < 0) return null;
        var fields = line[(equals + 1)..].Split(',');
        if (fields.Length < 2) return null;
        var value = fields[1].Trim().Trim('"').Replace('\\', Path.DirectorySeparatorChar);
        return value.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ? value : null;
    }

    private static string? FindGitRoot(string start, string boundary)
    {
        var current = new DirectoryInfo(Canonicalize(start));
        while (current is not null && IsWithin(boundary, current.FullName))
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) || File.Exists(Path.Combine(current.FullName, ".git")))
                return Canonicalize(current.FullName);
            current = current.Parent;
        }
        return null;
    }

    public static string Canonicalize(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
    private static bool IsWithin(string root, string path) => path.Equals(root, PathComparison) || path.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
