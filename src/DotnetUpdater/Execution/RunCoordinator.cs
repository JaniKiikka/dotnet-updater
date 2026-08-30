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
                results.Add(Result(repository, plan, PlannedBranch(plan), RunStage.Skipped, RunStage.Preflight, null, null, null, "Preflight blocked execution."));
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
        string? commit = null;
        var root = repository.RepositoryRoot;
        var branch = PlannedBranch(plan);

        if (plan.Git.UpdatesCurrentBranch)
        {
            var current = await git.CurrentBranchAsync(root, token);
            if (!current.Succeeded || string.IsNullOrWhiteSpace(current.StandardOutput))
                return Failed(repository, plan, branch, RunStage.SwitchBranch, stash, commit, "Could not determine the current branch.", progress);
            branch = current.StandardOutput.Trim();
        }

        Emit(repository, RunStage.Stash, "Checking existing work", progress);
        var dirty = await git.IsDirtyAsync(root, token);
        if (!dirty.Succeeded) return Failed(repository, plan, branch, RunStage.Stash, stash, commit, "Could not inspect working tree.", progress);
        if (dirty.IsDirty)
        {
            var stashMessage = $"dotnet-updater {branch} {DateTimeOffset.UtcNow:O}";
            var stashed = await git.StashAsync(root, stashMessage, token);
            if (!stashed.Succeeded) return Failed(repository, plan, branch, RunStage.Stash, stash, commit, "Could not stash existing work.", progress);
            stash = stashed.Reference;
        }

        if (plan.Git.BaseBranch is { } baseBranch)
        {
            Emit(repository, RunStage.SwitchBranch, $"Switching to {baseBranch}", progress);
            if (!(await git.SwitchAsync(root, baseBranch, token)).Succeeded)
                return Failed(repository, plan, branch, RunStage.SwitchBranch, stash, commit, $"Could not switch base branch {baseBranch}.", progress);

            Emit(repository, RunStage.Synchronize, $"Synchronizing {plan.Git.RemoteName}/{baseBranch}", progress);
            if (!(await git.FetchAsync(root, plan.Git.RemoteName, token)).Succeeded ||
                !(await git.PullAsync(root, plan.Git.RemoteName, baseBranch, token)).Succeeded)
                return Failed(repository, plan, branch, RunStage.Synchronize, stash, commit, "Fetch or fast-forward pull failed.", progress);
        }

        if (plan.Git.TargetBranch is { } targetBranch)
        {
            Emit(repository, RunStage.CreateBranch, $"Creating {targetBranch}", progress);
            if (!(await git.CreateBranchAsync(root, targetBranch, token)).Succeeded)
                return Failed(repository, plan, branch, RunStage.CreateBranch, stash, commit, "Update branch creation failed.", progress);
        }

        Emit(repository, RunStage.ApplyUpdates, "Applying reviewed package versions", progress);
        var edited = editor.Apply(repository.Edits);
        if (!edited.Succeeded) return Failed(repository, plan, branch, RunStage.ApplyUpdates, stash, commit, edited.Error ?? "Package edit failed.", progress);
        if (edited.ChangedPaths.Length == 0)
        {
            Emit(repository, RunStage.Skipped, "No package files changed", progress);
            return Result(repository, plan, branch, RunStage.Skipped, null, stash, null, null, "No package files changed.");
        }

        foreach (var target in repository.ValidationTargets)
        {
            Emit(repository, RunStage.Restore, $"Restoring {Path.GetFileName(target)}", progress);
            if (!(await Dotnet(root, token, "restore", target)).Succeeded)
                return Failed(repository, plan, branch, RunStage.Restore, stash, commit, "Restore failed. See the retained log.", progress);
        }

        foreach (var target in repository.ValidationTargets)
        {
            Emit(repository, RunStage.Build, $"Building {Path.GetFileName(target)}", progress);
            if (!(await Dotnet(root, token, "build", target, "--no-restore")).Succeeded)
                return Failed(repository, plan, branch, RunStage.Build, stash, commit, "Build failed. See the retained log.", progress);
        }

        foreach (var target in repository.ValidationTargets)
        {
            Emit(repository, RunStage.Test, $"Testing {Path.GetFileName(target)}", progress);
            if (!(await Dotnet(root, token, "test", target, "--no-build", "--no-restore")).Succeeded)
                return Failed(repository, plan, branch, RunStage.Test, stash, commit, "Tests failed. See the retained log.", progress);
        }

        if (!plan.Git.CommitAndPush)
        {
            Emit(repository, RunStage.Passed, "Passed; changes left uncommitted", progress);
            return Result(repository, plan, branch, RunStage.Passed, null, stash, null, null, "Changes were left uncommitted.");
        }

        Emit(repository, RunStage.Commit, "Staging reviewed declaration files", progress);
        if (!(await git.StageAsync(root, edited.ChangedPaths, token)).Succeeded)
            return Failed(repository, plan, branch, RunStage.Commit, stash, commit, "Selective staging failed.", progress);
        var staged = await git.HasStagedChangesAsync(root, token);
        if (staged.ExitCode == 0)
        {
            Emit(repository, RunStage.Skipped, "No package changes to commit", progress);
            return Result(repository, plan, branch, RunStage.Skipped, null, stash, null, null, "No package changes to commit.");
        }
        if (staged.ExitCode != 1)
            return Failed(repository, plan, branch, RunStage.Commit, stash, commit, "Could not inspect staged changes.", progress);
        if (!(await git.CommitAsync(root, $"{branch} .NET nuget package update", token)).Succeeded)
            return Failed(repository, plan, branch, RunStage.Commit, stash, commit, "Commit failed.", progress);
        var head = await git.HeadAsync(root, token);
        commit = head.Succeeded ? head.StandardOutput.Trim() : null;

        Emit(repository, RunStage.Push, $"Pushing {plan.Git.RemoteName}/{branch}", progress);
        if (!(await git.PushAsync(root, plan.Git.RemoteName, branch, token)).Succeeded)
            return Failed(repository, plan, branch, RunStage.Push, stash, commit, "Push failed; the local commit remains available.", progress);

        Emit(repository, RunStage.Passed, "Passed", progress);
        return Result(repository, plan, branch, RunStage.Passed, null, stash, commit, $"{plan.Git.RemoteName}/{branch}", null);
    }

    private Task<ProcessResult> Dotnet(string root, CancellationToken token, params string[] args) =>
        runner.RunAsync(new("dotnet", args, root), token);

    private RepositoryRunResult Failed(RepositoryPlan repository, UpgradePlan plan, string branch, RunStage stage, string? stash, string? commit, string message, IProgress<ProgressEvent>? progress)
    {
        logger.Write($"{repository.RepositoryRoot}: failed at {stage}: {message}");
        Emit(repository, RunStage.Failed, $"Failed at {stage}", progress);
        return Result(repository, plan, branch, RunStage.Failed, stage, stash, commit, null, message);
    }

    private RepositoryRunResult Result(RepositoryPlan repository, UpgradePlan plan, string branch, RunStage status, RunStage? failedStage, string? stash, string? commit, string? remoteBranch, string? message) =>
        new(repository.RepositoryRoot, status, failedStage, stash, branch, commit, remoteBranch,
            repository.Edits.Select(x => new ChangedPackage(x.PackageId, x.OldVersion, x.TargetVersion))
                .Distinct().OrderBy(x => x.PackageId, StringComparer.OrdinalIgnoreCase).ToImmutableArray(),
            logger.Path, message);

    private static string PlannedBranch(UpgradePlan plan) =>
        plan.Git.TargetBranch ?? plan.Git.BaseBranch ?? "current branch";

    private static void Emit(RepositoryPlan repository, RunStage stage, string message, IProgress<ProgressEvent>? progress) =>
        progress?.Report(new(repository.RepositoryRoot, stage, message));
}
