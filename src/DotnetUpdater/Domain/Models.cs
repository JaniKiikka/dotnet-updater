using System.Collections.Immutable;

namespace DotnetUpdater.Domain;

public sealed record RepositoryInfo(string RootPath, ImmutableArray<SelectionEntry> Entries);

public sealed record SelectionEntry(
    string Path,
    string RepositoryRoot,
    EntryKind Kind,
    ImmutableArray<string> ProjectPaths)
{
    public string DisplayName => System.IO.Path.GetFileName(Path);
}

public enum EntryKind { Solution, SolutionXml, StandaloneProject }

public sealed record DiscoveryWarning(string Path, string Message);

public sealed record DiscoveryResult(
    ImmutableArray<RepositoryInfo> Repositories,
    ImmutableArray<DiscoveryWarning> Warnings)
{
    public ImmutableArray<SelectionEntry> Entries => Repositories.SelectMany(x => x.Entries).ToImmutableArray();
}

public enum DeclarationKind { PackageReferenceAttribute, PackageReferenceElement, CentralPackageVersion }

public sealed record PackageDeclaration(
    string Path,
    string PackageId,
    string CurrentVersion,
    DeclarationKind Kind,
    string Locator);

public sealed record PackageOccurrence(
    string PackageId,
    string CurrentVersion,
    string ProjectPath,
    PackageDeclaration Declaration,
    string? UnsupportedReason = null);

public sealed record PackageGroup(
    string PackageId,
    ImmutableArray<PackageOccurrence> Occurrences,
    SemanticVersion? LatestMinor,
    SemanticVersion? LatestMajor,
    string? ResolutionError)
{
    public int HighestCurrentMajor => Occurrences
        .Select(x => SemanticVersion.TryParse(x.CurrentVersion, out var version) ? version.Major : 0)
        .DefaultIfEmpty(0).Max();
}

public enum UpgradeChoice { LatestMinor, LatestMajor, NoUpdate }

public sealed record PackageDecision(string PackageId, UpgradeChoice Choice, string? TargetVersion);

public sealed record DeclarationEdit(
    string RepositoryRoot,
    string DeclarationPath,
    string PackageId,
    string OldVersion,
    string TargetVersion,
    DeclarationKind Kind,
    string Locator);

public sealed record RepositoryPlan(
    string RepositoryRoot,
    ImmutableArray<string> ValidationTargets,
    ImmutableArray<DeclarationEdit> Edits);

public sealed record GitWorkflowOptions(
    string RemoteName,
    string? BaseBranch,
    string? TargetBranch,
    bool CommitAndPush)
{
    public bool UpdatesCurrentBranch => BaseBranch is null && TargetBranch is null;
}

public sealed record UpgradePlan(
    GitWorkflowOptions Git,
    ImmutableArray<RepositoryPlan> Repositories,
    DateTimeOffset CreatedAt);

public enum RunStage
{
    Queued, Preflight, Stash, SwitchBranch, Synchronize, CreateBranch,
    ApplyUpdates, Restore, Build, Test, Commit, Push, Passed, Failed, Skipped
}

public sealed class RepositoryStateMachine
{
    private static readonly IReadOnlyDictionary<RunStage, RunStage[]> LegalTransitions = new Dictionary<RunStage, RunStage[]>
    {
        [RunStage.Queued] = [RunStage.Preflight],
        [RunStage.Preflight] = [RunStage.Stash, RunStage.Skipped],
        [RunStage.Stash] = [RunStage.SwitchBranch, RunStage.CreateBranch, RunStage.ApplyUpdates, RunStage.Failed],
        [RunStage.SwitchBranch] = [RunStage.Synchronize, RunStage.Failed],
        [RunStage.Synchronize] = [RunStage.CreateBranch, RunStage.ApplyUpdates, RunStage.Failed],
        [RunStage.CreateBranch] = [RunStage.ApplyUpdates, RunStage.Failed],
        [RunStage.ApplyUpdates] = [RunStage.Restore, RunStage.Skipped, RunStage.Failed],
        [RunStage.Restore] = [RunStage.Restore, RunStage.Build, RunStage.Failed],
        [RunStage.Build] = [RunStage.Build, RunStage.Test, RunStage.Failed],
        [RunStage.Test] = [RunStage.Test, RunStage.Commit, RunStage.Passed, RunStage.Failed],
        [RunStage.Commit] = [RunStage.Push, RunStage.Skipped, RunStage.Failed],
        [RunStage.Push] = [RunStage.Passed, RunStage.Failed],
        [RunStage.Passed] = [], [RunStage.Failed] = [], [RunStage.Skipped] = []
    };

    public RunStage Current { get; private set; } = RunStage.Queued;
    public void MoveTo(RunStage next)
    {
        if (!LegalTransitions[Current].Contains(next)) throw new InvalidOperationException($"Illegal run-stage transition: {Current} -> {next}.");
        Current = next;
    }
}

public sealed record PreflightIssue(string RepositoryRoot, string Message);

public sealed record RepositoryPreflight(
    string RepositoryRoot,
    bool IsReady,
    ImmutableArray<PreflightIssue> Issues);

public sealed record ProgressEvent(string RepositoryRoot, RunStage Stage, string Message);

public sealed record ChangedPackage(string PackageId, string OldVersion, string TargetVersion);

public sealed record RepositoryRunResult(
    string RepositoryRoot,
    RunStage Status,
    RunStage? FailedStage,
    string? StashReference,
    string BranchName,
    string? CommitId,
    string? RemoteBranch,
    ImmutableArray<ChangedPackage> ChangedPackages,
    string LogPath,
    string? Message);

public readonly record struct SemanticVersion(int Major, int Minor, int Patch, string? Suffix = null)
    : IComparable<SemanticVersion>
{
    public bool IsPrerelease => !string.IsNullOrEmpty(Suffix);

    public int CompareTo(SemanticVersion other)
    {
        var result = Major.CompareTo(other.Major);
        if (result != 0) return result;
        result = Minor.CompareTo(other.Minor);
        if (result != 0) return result;
        result = Patch.CompareTo(other.Patch);
        if (result != 0) return result;
        if (Suffix is null && other.Suffix is not null) return 1;
        if (Suffix is not null && other.Suffix is null) return -1;
        return string.Compare(Suffix, other.Suffix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryParse(string? value, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = value.Trim();
        var plus = normalized.IndexOf('+');
        if (plus >= 0) normalized = normalized[..plus];
        string? suffix = null;
        var dash = normalized.IndexOf('-');
        if (dash >= 0)
        {
            suffix = normalized[(dash + 1)..];
            normalized = normalized[..dash];
        }
        var parts = normalized.Split('.');
        if (parts.Length is < 1 or > 4 || !int.TryParse(parts[0], out var major)) return false;
        var minor = parts.Length > 1 && int.TryParse(parts[1], out var parsedMinor) ? parsedMinor : 0;
        var patch = parts.Length > 2 && int.TryParse(parts[2], out var parsedPatch) ? parsedPatch : 0;
        if ((parts.Length > 1 && !int.TryParse(parts[1], out _)) ||
            (parts.Length > 2 && !int.TryParse(parts[2], out _))) return false;
        version = new SemanticVersion(major, minor, patch, suffix);
        return true;
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}{(Suffix is null ? string.Empty : $"-{Suffix}")}";
}
