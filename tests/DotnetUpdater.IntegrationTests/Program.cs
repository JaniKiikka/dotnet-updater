using DotnetUpdater.Execution;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DotnetUpdater.IntegrationTests;

[TestClass]
public sealed class GitServiceTests
{
    [TestMethod]
    public async Task LocalGitWorkflowPreservesUnrelatedChangesAndPushesPlannedChanges()
    {
        var temp = Path.Combine(Path.GetTempPath(), "dotnet-updater-integration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        try
        {
            var logger = new FileRunLogger(Path.Combine(temp, "logs"));
            var runner = new ProcessRunner(logger);
            var remote = Path.Combine(temp, "remote.git");
            var repo = Path.Combine(temp, "repo");
            await MustRunAsync(runner, temp, "git", "init", "--bare", remote);
            await MustRunAsync(runner, temp, "git", "clone", remote, repo);
            await MustRunAsync(runner, repo, "git", "config", "user.email", "tests@example.invalid");
            await MustRunAsync(runner, repo, "git", "config", "user.name", "Updater Tests");
            await File.WriteAllTextAsync(Path.Combine(repo, "planned.props"), "one\n");
            await File.WriteAllTextAsync(Path.Combine(repo, "unrelated.txt"), "keep\n");
            await MustRunAsync(runner, repo, "git", "add", ".");
            await MustRunAsync(runner, repo, "git", "commit", "-m", "initial");
            await MustRunAsync(runner, repo, "git", "branch", "-M", "development");
            await MustRunAsync(runner, repo, "git", "push", "-u", "origin", "development");

            var git = new GitService(runner);
            await File.AppendAllTextAsync(Path.Combine(repo, "unrelated.txt"), "dirty\n");
            await File.WriteAllTextAsync(Path.Combine(repo, "untracked.txt"), "untracked\n");

            var dirty = await git.IsDirtyAsync(repo, default);
            Assert.IsTrue(dirty.Succeeded && dirty.IsDirty, "Dirty state was not detected.");

            var stash = await git.StashAsync(repo, "dotnet-updater integration", default);
            Assert.IsTrue(stash.Succeeded, "Tracked and untracked work was not stashed.");
            Assert.IsNotNull(stash.Reference, "The stash reference was not resolved.");
            Assert.IsTrue((await git.SwitchAsync(repo, "development", default)).Succeeded, "Could not switch development.");
            Assert.IsTrue((await git.FetchAsync(repo, "origin", default)).Succeeded, "Could not fetch.");
            Assert.IsTrue((await git.PullAsync(repo, "origin", "development", default)).Succeeded, "Could not fast-forward pull.");
            Assert.IsTrue((await git.CreateBranchAsync(repo, "updates/dependencies", default)).Succeeded, "Could not create update branch.");

            await File.AppendAllTextAsync(Path.Combine(repo, "planned.props"), "two\n");
            await File.AppendAllTextAsync(Path.Combine(repo, "unrelated.txt"), "must-not-stage\n");
            Assert.IsTrue(
                (await git.StageAsync(repo, [Path.Combine(repo, "planned.props")], default)).Succeeded,
                "Selective staging failed.");

            var stagedNames = await runner.RunAsync(
                new("git", ["diff", "--cached", "--name-only"], repo),
                default);
            Assert.AreEqual("planned.props", stagedNames.StandardOutput.Trim(), "An unrelated file was staged.");
            Assert.IsTrue(
                (await git.CommitAsync(repo, "updates/dependencies .NET nuget package update", default)).Succeeded,
                "Commit failed.");

            var head = await git.HeadAsync(repo, default);
            Assert.IsTrue(head.Succeeded, "Commit ID could not be queried.");
            Assert.IsGreaterThanOrEqualTo(40, head.StandardOutput.Trim().Length, "Commit ID was not resolved.");
            Assert.IsTrue(
                (await git.PushAsync(repo, "origin", "updates/dependencies", default)).Succeeded,
                "Push failed.");

            var remoteBranch = await runner.RunAsync(
                new("git", ["show-ref", "--verify", "refs/remotes/origin/updates/dependencies"], repo),
                default);
            Assert.IsTrue(remoteBranch.Succeeded, "Remote update branch was not created.");
        }
        finally
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Directory.Delete(temp, true);
                    break;
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    if (attempt < 2) await Task.Delay(500);
                }
            }
        }
    }

    private static async Task<ProcessResult> MustRunAsync(
        IProcessRunner runner,
        string workingDirectory,
        string file,
        params string[] arguments)
    {
        var result = await runner.RunAsync(new(file, arguments, workingDirectory), default);
        Assert.IsTrue(
            result.Succeeded,
            $"{file} {string.Join(' ', arguments)} failed: {result.StandardError}");
        return result;
    }
}
