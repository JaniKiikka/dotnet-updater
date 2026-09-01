namespace DotnetUpdater.IO;

public sealed record RealPathResolution(string DisplayPath, string? ResolvedPath, string? Error)
{
    public bool Succeeded => ResolvedPath is not null;
}

public interface IRealPathContainment
{
    RealPathResolution ResolveExisting(string path);
    bool IsWithin(string resolvedRoot, string resolvedPath);
    bool PathsEqual(string left, string right);
}

public sealed class RealPathContainment : IRealPathContainment
{
    private const int MaximumLinkExpansions = 256;

    public RealPathResolution ResolveExisting(string path)
    {
        string displayPath;
        try
        {
            displayPath = Normalize(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new(path, null, $"Path is invalid: {ex.Message}");
        }

        var candidate = displayPath;
        var visited = new HashSet<string>(PathComparer) { candidate };
        try
        {
            for (var expansion = 0; expansion < MaximumLinkExpansions; expansion++)
            {
                var root = Path.GetPathRoot(candidate);
                if (string.IsNullOrEmpty(root))
                    return new(displayPath, null, "Path has no filesystem root.");

                var components = candidate[root.Length..]
                    .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                        StringSplitOptions.RemoveEmptyEntries);
                var current = Normalize(root);
                var expanded = false;

                for (var index = 0; index < components.Length; index++)
                {
                    current = Normalize(Path.Combine(current, components[index]));
                    var attributes = File.GetAttributes(current);
                    if ((attributes & FileAttributes.ReparsePoint) == 0) continue;

                    FileSystemInfo info = (attributes & FileAttributes.Directory) != 0
                        ? new DirectoryInfo(current)
                        : new FileInfo(current);
                    var target = info.ResolveLinkTarget(returnFinalTarget: false);
                    if (target is null)
                        return new(displayPath, null, $"Could not resolve linked path component: {current}");

                    var rewritten = Normalize(target.FullName);
                    for (var remaining = index + 1; remaining < components.Length; remaining++)
                        rewritten = Normalize(Path.Combine(rewritten, components[remaining]));

                    if (!visited.Add(rewritten))
                        return new(displayPath, null, $"A symbolic-link or reparse-point cycle was detected at {current}.");

                    candidate = rewritten;
                    expanded = true;
                    break;
                }

                if (!expanded)
                    return new(displayPath, candidate, null);
            }

            return new(displayPath, null,
                $"Path contains more than {MaximumLinkExpansions} symbolic-link or reparse-point expansions.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new(displayPath, null, $"Could not resolve real path: {ex.Message}");
        }
    }

    public bool IsWithin(string resolvedRoot, string resolvedPath)
    {
        var root = Normalize(resolvedRoot);
        var path = Normalize(resolvedPath);
        return PathsEqual(root, path) ||
            path.StartsWith(AppendSeparator(root), PathComparison);
    }

    public bool PathsEqual(string left, string right) =>
        PathComparer.Equals(Normalize(left), Normalize(right));

    private static string AppendSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    private static string Normalize(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return root is not null && PathComparer.Equals(fullPath, root)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
