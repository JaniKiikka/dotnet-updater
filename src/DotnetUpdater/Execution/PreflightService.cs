using System.Collections.Immutable;
using DotnetUpdater.Domain;
using DotnetUpdater.IO;

namespace DotnetUpdater.Execution;

public sealed class PreflightService(
    IProcessRunner runner,
    PackageEditor editor,
    IRealPathContainment? containment = null)
{
    private readonly IRealPathContainment _containment = containment ?? new RealPathContainment();

    public async Task<ImmutableArray<RepositoryPreflight>> InspectAsync(UpgradePlan plan, CancellationToken cancellationToken)
    {
        var output = ImmutableArray.CreateBuilder<RepositoryPreflight>();
        foreach (var repository in plan.Repositories)
        {
            var issues = ImmutableArray.CreateBuilder<PreflightIssue>();
            var resolvedProjectsRoot = ResolveStable(repository, repository.ProjectsRoot,
                repository.ResolvedProjectsRoot, "Projects folder", issues);
            var resolvedRepositoryRoot = ResolveStable(repository, repository.RepositoryRoot,
                repository.ResolvedRepositoryRoot, "Repository", issues);
            if (resolvedProjectsRoot is not null && resolvedRepositoryRoot is not null &&
                !_containment.IsWithin(resolvedProjectsRoot, resolvedRepositoryRoot))
                issues.Add(new(repository.RepositoryRoot,
                    $"Repository resolved target escapes the projects folder: {repository.RepositoryRoot} -> {resolvedRepositoryRoot}"));
            for (var index = 0; index < repository.ValidationTargets.Length; index++)
            {
                var target = repository.ValidationTargets[index];
                var planned = index < repository.ResolvedValidationTargets.Length
                    ? repository.ResolvedValidationTargets[index]
                    : target;
                var resolvedTarget = ResolveStable(repository, target, planned, "Validation target", issues);
                if (resolvedTarget is not null && resolvedProjectsRoot is not null && resolvedRepositoryRoot is not null &&
                    (!_containment.IsWithin(resolvedProjectsRoot, resolvedTarget) ||
                     !_containment.IsWithin(resolvedRepositoryRoot, resolvedTarget)))
                    issues.Add(new(repository.RepositoryRoot,
                        $"Validation target escapes the projects folder or repository: {target} -> {resolvedTarget}"));
            }

            if (issues.Count == 0)
            {
                var executionRoot = resolvedRepositoryRoot!;
                await CheckTool("git", ["--version"], executionRoot, "Git is unavailable.", issues, cancellationToken);
                await CheckTool("dotnet", ["--version"], executionRoot, ".NET SDK is unavailable.", issues, cancellationToken);
                var workTree = await runner.RunAsync(new("git", ["rev-parse", "--is-inside-work-tree"], executionRoot), cancellationToken);
                if (!workTree.Succeeded || !workTree.StandardOutput.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
                    issues.Add(new(repository.RepositoryRoot, "Path is not a Git working tree."));

                if (plan.Git.BaseBranch is { } baseBranch)
                {
                    await CheckBranchName(executionRoot, baseBranch, issues, cancellationToken);
                    var remoteBase = await RemoteBranchExists(executionRoot, plan.Git.RemoteName, baseBranch, cancellationToken);
                    if (!remoteBase.Succeeded)
                        issues.Add(new(repository.RepositoryRoot, $"Could not inspect remote {plan.Git.RemoteName}."));
                    var baseExists = await RefExists(executionRoot, $"refs/heads/{baseBranch}", cancellationToken) ||
                        await RefExists(executionRoot, $"refs/remotes/{plan.Git.RemoteName}/{baseBranch}", cancellationToken) || remoteBase.Exists;
                    if (!baseExists)
                        issues.Add(new(repository.RepositoryRoot, $"Base branch {baseBranch} does not exist locally or on {plan.Git.RemoteName}."));
                }

                if (plan.Git.TargetBranch is { } targetBranch)
                {
                    await CheckBranchName(executionRoot, targetBranch, issues, cancellationToken);
                    var targetExists = await RefExists(executionRoot, $"refs/heads/{targetBranch}", cancellationToken) ||
                        await RefExists(executionRoot, $"refs/remotes/{plan.Git.RemoteName}/{targetBranch}", cancellationToken);
                    if (plan.Git.BaseBranch is not null || plan.Git.CommitAndPush)
                    {
                        var remoteTarget = await RemoteBranchExists(executionRoot, plan.Git.RemoteName, targetBranch, cancellationToken);
                        if (!remoteTarget.Succeeded)
                            issues.Add(new(repository.RepositoryRoot, $"Could not inspect remote {plan.Git.RemoteName}."));
                        targetExists |= remoteTarget.Exists;
                    }
                    if (targetExists)
                        issues.Add(new(repository.RepositoryRoot, $"Target branch {targetBranch} already exists."));
                }

                if (plan.Git.UpdatesCurrentBranch)
                {
                    var current = await runner.RunAsync(
                        new("git", ["branch", "--show-current"], executionRoot), cancellationToken);
                    if (!current.Succeeded || string.IsNullOrWhiteSpace(current.StandardOutput))
                        issues.Add(new(repository.RepositoryRoot, "Cannot update the current branch while HEAD is detached."));
                }
                if (plan.Git.CommitAndPush && plan.Git.BaseBranch is null && plan.Git.TargetBranch is null)
                {
                    await CheckRemote(executionRoot, plan.Git.RemoteName, issues, cancellationToken);
                }

                var validation = editor.Validate(repository.Edits);
                if (!validation.IsValid) issues.Add(new(repository.RepositoryRoot, validation.Error!));
            }
            output.Add(new(repository.RepositoryRoot, issues.Count == 0, issues.ToImmutable()));
        }
        return output.ToImmutable();
    }

    private string? ResolveStable(
        RepositoryPlan repository,
        string displayPath,
        string plannedResolvedPath,
        string kind,
        ImmutableArray<PreflightIssue>.Builder issues)
    {
        var current = _containment.ResolveExisting(displayPath);
        if (!current.Succeeded)
        {
            issues.Add(new(repository.RepositoryRoot,
                $"{kind} real path could not be resolved: {displayPath}. {current.Error}"));
            return null;
        }
        if (!_containment.PathsEqual(current.ResolvedPath!, plannedResolvedPath))
        {
            issues.Add(new(repository.RepositoryRoot,
                $"{kind} target changed after planning: {displayPath} ({plannedResolvedPath} -> {current.ResolvedPath})."));
            return null;
        }
        return current.ResolvedPath;
    }

    private async Task<bool> RefExists(string root, string reference, CancellationToken token)
    {
        var result = await runner.RunAsync(new("git", ["show-ref", "--verify", "--quiet", reference], root), token);
        return result.ExitCode == 0;
    }

    private async Task<(bool Succeeded, bool Exists)> RemoteBranchExists(string root, string remote, string branch, CancellationToken token)
    {
        var result = await runner.RunAsync(new("git", ["ls-remote", "--exit-code", "--heads", remote, branch], root), token);
        return result.ExitCode switch { 0 => (true, true), 2 => (true, false), _ => (false, false) };
    }

    private async Task CheckBranchName(
        string root,
        string branch,
        ImmutableArray<PreflightIssue>.Builder issues,
        CancellationToken token)
    {
        var result = await runner.RunAsync(new("git", ["check-ref-format", "--branch", branch], root), token);
        if (!result.Succeeded) issues.Add(new(root, $"{branch} is not a valid Git branch name."));
    }

    private async Task CheckRemote(
        string root,
        string remote,
        ImmutableArray<PreflightIssue>.Builder issues,
        CancellationToken token)
    {
        var result = await runner.RunAsync(new("git", ["ls-remote", "--heads", remote], root), token);
        if (!result.Succeeded) issues.Add(new(root, $"Could not inspect remote {remote}."));
    }

    private async Task CheckTool(string tool, string[] args, string root, string error, ImmutableArray<PreflightIssue>.Builder issues, CancellationToken token)
    {
        var result = await runner.RunAsync(new(tool, args, root), token);
        if (!result.Succeeded) issues.Add(new(root, error));
    }
}
