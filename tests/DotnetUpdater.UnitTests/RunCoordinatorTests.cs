namespace DotnetUpdater.UnitTests;

[TestClass]
public sealed class RunCoordinatorTests
{
    [TestMethod]
    public async Task ExecutePreservesCommandOrdering()
    {
        using var temp = new TempDirectory();
        var declarationPath = Path.Combine(temp.Path, "Directory.Packages.props");
        var target = Path.Combine(temp.Path, "Repo.slnx");
        File.WriteAllText(declarationPath, "<Project><ItemGroup><PackageVersion Include=\"Thing\" Version=\"1.0.0\" /></ItemGroup></Project>");
        File.WriteAllText(target, "<Solution />");
        var edit = new DeclarationEdit(
            temp.Path,
            declarationPath,
            "Thing",
            "1.0.0",
            "2.0.0",
            DeclarationKind.CentralPackageVersion,
            "PackageVersion:Thing");
        var repository = new RepositoryPlan(temp.Path, [target], [edit]);
        var plan = new UpgradePlan(
            new("origin", "development", "versions/TEST-1", true),
            [repository],
            DateTimeOffset.UnixEpoch);
        var fake = new RecordingRunner(request =>
        {
            var command = $"{request.FileName} {string.Join(' ', request.Arguments)}";
            if (command == "git switch --create versions/TEST-1")
                StringAssert.Contains(File.ReadAllText(declarationPath), "1.0.0");
            if (command == "git diff --cached --quiet")
                return new(1, string.Empty, string.Empty);
            if (command == "git rev-parse HEAD")
                return new(0, new string('a', 40) + "\n", string.Empty);
            return new(0, string.Empty, string.Empty);
        });
        var logger = new FileRunLogger(Path.Combine(temp.Path, "logs"));
        var coordinator = new RunCoordinator(fake, new GitService(fake), new PackageEditor(), logger);
        var ready = new Dictionary<string, RepositoryPreflight>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
        {
            [temp.Path] = new(temp.Path, true, [])
        };

        var results = await coordinator.ExecuteAsync(plan, ready, null, default);

        Assert.AreEqual(RunStage.Passed, results.Single().Status);
        CollectionAssert.AreEqual(new[]
        {
            "git status --porcelain=v1 --untracked-files=all",
            "git switch development",
            "git fetch origin",
            "git pull --ff-only origin development",
            "git switch --create versions/TEST-1",
            $"dotnet restore {target}",
            $"dotnet build {target} --no-restore",
            $"dotnet test {target} --no-build --no-restore",
            "git add -- Directory.Packages.props",
            "git diff --cached --quiet",
            "git commit --message versions/TEST-1 .NET nuget package update",
            "git rev-parse HEAD",
            "git push --set-upstream origin versions/TEST-1"
        }, fake.Requests.Select(request => $"{request.FileName} {string.Join(' ', request.Arguments)}").ToArray());
    }

    [TestMethod]
    public async Task ExecuteCanUpdateCurrentBranchWithoutSwitchCommitOrPush()
    {
        using var temp = new TempDirectory();
        var declarationPath = Path.Combine(temp.Path, "Directory.Packages.props");
        var target = Path.Combine(temp.Path, "Repo.slnx");
        File.WriteAllText(declarationPath, "<Project><ItemGroup><PackageVersion Include=\"Thing\" Version=\"1.0.0\" /></ItemGroup></Project>");
        File.WriteAllText(target, "<Solution />");
        var repository = new RepositoryPlan(temp.Path, [target],
            [new(temp.Path, declarationPath, "Thing", "1.0.0", "2.0.0", DeclarationKind.CentralPackageVersion, "PackageVersion:Thing")]);
        var plan = new UpgradePlan(new("origin", null, null, false), [repository], DateTimeOffset.UnixEpoch);
        var fake = new RecordingRunner(request => request.Arguments.SequenceEqual(["branch", "--show-current"])
            ? new(0, "work-in-progress\n", string.Empty)
            : new(0, string.Empty, string.Empty));
        var logger = new FileRunLogger(Path.Combine(temp.Path, "logs"));
        var coordinator = new RunCoordinator(fake, new GitService(fake), new PackageEditor(), logger);
        var ready = new Dictionary<string, RepositoryPreflight>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
        {
            [temp.Path] = new(temp.Path, true, [])
        };

        var result = (await coordinator.ExecuteAsync(plan, ready, null, default)).Single();

        Assert.AreEqual(RunStage.Passed, result.Status);
        Assert.AreEqual("work-in-progress", result.BranchName);
        Assert.IsNull(result.CommitId);
        Assert.IsNull(result.RemoteBranch);
        CollectionAssert.AreEqual(new[]
        {
            "git branch --show-current",
            "git status --porcelain=v1 --untracked-files=all",
            $"dotnet restore {target}",
            $"dotnet build {target} --no-restore",
            $"dotnet test {target} --no-build --no-restore"
        }, fake.Requests.Select(request => $"{request.FileName} {string.Join(' ', request.Arguments)}").ToArray());
    }

