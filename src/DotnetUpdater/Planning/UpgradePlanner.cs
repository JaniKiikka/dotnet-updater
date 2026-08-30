using System.Collections.Immutable;
using DotnetUpdater.Domain;

namespace DotnetUpdater.Planning;

public sealed class UpgradePlanner
{
    public UpgradePlan Create(
        IEnumerable<SelectionEntry> selectedEntries,
        IEnumerable<PackageGroup> groups,
        IReadOnlyDictionary<string, PackageDecision> decisions,
        GitWorkflowOptions git,
        DateTimeOffset createdAt)
    {
        git = git with
        {
            RemoteName = string.IsNullOrWhiteSpace(git.RemoteName) ? "origin" : git.RemoteName.Trim(),
            BaseBranch = NormalizeBranch(git.BaseBranch),
            TargetBranch = NormalizeBranch(git.TargetBranch)
        };
        var selected = selectedEntries.ToArray();
        var edits = new List<DeclarationEdit>();
        foreach (var group in groups.OrderBy(x => x.PackageId, StringComparer.OrdinalIgnoreCase))
        {
            if (!decisions.TryGetValue(group.PackageId, out var decision) ||
                decision.Choice == UpgradeChoice.NoUpdate ||
                !SemanticVersion.TryParse(decision.TargetVersion, out var target)) continue;

            foreach (var occurrence in group.Occurrences.Where(x => x.UnsupportedReason is null))
            {
                if (!SemanticVersion.TryParse(occurrence.CurrentVersion, out var current) || target.CompareTo(current) <= 0) continue;
                var repository = selected.FirstOrDefault(x => x.ProjectPaths.Contains(occurrence.ProjectPath, PathComparer))?.RepositoryRoot;
                if (repository is null) continue;
                edits.Add(new(repository, occurrence.Declaration.Path, occurrence.PackageId, occurrence.CurrentVersion,
                    decision.TargetVersion!, occurrence.Declaration.Kind, occurrence.Declaration.Locator));
            }
        }

        var uniqueEdits = edits.GroupBy(x => (Path: NormalizePath(x.DeclarationPath), x.Locator), EditKeyComparer.Instance)
            .Select(group =>
            {
                var targets = group.Select(x => x.TargetVersion).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (targets.Length != 1) throw new InvalidOperationException($"Shared declaration {group.Key.Path} has conflicting targets.");
                return group.First();
            }).ToArray();

        var repositories = selected.GroupBy(x => x.RepositoryRoot, PathComparer)
            .OrderBy(x => x.Key, PathComparer)
            .Select(group => new RepositoryPlan(
                group.Key,
                group.Select(x => x.Path).Distinct(PathComparer).OrderBy(x => x, PathComparer).ToImmutableArray(),
                uniqueEdits.Where(x => PathComparer.Equals(x.RepositoryRoot, group.Key))
                    .OrderBy(x => x.DeclarationPath, PathComparer).ThenBy(x => x.PackageId, StringComparer.OrdinalIgnoreCase).ToImmutableArray()))
            .Where(x => x.Edits.Length > 0)
            .ToImmutableArray();

        return new(git, repositories, createdAt);
    }

    public static PackageDecision AutomaticDecision(PackageGroup group, UpgradeChoice choice)
    {
        var target = choice switch
        {
            UpgradeChoice.LatestMinor => group.LatestMinor?.ToString(),
            UpgradeChoice.LatestMajor => group.LatestMajor?.ToString(),
            _ => null
        };
        return new(group.PackageId, target is null ? UpgradeChoice.NoUpdate : choice, target);
    }

    private static string NormalizePath(string path) => Path.GetFullPath(path);
    private static string? NormalizeBranch(string? branch) => string.IsNullOrWhiteSpace(branch) ? null : branch.Trim();
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed class EditKeyComparer : IEqualityComparer<(string Path, string Locator)>
    {
        public static EditKeyComparer Instance { get; } = new();
        public bool Equals((string Path, string Locator) x, (string Path, string Locator) y) =>
            PathComparer.Equals(x.Path, y.Path) && StringComparer.Ordinal.Equals(x.Locator, y.Locator);
        public int GetHashCode((string Path, string Locator) obj) => HashCode.Combine(PathComparer.GetHashCode(obj.Path), obj.Locator);
    }
}
