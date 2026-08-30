using System.Collections.Immutable;
using DotnetUpdater.Domain;

namespace DotnetUpdater.Execution;

public sealed class PreflightService(IProcessRunner runner, PackageEditor editor)
{
    public async Task<ImmutableArray<RepositoryPreflight>> InspectAsync(UpgradePlan plan, CancellationToken cancellationToken)
    {
        var output = ImmutableArray.CreateBuilder<RepositoryPreflight>();
        foreach (var repository in plan.Repositories)
        {
            var issues = ImmutableArray.CreateBuilder<PreflightIssue>();
            if (!Directory.Exists(repository.RepositoryRoot)) issues.Add(new(repository.RepositoryRoot, "Repository no longer exists."));
            foreach (var target in repository.ValidationTargets.Where(path => !File.Exists(path)))
                issues.Add(new(repository.RepositoryRoot, $"Validation target no longer exists: {target}"));
            foreach (var edit in repository.Edits.Where(edit => !File.Exists(edit.DeclarationPath)))
                issues.Add(new(repository.RepositoryRoot, $"Declaration file no longer exists: {edit.DeclarationPath}"));

            if (issues.Count == 0)
            {
                await CheckTool("git", ["--version"], repository.RepositoryRoot, "Git is unavailable.", issues, cancellationToken);
                await CheckTool("dotnet", ["--version"], repository.RepositoryRoot, ".NET SDK is unavailable.", issues, cancellationToken);
                var workTree = await runner.RunAsync(new("git", ["rev-parse", "--is-inside-work-tree"], repository.RepositoryRoot), cancellationToken);
                if (!workTree.Succeeded || !workTree.StandardOutput.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
                    issues.Add(new(repository.RepositoryRoot, "Path is not a Git working tree."));

                if (plan.Git.BaseBranch is { } baseBranch)
                {
                    await CheckBranchName(repository.RepositoryRoot, baseBranch, issues, cancellationToken);
                    var remoteBase = await RemoteBranchExists(repository.RepositoryRoot, plan.Git.RemoteName, baseBranch, cancellationToken);
                    if (!remoteBase.Succeeded)
                        issues.Add(new(repository.RepositoryRoot, $"Could not inspect remote {plan.Git.RemoteName}."));
                    var baseExists = await RefExists(repository.RepositoryRoot, $"refs/heads/{baseBranch}", cancellationToken) ||
                        await RefExists(repository.RepositoryRoot, $"refs/remotes/{plan.Git.RemoteName}/{baseBranch}", cancellationToken) || remoteBase.Exists;
                    if (!baseExists)
                        issues.Add(new(repository.RepositoryRoot, $"Base branch {baseBranch} does not exist locally or on {plan.Git.RemoteName}."));
                }

                if (plan.Git.TargetBranch is { } targetBranch)
                {
                    await CheckBranchName(repository.RepositoryRoot, targetBranch, issues, cancellationToken);
                    var targetExists = await RefExists(repository.RepositoryRoot, $"refs/heads/{targetBranch}", cancellationToken) ||
                        await RefExists(repository.RepositoryRoot, $"refs/remotes/{plan.Git.RemoteName}/{targetBranch}", cancellationToken);
                    if (plan.Git.BaseBranch is not null || plan.Git.CommitAndPush)
                    {
                        var remoteTarget = await RemoteBranchExists(repository.RepositoryRoot, plan.Git.RemoteName, targetBranch, cancellationToken);
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
                        new("git", ["branch", "--show-current"], repository.RepositoryRoot), cancellationToken);
                    if (!current.Succeeded || string.IsNullOrWhiteSpace(current.StandardOutput))
                        issues.Add(new(repository.RepositoryRoot, "Cannot update the current branch while HEAD is detached."));
                }
                if (plan.Git.CommitAndPush && plan.Git.BaseBranch is null && plan.Git.TargetBranch is null)
                {
                    await CheckRemote(repository.RepositoryRoot, plan.Git.RemoteName, issues, cancellationToken);
                }

                var validation = editor.Validate(repository.Edits);
                if (!validation.IsValid) issues.Add(new(repository.RepositoryRoot, validation.Error!));
            }
            output.Add(new(repository.RepositoryRoot, issues.Count == 0, issues.ToImmutable()));
        }
        return output.ToImmutable();
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
