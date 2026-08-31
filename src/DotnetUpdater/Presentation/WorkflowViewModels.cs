using System.Collections.Immutable;
using DotnetUpdater.Configuration;
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
    ManagePackageRules
}

public sealed record ApplicationActionOption(ApplicationAction Action, string Label, string Description);

public enum PackageRuleState { Normal, Ignored, Forced }

public sealed class PackageRuleViewModel
{
    public PackageRuleViewModel(
        string packageId,
        bool isDiscovered,
        string? projectPath,
        PackageRuleState state,
        string? forcedVersion)
    {
        PackageId = packageId;
        IsDiscovered = isDiscovered;
        ProjectPath = projectPath;
        State = state;
        ForcedVersion = forcedVersion;
    }

    public string PackageId { get; }
    public bool IsDiscovered { get; }
    public string? ProjectPath { get; }
    public PackageRuleState State { get; private set; }
    public string? ForcedVersion { get; private set; }

    public void ToggleIgnored()
    {
        if (State == PackageRuleState.Ignored)
        {
            State = PackageRuleState.Normal;
            return;
        }
        State = PackageRuleState.Ignored;
        ForcedVersion = null;
    }

    public void Force(string version)
    {
        State = PackageRuleState.Forced;
        ForcedVersion = version;
    }

    public void Clear()
    {
        State = PackageRuleState.Normal;
        ForcedVersion = null;
    }

    public string DisplayText
    {
        get
        {
            var rule = State switch
            {
                PackageRuleState.Ignored => "[IGNORED]",
                PackageRuleState.Forced => $"[FORCED → {ForcedVersion}]",
                _ => "[UPDATES ENABLED]"
            };
            var discovered = IsDiscovered ? string.Empty : "  ·  not currently discovered";
            return $"{rule,-28} {PackageId}{discovered}";
        }
    }
}

public sealed class PackageRulesViewModel
{
    public PackageRulesViewModel(
        IEnumerable<PackageOccurrence> occurrences,
        IEnumerable<string> ignoredPackages,
        IEnumerable<PackageVersionLock> forcedVersions)
    {
        var discovered = occurrences
            .GroupBy(x => x.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key.Trim(), x => x.First().ProjectPath, StringComparer.OrdinalIgnoreCase);
        var ignored = Normalize(ignoredPackages).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var forced = forcedVersions
            .Where(x => !string.IsNullOrWhiteSpace(x.PackageId) && !string.IsNullOrWhiteSpace(x.Version))
            .GroupBy(x => x.PackageId.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Last().Version.Trim(), StringComparer.OrdinalIgnoreCase);
        var all = discovered.Keys.Concat(ignored).Concat(forced.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);

        Items = all.Select(packageId =>
        {
            var isDiscovered = discovered.TryGetValue(packageId, out var projectPath);
            var state = ignored.Contains(packageId)
                ? PackageRuleState.Ignored
                : forced.ContainsKey(packageId) ? PackageRuleState.Forced : PackageRuleState.Normal;
            return new PackageRuleViewModel(
                packageId,
                isDiscovered,
                projectPath,
                state,
                forced.GetValueOrDefault(packageId));
        }).ToImmutableArray();
    }

    public ImmutableArray<PackageRuleViewModel> Items { get; }

    public ImmutableArray<string> IgnoredPackages => Items
        .Where(x => x.State == PackageRuleState.Ignored)
        .Select(x => x.PackageId)
        .ToImmutableArray();

    public ImmutableArray<PackageVersionLock> ForcedVersions => Items
        .Where(x => x.State == PackageRuleState.Forced && x.ForcedVersion is not null)
        .Select(x => new PackageVersionLock(x.PackageId, x.ForcedVersion!))
        .ToImmutableArray();

    public static ImmutableArray<string> Normalize(IEnumerable<string> packages) => packages
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Select(x => x.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
        .ToImmutableArray();
}

public static class PackageVersionSearch
{
    public static ImmutableArray<string> Filter(IEnumerable<string> versions, string? search)
    {
        var value = search?.Trim();
        return versions
            .Where(x => string.IsNullOrEmpty(value) || x.Contains(value, StringComparison.OrdinalIgnoreCase))
            .ToImmutableArray();
    }
}

public sealed class PackageDecisionViewModel
{
    private static readonly UpgradeChoice[] ChoiceOrder =
        [UpgradeChoice.NoUpdate, UpgradeChoice.LatestMinor, UpgradeChoice.LatestMajor];

    public PackageDecisionViewModel(PackageGroup group, string? forcedVersion = null)
    {
        Group = group;
        Choice = forcedVersion is null ? UpgradeChoice.NoUpdate : UpgradeChoice.ExactVersion;
        ForcedVersion = forcedVersion;
    }

    public PackageGroup Group { get; }
    public UpgradeChoice Choice { get; private set; }
    public string? ForcedVersion { get; }
    public bool IsForced => Choice == UpgradeChoice.ExactVersion;

    public void Cycle()
    {
        if (IsForced) return;
        var index = Array.IndexOf(ChoiceOrder, Choice);
        Choice = ChoiceOrder[(index + 1) % ChoiceOrder.Length];
    }

    public PackageDecision ToDecision() => IsForced
        ? new(Group.PackageId, UpgradeChoice.ExactVersion, ForcedVersion)
        : UpgradePlanner.AutomaticDecision(Group, Choice);

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
                UpgradeChoice.ExactVersion => $"FORCED → {ForcedVersion}",
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
