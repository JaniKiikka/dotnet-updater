using System.Collections.Immutable;
using DotnetUpdater.Domain;

namespace DotnetUpdater.Execution;

public sealed class RunCoordinator(
    IProcessRunner runner,
    GitService git,
    PackageEditor editor,
    IRunLogger logger)
{
    public async Task<ImmutableArray<RepositoryRunResult>> ExecuteAsync(
        UpgradePlan plan,
        IReadOnlyDictionary<string, RepositoryPreflight> preflight,
        IProgress<ProgressEvent>? progress,
        CancellationToken cancellationToken)
    {
        var results = ImmutableArray.CreateBuilder<RepositoryRunResult>();
        foreach (var repository in plan.Repositories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!preflight.TryGetValue(repository.RepositoryRoot, out var readiness) || !readiness.IsReady)
            {
                Emit(repository, RunStage.Skipped, "Preflight blocked this repository.", progress);
                results.Add(Result(repository, plan, PlannedBranch(plan), RunStage.Skipped, RunStage.Preflight,
                    null, null, null, "Preflight blocked execution."));
                continue;
            }
            results.Add(await ExecuteRepositoryAsync(repository, plan, progress, cancellationToken));
        }
        return results.ToImmutable();
    }

    private async Task<RepositoryRunResult> ExecuteRepositoryAsync(
        RepositoryPlan repository,
        UpgradePlan plan,
        IProgress<ProgressEvent>? progress,
        CancellationToken token)
    {
        string? stash = null;
        var root = repository.RepositoryRoot;
        var branch = PlannedBranch(plan);

        if (plan.Git.UpdatesCurrentBranch)
        {
            var current = await git.CurrentBranchAsync(root, token);
            if (!current.Succeeded || string.IsNullOrWhiteSpace(current.StandardOutput))
                return Failed(repository, plan, branch, RunStage.SwitchBranch, stash, null,
                    "Could not determine the current branch.", progress);
            branch = current.StandardOutput.Trim();
        }

        Emit(repository, RunStage.Stash, "Checking existing work", progress);
        var dirty = await git.IsDirtyAsync(root, token);
        if (!dirty.Succeeded)
            return Failed(repository, plan, branch, RunStage.Stash, stash, null,
                "Could not inspect working tree.", progress);
        if (dirty.IsDirty)
        {
            var stashMessage = $"dotnet-updater {branch} {DateTimeOffset.UtcNow:O}";
            var stashed = await git.StashAsync(root, stashMessage, token);
            if (!stashed.Succeeded)
                return Failed(repository, plan, branch, RunStage.Stash, stash, null,
                    "Could not stash existing work.", progress);
            stash = stashed.Reference;
        }

        if (plan.Git.BaseBranch is { } baseBranch)
        {
            Emit(repository, RunStage.SwitchBranch, $"Switching to {baseBranch}", progress);
            if (!(await git.SwitchAsync(root, baseBranch, token)).Succeeded)
                return Failed(repository, plan, branch, RunStage.SwitchBranch, stash, null,
                    $"Could not switch base branch {baseBranch}.", progress);

            Emit(repository, RunStage.Synchronize, $"Synchronizing {plan.Git.RemoteName}/{baseBranch}", progress);
            if (!(await git.FetchAsync(root, plan.Git.RemoteName, token)).Succeeded ||
                !(await git.PullAsync(root, plan.Git.RemoteName, baseBranch, token)).Succeeded)
                return Failed(repository, plan, branch, RunStage.Synchronize, stash, null,
                    "Fetch or fast-forward pull failed.", progress);
        }

        if (plan.Git.TargetBranch is { } targetBranch)
        {
            Emit(repository, RunStage.CreateBranch, $"Creating {targetBranch}", progress);
            if (!(await git.CreateBranchAsync(root, targetBranch, token)).Succeeded)
                return Failed(repository, plan, branch, RunStage.CreateBranch, stash, null,
                    "Update branch creation failed.", progress);
        }

        return plan.Strategy == UpgradeStrategy.ValidatedIncremental
            ? await ExecuteValidatedIncrementalAsync(repository, plan, branch, stash, progress, token)
            : await ExecuteBatchAsync(repository, plan, branch, stash, progress, token);
    }

    private async Task<RepositoryRunResult> ExecuteBatchAsync(
        RepositoryPlan repository,
        UpgradePlan plan,
        string branch,
        string? stash,
        IProgress<ProgressEvent>? progress,
        CancellationToken token)
    {
        Emit(repository, RunStage.ApplyUpdates, "Applying reviewed package versions", progress);
        var edited = editor.Apply(repository.Edits);
        if (!edited.Succeeded)
            return Failed(repository, plan, branch, RunStage.ApplyUpdates, stash, null,
                edited.Error ?? "Package edit failed.", progress);
        if (edited.ChangedPaths.Length == 0)
        {
            Emit(repository, RunStage.Skipped, "No package files changed", progress);
            return Result(repository, plan, branch, RunStage.Skipped, null, stash, null, null,
                "No package files changed.");
        }

        var validation = await ValidateAsync(repository, "Updated repository", null, progress, token);
        if (validation is not null)
            return Failed(repository, plan, branch, validation.Stage, stash, null, validation.Message, progress);

        var changed = repository.Edits.Select(ToChangedPackage).ToImmutableArray();
        return await FinishAsync(repository, plan, branch, stash, edited.ChangedPaths, changed, [], progress, token);
    }

    private async Task<RepositoryRunResult> ExecuteValidatedIncrementalAsync(
        RepositoryPlan repository,
        UpgradePlan plan,
        string branch,
        string? stash,
        IProgress<ProgressEvent>? progress,
        CancellationToken token)
    {
        var acceptedChanges = ImmutableArray.CreateBuilder<ChangedPackage>();
        var packageResults = ImmutableArray.CreateBuilder<PackageUpdateResult>();
        var changedPaths = new HashSet<string>(PathComparer);

        var baseline = await ValidateAsync(repository, "Baseline", null, progress, token);
        if (baseline is not null)
            return Failed(repository, plan, branch, baseline.Stage, stash, null,
                $"Baseline validation failed. {baseline.Message}", progress,
                packageResults: packageResults.ToImmutable());

        var firstParty = repository.ValidatedUpdates.Where(x => x.IsFirstParty).ToArray();
        if (firstParty.Length > 0)
        {
            var packageNames = string.Join(", ", firstParty.Select(x => x.PackageId));
            var edits = firstParty.SelectMany(x => x.PreferredEdits).ToImmutableArray();
            Emit(repository, RunStage.ApplyUpdates, $"Updating Microsoft first-party packages: {packageNames}", progress);
            var applied = editor.Apply(edits);
            if (!applied.Succeeded)
                return Failed(repository, plan, branch, RunStage.ApplyUpdates, stash, null,
                    applied.Error ?? "Microsoft first-party package edit failed.", progress,
                    packageResults: packageResults.ToImmutable());

            var validation = await ValidateAsync(repository, "Microsoft first-party batch", packageNames, progress, token);
            if (validation is not null)
            {
                foreach (var update in firstParty)
                {
                    packageResults.Add(new(update.PackageId, PackageUpdateStatus.Failed, null,
                        "The Microsoft first-party batch did not pass validation."));
                    Emit(repository, RunStage.ApplyUpdates,
                        $"Microsoft first-party validation failed for {update.PackageId}", progress, update.PackageId,
                        PackageUpdateStatus.Failed);
                }
                if (!Rollback(edits, out var rollbackError))
                    return Failed(repository, plan, branch, RunStage.ApplyUpdates, stash, null,
                        $"{validation.Message} Rollback also failed: {rollbackError}", progress,
                        packageResults: packageResults.ToImmutable());
                return Failed(repository, plan, branch, validation.Stage, stash, null,
                    $"Microsoft first-party package validation failed; its edits were rolled back. {validation.Message}",
                    progress, packageResults: packageResults.ToImmutable());
            }

            foreach (var update in firstParty)
            {
                acceptedChanges.AddRange(update.PreferredEdits.Select(ToChangedPackage));
                packageResults.Add(new(update.PackageId, PackageUpdateStatus.Updated,
                    TargetDescription(update.PreferredEdits), "Updated in the Microsoft first-party batch."));
            }
            foreach (var path in applied.ChangedPaths) changedPaths.Add(path);
        }

        foreach (var update in repository.ValidatedUpdates.Where(x => !x.IsFirstParty))
        {
            token.ThrowIfCancellationRequested();
            var preferredTarget = TargetDescription(update.PreferredEdits);
            Emit(repository, RunStage.ApplyUpdates,
                $"Updating {update.PackageId} to {preferredTarget}", progress, update.PackageId);
            var applied = editor.Apply(update.PreferredEdits);
            if (!applied.Succeeded)
                return Failed(repository, plan, branch, RunStage.ApplyUpdates, stash, null,
                    applied.Error ?? $"Could not edit {update.PackageId}.", progress,
                    packageResults: packageResults.ToImmutable());

            var validation = await ValidateAsync(repository, $"{update.PackageId} {preferredTarget}",
                update.PackageId, progress, token);
            if (validation is null)
            {
                acceptedChanges.AddRange(update.PreferredEdits.Select(ToChangedPackage));
                packageResults.Add(new(update.PackageId, PackageUpdateStatus.Updated, preferredTarget,
                    update.IsForced ? "The forced version passed validation." : "The latest major version passed validation."));
                foreach (var path in applied.ChangedPaths) changedPaths.Add(path);
                continue;
            }

            if (!Rollback(update.PreferredEdits, out var rollbackError))
                return Failed(repository, plan, branch, RunStage.ApplyUpdates, stash, null,
                    $"{update.PackageId} failed validation and rollback failed: {rollbackError}", progress,
                    packageResults: packageResults.ToImmutable());

            var fallback = update.FallbackEdits;
            var fallbackTarget = TargetDescription(fallback);
            if (update.IsForced || fallback.Length == 0 ||
                string.Equals(preferredTarget, fallbackTarget, StringComparison.OrdinalIgnoreCase))
            {
                packageResults.Add(new(update.PackageId, PackageUpdateStatus.Failed, null,
                    update.IsForced
                        ? "The forced version failed validation; the working version was restored."
                        : "The major version failed and no distinct minor update was available; the working version was restored."));
                Emit(repository, RunStage.ApplyUpdates,
                    $"Could not update {update.PackageId}; restored its working version", progress, update.PackageId,
                    PackageUpdateStatus.Failed);
                continue;
            }

            Emit(repository, RunStage.ApplyUpdates,
                $"Major update failed; trying {update.PackageId} minor fallback {fallbackTarget}", progress, update.PackageId);
            var fallbackApplied = editor.Apply(fallback);
            if (!fallbackApplied.Succeeded)
                return Failed(repository, plan, branch, RunStage.ApplyUpdates, stash, null,
                    fallbackApplied.Error ?? $"Could not apply the minor fallback for {update.PackageId}.", progress,
                    packageResults: packageResults.ToImmutable());

            var fallbackValidation = await ValidateAsync(repository, $"{update.PackageId} fallback {fallbackTarget}",
                update.PackageId, progress, token);
            if (fallbackValidation is null)
            {
                acceptedChanges.AddRange(fallback.Select(ToChangedPackage));
                packageResults.Add(new(update.PackageId, PackageUpdateStatus.UpdatedWithFallback, fallbackTarget,
                    "The major update failed; the latest minor update passed validation."));
                foreach (var path in fallbackApplied.ChangedPaths) changedPaths.Add(path);
                continue;
            }

            if (!Rollback(fallback, out rollbackError))
                return Failed(repository, plan, branch, RunStage.ApplyUpdates, stash, null,
                    $"{update.PackageId} minor fallback failed validation and rollback failed: {rollbackError}", progress,
                    packageResults: packageResults.ToImmutable());
            packageResults.Add(new(update.PackageId, PackageUpdateStatus.Failed, null,
                "Both major and minor updates failed validation; the working version was restored."));
            Emit(repository, RunStage.ApplyUpdates,
                $"Could not update {update.PackageId}; restored its working version", progress, update.PackageId,
                PackageUpdateStatus.Failed);
        }

        if (acceptedChanges.Count == 0)
        {
            Emit(repository, RunStage.Passed, "Baseline passed; no package update passed validation", progress);
            return Result(repository, plan, branch, RunStage.Passed, null, stash, null, null,
                "The repository is healthy, but no package update passed validation.", [], packageResults.ToImmutable());
        }

        return await FinishAsync(repository, plan, branch, stash, changedPaths, acceptedChanges.ToImmutable(),
            packageResults.ToImmutable(), progress, token);
    }

    private async Task<ValidationFailure?> ValidateAsync(
        RepositoryPlan repository,
        string phase,
        string? packageId,
        IProgress<ProgressEvent>? progress,
        CancellationToken token)
    {
        foreach (var target in repository.ValidationTargets)
        {
            Emit(repository, RunStage.Restore, $"{phase}: restoring {Path.GetFileName(target)}", progress, packageId);
            if (!(await Dotnet(repository.RepositoryRoot, token, "restore", target)).Succeeded)
                return new(RunStage.Restore, $"Restore failed during {phase}. See the retained log.");
        }
        foreach (var target in repository.ValidationTargets)
        {
            Emit(repository, RunStage.Build, $"{phase}: building {Path.GetFileName(target)}", progress, packageId);
            if (!(await Dotnet(repository.RepositoryRoot, token, "build", target, "--no-restore")).Succeeded)
                return new(RunStage.Build, $"Build failed during {phase}. See the retained log.");
        }
        foreach (var target in repository.ValidationTargets)
        {
            Emit(repository, RunStage.Test, $"{phase}: testing {Path.GetFileName(target)}", progress, packageId);
            if (!(await Dotnet(repository.RepositoryRoot, token, "test", target, "--no-build", "--no-restore")).Succeeded)
                return new(RunStage.Test, $"Tests failed during {phase}. See the retained log.");
        }
        return null;
    }

    private async Task<RepositoryRunResult> FinishAsync(
        RepositoryPlan repository,
        UpgradePlan plan,
        string branch,
        string? stash,
        IEnumerable<string> changedPaths,
        ImmutableArray<ChangedPackage> changedPackages,
        ImmutableArray<PackageUpdateResult> packageResults,
        IProgress<ProgressEvent>? progress,
        CancellationToken token)
    {
        if (!plan.Git.CommitAndPush)
        {
            Emit(repository, RunStage.Passed, "Passed; validated changes left uncommitted", progress);
            return Result(repository, plan, branch, RunStage.Passed, null, stash, null, null,
                "Changes were left uncommitted.", changedPackages, packageResults);
        }

        Emit(repository, RunStage.Commit, "Staging validated declaration files", progress);
        if (!(await git.StageAsync(repository.RepositoryRoot, changedPaths.Distinct(PathComparer), token)).Succeeded)
            return Failed(repository, plan, branch, RunStage.Commit, stash, null,
                "Selective staging failed.", progress, changedPackages, packageResults);
        var staged = await git.HasStagedChangesAsync(repository.RepositoryRoot, token);
        if (staged.ExitCode == 0)
        {
            Emit(repository, RunStage.Skipped, "No package changes to commit", progress);
            return Result(repository, plan, branch, RunStage.Skipped, null, stash, null, null,
                "No package changes to commit.", changedPackages, packageResults);
        }
        if (staged.ExitCode != 1)
            return Failed(repository, plan, branch, RunStage.Commit, stash, null,
                "Could not inspect staged changes.", progress, changedPackages, packageResults);
        if (!(await git.CommitAsync(repository.RepositoryRoot, $"{branch} .NET nuget package update", token)).Succeeded)
            return Failed(repository, plan, branch, RunStage.Commit, stash, null,
                "Commit failed.", progress, changedPackages, packageResults);
        var head = await git.HeadAsync(repository.RepositoryRoot, token);
        var commit = head.Succeeded ? head.StandardOutput.Trim() : null;

        Emit(repository, RunStage.Push, $"Pushing {plan.Git.RemoteName}/{branch}", progress);
        if (!(await git.PushAsync(repository.RepositoryRoot, plan.Git.RemoteName, branch, token)).Succeeded)
            return Failed(repository, plan, branch, RunStage.Push, stash, commit,
                "Push failed; the local commit remains available.", progress, changedPackages, packageResults);

        Emit(repository, RunStage.Passed, "Passed", progress);
        return Result(repository, plan, branch, RunStage.Passed, null, stash, commit,
            $"{plan.Git.RemoteName}/{branch}", null, changedPackages, packageResults);
    }

    private bool Rollback(IEnumerable<DeclarationEdit> edits, out string? error)
    {
        var reverse = edits.Select(edit => edit with
        {
            OldVersion = edit.TargetVersion,
            TargetVersion = edit.OldVersion
        }).ToArray();
        var result = editor.Apply(reverse);
        error = result.Error;
        return result.Succeeded;
    }

    private Task<ProcessResult> Dotnet(string root, CancellationToken token, params string[] args) =>
        runner.RunAsync(new("dotnet", args, root), token);

    private RepositoryRunResult Failed(
        RepositoryPlan repository,
        UpgradePlan plan,
        string branch,
        RunStage stage,
        string? stash,
        string? commit,
        string message,
        IProgress<ProgressEvent>? progress,
        ImmutableArray<ChangedPackage> changedPackages = default,
        ImmutableArray<PackageUpdateResult> packageResults = default)
    {
        logger.Write($"{repository.RepositoryRoot}: failed at {stage}: {message}");
        Emit(repository, RunStage.Failed, $"Failed at {stage}: {message}", progress);
        return Result(repository, plan, branch, RunStage.Failed, stage, stash, commit, null, message,
            changedPackages.IsDefault ? [] : changedPackages,
            packageResults.IsDefault ? [] : packageResults);
    }

    private RepositoryRunResult Result(
        RepositoryPlan repository,
        UpgradePlan plan,
        string branch,
        RunStage status,
        RunStage? failedStage,
        string? stash,
        string? commit,
        string? remoteBranch,
        string? message,
        ImmutableArray<ChangedPackage> changedPackages = default,
        ImmutableArray<PackageUpdateResult> packageResults = default) =>
        new(repository.RepositoryRoot, status, failedStage, stash, branch, commit, remoteBranch,
            changedPackages.IsDefault
                ? repository.Edits.Select(ToChangedPackage).Distinct().OrderBy(x => x.PackageId, StringComparer.OrdinalIgnoreCase).ToImmutableArray()
                : changedPackages.Distinct().OrderBy(x => x.PackageId, StringComparer.OrdinalIgnoreCase).ToImmutableArray(),
            logger.Path, message)
        {
            PackageResults = packageResults.IsDefault ? [] : packageResults
        };

    private static ChangedPackage ToChangedPackage(DeclarationEdit edit) =>
        new(edit.PackageId, edit.OldVersion, edit.TargetVersion);

    private static string TargetDescription(IEnumerable<DeclarationEdit> edits)
    {
        var targets = edits.Select(x => x.TargetVersion).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return targets.Length == 0 ? "unavailable" : string.Join(", ", targets);
    }

    private static string PlannedBranch(UpgradePlan plan) =>
        plan.Git.TargetBranch ?? plan.Git.BaseBranch ?? "current branch";

    private static void Emit(
        RepositoryPlan repository,
        RunStage stage,
        string message,
        IProgress<ProgressEvent>? progress,
        string? packageId = null,
        PackageUpdateStatus? packageStatus = null) =>
        progress?.Report(new(repository.RepositoryRoot, stage, message, packageId, packageStatus));

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed record ValidationFailure(RunStage Stage, string Message);
}
