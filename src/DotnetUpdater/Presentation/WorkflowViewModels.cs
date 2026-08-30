using System.Collections.Immutable;
using DotnetUpdater.Domain;
using DotnetUpdater.Planning;

namespace DotnetUpdater.Presentation;

public enum UpgradeMode
{
    LatestMinor,
    LatestMajor,
    SelectPackages
}

public sealed record UpgradeModeOption(UpgradeMode Mode, string Label, string Description);

public enum ApplicationAction
{
    UpgradePackages,
    ManageIgnoredPackages
}

public sealed record ApplicationActionOption(ApplicationAction Action, string Label, string Description);

public sealed record IgnoredPackageViewModel(string PackageId, bool IsDiscovered, bool IsIgnored);

public sealed class IgnoredPackagesViewModel
{
    public IgnoredPackagesViewModel(IEnumerable<string> discoveredPackages, IEnumerable<string> ignoredPackages)
    {
        var discovered = Normalize(discoveredPackages);
        var ignored = Normalize(ignoredPackages);
        var ignoredSet = ignored.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var discoveredSet = discovered.ToHashSet(StringComparer.OrdinalIgnoreCase);

        Items = discovered
            .Select(x => new IgnoredPackageViewModel(x, true, ignoredSet.Contains(x)))
            .Concat(ignored.Where(x => !discoveredSet.Contains(x))
                .Select(x => new IgnoredPackageViewModel(x, false, true)))
            .OrderBy(x => x.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
    }

    public ImmutableArray<IgnoredPackageViewModel> Items { get; }

    public static ImmutableArray<string> Normalize(IEnumerable<string> packages) => packages
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Select(x => x.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
        .ToImmutableArray();
}

public sealed class PackageDecisionViewModel
{
    private static readonly UpgradeChoice[] ChoiceOrder =
        [UpgradeChoice.NoUpdate, UpgradeChoice.LatestMinor, UpgradeChoice.LatestMajor];

    public PackageDecisionViewModel(PackageGroup group)
    {
        Group = group;
        Choice = UpgradeChoice.NoUpdate;
    }

    public PackageGroup Group { get; }
    public UpgradeChoice Choice { get; private set; }

    public void Cycle()
    {
        var index = Array.IndexOf(ChoiceOrder, Choice);
        Choice = ChoiceOrder[(index + 1) % ChoiceOrder.Length];
    }

    public PackageDecision ToDecision() => UpgradePlanner.AutomaticDecision(Group, Choice);

    public string DisplayText
    {
        get
        {
            var current = string.Join(", ", Group.Occurrences.Select(x => x.CurrentVersion)
                .Distinct(StringComparer.OrdinalIgnoreCase));
            var action = Choice switch
            {
                UpgradeChoice.LatestMinor => $"minor → {Group.LatestMinor?.ToString() ?? "unavailable"}",
                UpgradeChoice.LatestMajor => $"MAJOR → {Group.LatestMajor?.ToString() ?? "unavailable"}",
                _ => "no update"
            };
            return $"{Group.PackageId}  [{current}]  {action}";
        }
    }
}

public sealed class RepositoryProgressViewModel(IEnumerable<string> repositoryRoots)
{
    private readonly Dictionary<string, ProgressEvent> events = repositoryRoots.ToDictionary(
        x => x,
        x => new ProgressEvent(x, RunStage.Queued, "Waiting"),
        PathComparer);

    public void Apply(ProgressEvent value) => events[value.RepositoryRoot] = value;

    public ImmutableArray<ProgressEvent> Snapshot() => events.Values
        .OrderBy(x => x.RepositoryRoot, PathComparer)
        .ToImmutableArray();

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
