namespace DotnetUpdater.Execution;

public sealed class GitService(IProcessRunner runner)
{
    public Task<ProcessResult> InspectAsync(string root, params string[] arguments) =>
        runner.RunAsync(new("git", arguments, root), CancellationToken.None);

    public Task<ProcessResult> RunAsync(string root, CancellationToken cancellationToken, params string[] arguments) =>
        runner.RunAsync(new("git", arguments, root), cancellationToken);

    public async Task<(bool Succeeded, bool IsDirty)> IsDirtyAsync(string root, CancellationToken cancellationToken)
    {
        var result = await RunAsync(root, cancellationToken, "status", "--porcelain=v1", "--untracked-files=all");
        return (result.Succeeded, !string.IsNullOrWhiteSpace(result.StandardOutput));
    }

    public async Task<(bool Succeeded, string? Reference)> StashAsync(string root, string message, CancellationToken cancellationToken)
    {
        var result = await RunAsync(root, cancellationToken, "stash", "push", "--include-untracked", "--message", message);
        if (!result.Succeeded) return (false, null);
        var list = await RunAsync(root, cancellationToken, "stash", "list", "--format=%gd%x09%s");
        var match = list.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(x => x.Contains(message, StringComparison.Ordinal));
        return (list.Succeeded, match?.Split('\t')[0]);
    }

    public Task<ProcessResult> SwitchAsync(string root, string branch, CancellationToken token) => RunAsync(root, token, "switch", branch);
    public Task<ProcessResult> FetchAsync(string root, string remote, CancellationToken token) => RunAsync(root, token, "fetch", remote);
    public Task<ProcessResult> PullAsync(string root, string remote, string branch, CancellationToken token) => RunAsync(root, token, "pull", "--ff-only", remote, branch);
    public Task<ProcessResult> CreateBranchAsync(string root, string branch, CancellationToken token) => RunAsync(root, token, "switch", "--create", branch);
    public Task<ProcessResult> CurrentBranchAsync(string root, CancellationToken token) => RunAsync(root, token, "branch", "--show-current");
    public Task<ProcessResult> StageAsync(string root, IEnumerable<string> paths, CancellationToken token) =>
        RunAsync(root, token, ["add", "--", .. paths.Select(path => Path.GetRelativePath(root, path))]);
    public Task<ProcessResult> HasStagedChangesAsync(string root, CancellationToken token) => RunAsync(root, token, "diff", "--cached", "--quiet");
    public Task<ProcessResult> CommitAsync(string root, string message, CancellationToken token) => RunAsync(root, token, "commit", "--message", message);
    public Task<ProcessResult> PushAsync(string root, string remote, string branch, CancellationToken token) => RunAsync(root, token, "push", "--set-upstream", remote, branch);
    public Task<ProcessResult> HeadAsync(string root, CancellationToken token) => RunAsync(root, token, "rev-parse", "HEAD");
}
