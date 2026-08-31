using System.Collections.Immutable;
using DotnetUpdater.Domain;
using SharpConsoleUI.Parsing;

namespace DotnetUpdater.Presentation;

public static class PresentationText
{
    public static string Review(UpgradePlan plan)
    {
        var lines = new List<string>
        {
            $"[bold]Strategy:[/] {(plan.Strategy == UpgradeStrategy.ValidatedIncremental ? "Validated incremental" : "Batch update")}",
            $"[bold]Base:[/] {BaseDescription(plan.Git)}",
            $"[bold]Updates:[/] {TargetDescription(plan.Git)}",
            $"[bold]Delivery:[/] {(plan.Git.CommitAndPush ? $"Commit and push to {Escape(plan.Git.RemoteName)}" : "Leave changes uncommitted")}",
            ""
        };
        foreach (var repository in plan.Repositories)
        {
            lines.Add($"[bold cyan]{Escape(Path.GetFileName(repository.RepositoryRoot))}[/]");
            lines.Add($"[dim]{Escape(repository.RepositoryRoot)}[/]");
            if (plan.Strategy == UpgradeStrategy.ValidatedIncremental)
            {
                lines.Add("  [bold]Baseline:[/] restore, build, and test before package edits");
                var firstParty = repository.ValidatedUpdates.Where(x => x.IsFirstParty).ToArray();
                if (firstParty.Length > 0)
                    lines.Add($"  [bold]Microsoft first-party batch:[/] {string.Join(", ", firstParty.Select(x => Escape(x.PackageId)))}");
                if (repository.ValidatedUpdates.Any(x => !x.IsFirstParty))
                    lines.Add("  [bold]Third-party sequence:[/] latest major, then latest minor fallback when needed");
            }
            if (plan.Strategy == UpgradeStrategy.ValidatedIncremental)
            {
                foreach (var update in repository.ValidatedUpdates)
                {
                    var oldVersions = string.Join(", ", update.PreferredEdits.Select(x => x.OldVersion)
                        .Distinct(StringComparer.OrdinalIgnoreCase).Select(Escape));
                    var preferredTargets = string.Join(", ", update.PreferredEdits.Select(x => x.TargetVersion)
                        .Distinct(StringComparer.OrdinalIgnoreCase).Select(Escape));
                    var forced = update.IsForced ? " [bold magenta]FORCED[/]" : " [bold yellow]LATEST FIRST[/]";
                    lines.Add($"  • {Escape(update.PackageId)}: {oldVersions} → {preferredTargets}{forced}");
                    if (update.FallbackEdits.Length > 0)
                    {
                        var fallbackTargets = string.Join(", ", update.FallbackEdits.Select(x => x.TargetVersion)
                            .Distinct(StringComparer.OrdinalIgnoreCase).Select(Escape));
                        lines.Add($"    [dim]fallback → {fallbackTargets}[/]");
                    }
                }
            }
            else
            {
                foreach (var edit in repository.Edits)
                {
                    var major = IsMajor(edit) ? " [bold yellow]MAJOR[/]" : string.Empty;
                    var forced = edit.IsForced ? " [bold magenta]FORCED[/]" : string.Empty;
                    lines.Add($"  • {Escape(edit.PackageId)}: {Escape(edit.OldVersion)} → {Escape(edit.TargetVersion)}{major}{forced}");
                }
            }
            lines.Add("");
        }
        return string.Join('\n', lines);
    }

    public static string Preflight(IEnumerable<RepositoryPreflight> results)
    {
        var lines = new List<string>();
        foreach (var item in results)
        {
            var status = item.IsReady ? "[green]✓ READY[/]" : "[yellow]○ SKIPPED[/]";
            lines.Add($"{status}  [bold]{Escape(Path.GetFileName(item.RepositoryRoot))}[/]");
            foreach (var issue in item.Issues)
                lines.Add($"  • {Escape(issue.Message)}");
        }
        return string.Join('\n', lines);
    }