    [TestMethod]
    public async Task ValidatedIncrementalBuildsBaselineThenUsesMinorFallback()
    {
        using var temp = new TempDirectory();
        var project = Path.Combine(temp.Path, "App.csproj");
        File.WriteAllText(project, """
            <Project><ItemGroup>
              <PackageReference Include="Microsoft.Extensions.Example" Version="1.0.0" />
              <PackageReference Include="Third.Party" Version="1.0.0" />
            </ItemGroup></Project>
            """);
        var microsoftEdit = Edit(temp.Path, project, "Microsoft.Extensions.Example", "1.0.0", "2.0.0");
        var thirdMajor = Edit(temp.Path, project, "Third.Party", "1.0.0", "2.0.0");
        var thirdMinor = Edit(temp.Path, project, "Third.Party", "1.0.0", "1.5.0");
        var repository = new RepositoryPlan(temp.Path, [project], [microsoftEdit, thirdMajor])
        {
            ValidatedUpdates =
            [
                new("Microsoft.Extensions.Example", true, false, [microsoftEdit], []),
                new("Third.Party", false, false, [thirdMajor], [thirdMinor])
            ]
        };
        var plan = new UpgradePlan(new("origin", null, null, false), [repository], DateTimeOffset.UnixEpoch)
        {
            Strategy = UpgradeStrategy.ValidatedIncremental
        };
        var fake = new RecordingRunner(request =>
        {
            if (request.Arguments.SequenceEqual(["branch", "--show-current"]))
                return new(0, "work-in-progress\n", string.Empty);
            if (request.FileName == "dotnet" && request.Arguments.FirstOrDefault() == "build" &&
                File.ReadAllText(project).Contains("Third.Party\" Version=\"2.0.0", StringComparison.Ordinal))
                return new(1, string.Empty, "major build failed");
            return new(0, string.Empty, string.Empty);
        });
        var logger = new FileRunLogger(Path.Combine(temp.Path, "logs"));
        var coordinator = new RunCoordinator(fake, new GitService(fake), new PackageEditor(), logger);
        var ready = Ready(temp.Path);

        var result = (await coordinator.ExecuteAsync(plan, ready, null, default)).Single();

        Assert.AreEqual(RunStage.Passed, result.Status);
        Assert.AreEqual(PackageUpdateStatus.UpdatedWithFallback,
            result.PackageResults.Single(x => x.PackageId == "Third.Party").Status);
        var final = File.ReadAllText(project);
        StringAssert.Contains(final, "Microsoft.Extensions.Example\" Version=\"2.0.0");
        StringAssert.Contains(final, "Third.Party\" Version=\"1.5.0");
        Assert.HasCount(4, fake.Requests.Where(x => x.FileName == "dotnet" && x.Arguments.First() == "restore"));
        Assert.HasCount(3, fake.Requests.Where(x => x.FileName == "dotnet" && x.Arguments.First() == "test"));
    }

    [TestMethod]
    public async Task ValidatedIncrementalRestoresRejectedPackageAndContinues()
    {
        using var temp = new TempDirectory();
        var project = Path.Combine(temp.Path, "App.csproj");
        File.WriteAllText(project, """
            <Project><ItemGroup>
              <PackageReference Include="Broken.Package" Version="1.0.0" />
              <PackageReference Include="Working.Package" Version="1.0.0" />
            </ItemGroup></Project>
            """);
        var brokenMajor = Edit(temp.Path, project, "Broken.Package", "1.0.0", "2.0.0");
        var brokenMinor = Edit(temp.Path, project, "Broken.Package", "1.0.0", "1.5.0");
        var working = Edit(temp.Path, project, "Working.Package", "1.0.0", "2.0.0");
        var repository = new RepositoryPlan(temp.Path, [project], [brokenMajor, working])
        {
            ValidatedUpdates =
            [
                new("Broken.Package", false, false, [brokenMajor], [brokenMinor]),
                new("Working.Package", false, false, [working], [])
            ]
        };
        var plan = new UpgradePlan(new("origin", null, null, false), [repository], DateTimeOffset.UnixEpoch)
        {
            Strategy = UpgradeStrategy.ValidatedIncremental
        };
        var fake = new RecordingRunner(request =>
        {
            if (request.Arguments.SequenceEqual(["branch", "--show-current"]))
                return new(0, "work-in-progress\n", string.Empty);
            if (request.FileName == "dotnet" && request.Arguments.FirstOrDefault() == "test" &&
                !File.ReadAllText(project).Contains("Broken.Package\" Version=\"1.0.0", StringComparison.Ordinal))
                return new(1, string.Empty, "tests failed");
            return new(0, string.Empty, string.Empty);
        });
        var coordinator = new RunCoordinator(fake, new GitService(fake), new PackageEditor(),
            new FileRunLogger(Path.Combine(temp.Path, "logs")));
        var progress = new RecordingProgress<ProgressEvent>();

        var result = (await coordinator.ExecuteAsync(plan, Ready(temp.Path), progress, default)).Single();

        Assert.AreEqual(RunStage.Passed, result.Status);
        Assert.AreEqual(PackageUpdateStatus.Failed,
            result.PackageResults.Single(x => x.PackageId == "Broken.Package").Status);
        var final = File.ReadAllText(project);
        StringAssert.Contains(final, "Broken.Package\" Version=\"1.0.0");
        StringAssert.Contains(final, "Working.Package\" Version=\"2.0.0");
        Assert.IsFalse(result.ChangedPackages.Any(x => x.PackageId == "Broken.Package"));
        Assert.IsTrue(progress.Messages.Any(x =>
            x.PackageId == "Broken.Package" && x.PackageStatus == PackageUpdateStatus.Failed));
    }

    private static DeclarationEdit Edit(string root, string project, string packageId, string oldVersion, string targetVersion) =>
        new(root, project, packageId, oldVersion, targetVersion, DeclarationKind.PackageReferenceAttribute,
            $"PackageReference:{packageId}:attribute");

    private static Dictionary<string, RepositoryPreflight> Ready(string root) =>
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
        {
            [root] = new(root, true, [])
        };
}