    public static string Progress(IEnumerable<ProgressEvent> events)
    {
        return string.Join('\n', events.Select(value =>
        {
            var (icon, color) = value.Stage switch
            {
                RunStage.Passed => ("✓", "green"),
                RunStage.Failed => ("✗", "red"),
                RunStage.Skipped => ("○", "yellow"),
                RunStage.Queued => ("·", "dim"),
                _ => ("◆", "cyan")
            };
            return $"[{color}]{icon} {Escape(Path.GetFileName(value.RepositoryRoot))}[/]  " +
                $"{StageLabel(value.Stage)} — {Escape(value.Message)}";
        }));
    }

    public static string Summary(ImmutableArray<RepositoryRunResult> results)
    {
        var lines = new List<string>
        {
            $"[bold green]Passed: {results.Count(x => x.Status == RunStage.Passed)}[/]   " +
            $"[bold red]Failed: {results.Count(x => x.Status == RunStage.Failed)}[/]   " +
            $"[bold yellow]Skipped: {results.Count(x => x.Status == RunStage.Skipped)}[/]",
            ""
        };
        foreach (var result in results)
        {
            var (icon, color) = result.Status switch
            {
                RunStage.Passed => ("✓", "green"),
                RunStage.Failed => ("✗", "red"),
                _ => ("○", "yellow")
            };
            lines.Add($"[bold {color}]{icon} {Escape(Path.GetFileName(result.RepositoryRoot))} — {result.Status}[/]");
            lines.Add($"  [dim]{Escape(result.RepositoryRoot)}[/]");
            if (result.FailedStage is not null) lines.Add($"  Failed stage: {StageLabel(result.FailedStage.Value)}");
            lines.Add($"  Branch: {Escape(result.BranchName)}");
            if (result.StashReference is not null) lines.Add($"  Stash: {Escape(result.StashReference)} [yellow](not restored)[/]");
            if (result.CommitId is not null) lines.Add($"  Commit: {Escape(result.CommitId)}");
            if (result.RemoteBranch is not null) lines.Add($"  Remote: {Escape(result.RemoteBranch)}");
            foreach (var package in result.ChangedPackages)
                lines.Add($"  • {Escape(package.PackageId)}: {Escape(package.OldVersion)} → {Escape(package.TargetVersion)}");
            foreach (var package in result.PackageResults.Where(x => x.Status == PackageUpdateStatus.Failed))
                lines.Add($"  [red]✗ Package not updated: {Escape(package.PackageId)} — {Escape(package.Message)}[/]");
            foreach (var package in result.PackageResults.Where(x => x.Status == PackageUpdateStatus.UpdatedWithFallback))
                lines.Add($"  [yellow]↳ Minor fallback: {Escape(package.PackageId)} → {Escape(package.TargetVersion ?? "unknown")}[/]");
            if (result.Message is not null) lines.Add($"  {Escape(result.Message)}");
            lines.Add($"  Log: {Escape(result.LogPath)}");
            lines.Add("");
        }
        return string.Join('\n', lines);
    }

    public static string StageLabel(RunStage stage) => stage switch
    {
        RunStage.Stash => "Stashing work",
        RunStage.SwitchBranch => "Switching branch",
        RunStage.Synchronize => "Synchronizing",
        RunStage.CreateBranch => "Creating update branch",
        RunStage.ApplyUpdates => "Updating packages",
        RunStage.Restore => "Restoring",
        RunStage.Build => "Building",
        RunStage.Test => "Testing",
        RunStage.Commit => "Committing",
        RunStage.Push => "Pushing",
        _ => stage.ToString()
    };

    public static string Escape(string value) => MarkupParser.Escape(value);

    private static string BaseDescription(GitWorkflowOptions git) => git.BaseBranch is null
        ? "Current branch (no switch or pull)"
        : $"{Escape(git.RemoteName)}/{Escape(git.BaseBranch)}";

    private static string TargetDescription(GitWorkflowOptions git) => git.TargetBranch is not null
        ? $"New branch {Escape(git.TargetBranch)}"
        : git.BaseBranch is not null
            ? $"Directly on {Escape(git.BaseBranch)}"
            : "Directly on each repository's current branch";

    private static bool IsMajor(DeclarationEdit edit) =>
        SemanticVersion.TryParse(edit.OldVersion, out var oldVersion) &&
        SemanticVersion.TryParse(edit.TargetVersion, out var targetVersion) &&
        targetVersion.Major > oldVersion.Major;
}
