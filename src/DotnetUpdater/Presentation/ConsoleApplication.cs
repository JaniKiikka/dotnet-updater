using System.Collections.Immutable;
using DotnetUpdater.Configuration;
using DotnetUpdater.Discovery;
using DotnetUpdater.Domain;
using DotnetUpdater.Execution;
using DotnetUpdater.Packages;
using DotnetUpdater.Planning;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Configuration;
using SharpConsoleUI.Core;
using SharpConsoleUI.Dialogs;
using SharpConsoleUI.Drivers;
using SharpConsoleUI.Flows;
using SharpConsoleUI.Layout;
using DialogsApi = SharpConsoleUI.Dialogs.Dialogs;

namespace DotnetUpdater.Presentation;

public sealed class ConsoleApplication(
    IConfigurationStore configurationStore,
    DiscoveryService discovery,
    PackageInventoryService inventory,
    NuGetVersionService versions,
    UpgradePlanner planner,
    PreflightService preflight,
    RunCoordinator coordinator,
    IRunLogger logger)
{
    public static ConsoleApplication CreateDefault()
    {
        var logger = new FileRunLogger();
        var runner = new ProcessRunner(logger);
        var editor = new PackageEditor();
        return new(
            new JsonConfigurationStore(new UserConfigurationPathProvider()),
            new DiscoveryService(),
            new PackageInventoryService(),
            new NuGetVersionService(runner),
            new UpgradePlanner(),
            new PreflightService(runner, editor),
            new RunCoordinator(runner, new GitService(runner), editor, logger),
            logger);
    }

    public Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Any(x => x is "-h" or "--help"))
        {
            ShowHelp();
            return Task.FromResult(0);
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var options = new ConsoleWindowSystemOptions(InstallSynchronizationContext: false);
        var windowSystem = new ConsoleWindowSystem(new NetConsoleDriver(RenderMode.Buffer), options: options);
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += handler;

        new WindowBuilder(windowSystem)
            .WithTitle(".NET Multi-Project Updater")
            .Maximized()
            .Resizable(false)
            .Movable(false)
            .Closable(false)
            .Minimizable(false)
            .Maximizable(false)
            .AddControl(Controls.Markup()
                .AddLine("[bold cyan].NET Multi-Project Updater[/]")
                .AddLine("")
                .AddLine("[dim]Guided NuGet upgrades across Git repositories[/]")
                .AddLine("")
                .AddLine("Use [bold]Tab[/] to move between controls, [bold]↑/↓[/] to navigate lists, and [bold]Enter[/] to activate.")
                .AddLine("Press [bold]Esc[/] in a dialog to cancel the run. Ctrl+C requests cancellation between operations.")
                .Centered()
                .WithVerticalAlignment(VerticalAlignment.Center)
                .Build())
            .WithAsyncWindowThread(async (window, windowToken) =>
            {
                using var workflowCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellation.Token,
                    windowToken);
                var exitCode = await RunFlowAsync(windowSystem, window, workflowCancellation.Token).ConfigureAwait(false);
                windowSystem.Shutdown(exitCode);
            })
            .BuildAndShow();

        try
        {
            return Task.FromResult(windowSystem.Run());
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    private async Task<int> RunFlowAsync(ConsoleWindowSystem windowSystem, Window parent, CancellationToken cancellationToken)
    {
        var result = await Flow.Run<int>(
            windowSystem,
            parent,
            context => RunWorkflowAsync(windowSystem, parent, context, cancellationToken),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.Completed) return result.Value;
        if (result.Cancelled) return 130;

        var message = result.Error?.Message ?? "The UI workflow failed unexpectedly.";
        logger.Write($"UI workflow failed: {result.Error}");
        await DialogsApi.MessageAsync(
            windowSystem,
            "Unexpected error",
            $"The run could not continue.\n\n{PresentationText.Escape(message)}\n\nLog: {PresentationText.Escape(logger.Path)}",
            severity: NotificationSeverityEnum.Danger,
            parent: parent).ConfigureAwait(false);
        return 1;
    }

    private async Task<int> RunWorkflowAsync(
        ConsoleWindowSystem windowSystem,
        Window parent,
        FlowContext context,
        CancellationToken cancellationToken)
    {
        var loaded = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (loaded.Warning is not null)
            await ShowMessageAsync(windowSystem, parent, "Configuration warning",
                $"[yellow]⚠ {PresentationText.Escape(loaded.Warning)}[/]", cancellationToken).ConfigureAwait(false);

        var configuration = await ConfigureAsync(context, loaded.Configuration, cancellationToken).ConfigureAwait(false);

        var found = await context.RunWithProgress(
            "Discovering projects",
            $"Scanning {configuration.ProjectsFolder}",
            (token, progress) => Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                progress.Report("Finding Git repositories, solutions, and standalone projects…");
                return discovery.Scan(configuration.ProjectsFolder);
            }, token)).ConfigureAwait(false);
        if (found is null) throw new OperationCanceledException();
        cancellationToken.ThrowIfCancellationRequested();

        if (found.Warnings.Length > 0)
        {
            var warnings = found.Warnings.Take(10)
                .Select(x => $"• {PresentationText.Escape(x.Path)}: {PresentationText.Escape(x.Message)}")
                .ToList();
            if (found.Warnings.Length > 10)
                warnings.Add($"• {found.Warnings.Length - 10} additional warning(s) omitted");
            await ShowMessageAsync(windowSystem, parent, "Scan warnings",
                "[yellow]Some paths could not be included.[/]\n\n" + string.Join('\n', warnings), cancellationToken)
                .ConfigureAwait(false);
        }

        if (found.Entries.Length == 0)
        {
            await ShowMessageAsync(windowSystem, parent, "Nothing found",
                "No selectable solutions or standalone projects were found inside Git repositories.", cancellationToken)
                .ConfigureAwait(false);
            return 2;
        }

        while (true)
        {
            var action = await PresentAsync(
                windowSystem,
                parent,
                "Choose an action",
                new ChoiceListContent<ApplicationActionOption>(
                    "Start an upgrade run or edit persistent package update rules.",
                    [
                        ("Upgrade packages", "select projects and plan package updates", new(ApplicationAction.UpgradePackages, "Upgrade packages", "")),
                        ("Package rules", "mark packages as ignored or force exact versions", new(ApplicationAction.ManagePackageRules, "Package rules", ""))
                    ]),
                cancellationToken,
                width: 84,
                height: 18).ConfigureAwait(false);
            if (action is null) throw new OperationCanceledException();
            if (action.Action == ApplicationAction.UpgradePackages) break;

            configuration = await ManagePackageRulesAsync(
                windowSystem,
                parent,
                context,
                found.Entries,
                configuration,
                cancellationToken).ConfigureAwait(false);
        }

        var entrySelection = await PresentAsync(
            windowSystem,
            parent,
            "Select solutions and projects",
            new EntrySelectionContent(found.Entries),
            cancellationToken).ConfigureAwait(false);
        if (entrySelection is null) throw new OperationCanceledException();
        var selected = entrySelection.Entries;

        var ignored = configuration.IgnoredPackages.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var forcedVersions = configuration.ForcedPackageVersions.ToDictionary(
            x => x.PackageId,
            x => x.Version,
            StringComparer.OrdinalIgnoreCase);
        var inventoryResult = await context.RunWithProgress(
            "Reading package inventory",
            "Combining package declarations across the selection…",
            (token, _) => Task.Run(() => inventory.Read(selected, ignored), token)).ConfigureAwait(false);
        if (inventoryResult is null) throw new OperationCanceledException();

        var inventoryMessages = inventoryResult.Warnings
            .Select(x => $"• {PresentationText.Escape(x)}")
            .Concat(inventoryResult.Occurrences
                .Where(x => x.UnsupportedReason is not null)
                .Select(x => $"• {PresentationText.Escape(x.PackageId)} in " +
                    $"{PresentationText.Escape(Path.GetFileName(x.ProjectPath))}: {PresentationText.Escape(x.UnsupportedReason!)}"))
            .ToArray();
        if (inventoryMessages.Length > 0)
            await ShowMessageAsync(windowSystem, parent, "Unsupported or unavailable declarations",
                string.Join('\n', inventoryMessages), cancellationToken).ConfigureAwait(false);

        var eligible = inventoryResult.Occurrences.Where(x => x.UnsupportedReason is null).ToArray();
        if (eligible.Length == 0)
        {
            await ShowMessageAsync(windowSystem, parent, "No eligible packages",
                "No supported, non-ignored package declarations were found.", cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var grouped = eligible.GroupBy(x => x.PackageId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var floatingGroups = grouped.Where(x => !forcedVersions.ContainsKey(x.Key)).ToArray();
        var floatingResolved = floatingGroups.Length == 0
            ? ImmutableArray<PackageGroup>.Empty
            : await context.RunWithProgress(
                "Resolving package targets",
                $"Resolving 0 of {floatingGroups.Length} packages…",
                (token, progress) => versions.ResolveAllAsync(floatingGroups, progress, token)).ConfigureAwait(false);
        if (floatingResolved.IsDefault) throw new OperationCanceledException();
        var floatingById = floatingResolved.ToDictionary(x => x.PackageId, StringComparer.OrdinalIgnoreCase);
        var resolved = grouped.Select(group => forcedVersions.ContainsKey(group.Key)
                ? new PackageGroup(group.Key, group.ToImmutableArray(), null, null, null)
                : floatingById[group.Key])
            .ToImmutableArray();

        var unavailable = resolved.Where(x => x.ResolutionError is not null).ToArray();
        if (unavailable.Length > 0)
            await ShowMessageAsync(windowSystem, parent, "Package resolution warnings",
                string.Join('\n', unavailable.Select(x =>
                    $"• {PresentationText.Escape(x.PackageId)}: {PresentationText.Escape(x.ResolutionError!)}")), cancellationToken)
                .ConfigureAwait(false);

        var mode = await PresentAsync(
            windowSystem,
            parent,
            "Upgrade mode",
            new ChoiceListContent<UpgradeModeOption>(
                "Choose how targets should be selected for every eligible package.",
                [
                    ("Latest minor", "stay within the highest major already selected", new(UpgradeMode.LatestMinor, "Latest minor", "")),
                    ("Latest major", "allow breaking changes; the review marks every major jump", new(UpgradeMode.LatestMajor, "Latest major", "")),
                    ("Validated incremental", "baseline-check, update Microsoft packages together, then validate third-party packages one by one", new(UpgradeMode.ValidatedIncremental, "Validated incremental", "")),
                    ("Select packages", "cycle an action independently for each package", new(UpgradeMode.SelectPackages, "Select packages", ""))
                ]),
            cancellationToken,
            width: 84,
            height: 18).ConfigureAwait(false);
        if (mode is null) throw new OperationCanceledException();

        var decisions = new Dictionary<string, PackageDecision>(StringComparer.OrdinalIgnoreCase);
        if (mode.Mode is UpgradeMode.LatestMinor or UpgradeMode.LatestMajor)
        {
            var choice = mode.Mode == UpgradeMode.LatestMinor ? UpgradeChoice.LatestMinor : UpgradeChoice.LatestMajor;
            foreach (var group in resolved) decisions[group.PackageId] = UpgradePlanner.AutomaticDecision(group, choice);
            foreach (var forced in forcedVersions.Where(x => decisions.ContainsKey(x.Key)))
                decisions[forced.Key] = new(forced.Key, UpgradeChoice.ExactVersion, forced.Value);
        }
        else if (mode.Mode == UpgradeMode.SelectPackages)
        {
            var manual = await PresentAsync(
                windowSystem,
                parent,
                "Select package upgrades",
                new PackageDecisionContent(resolved, forcedVersions),
                cancellationToken,
                width: 110,
                height: 32).ConfigureAwait(false);
            if (manual is null) throw new OperationCanceledException();
            foreach (var decision in manual.Decisions) decisions[decision.PackageId] = decision;
        }

        var gitWorkflow = await ReadGitWorkflowAsync(context, configuration).ConfigureAwait(false);
        var plan = mode.Mode == UpgradeMode.ValidatedIncremental
            ? planner.CreateValidatedIncremental(
                selected,
                resolved,
                forcedVersions,
                gitWorkflow,
                DateTimeOffset.UtcNow)
            : planner.Create(
                selected,
                resolved,
                decisions,
                gitWorkflow,
                DateTimeOffset.UtcNow);
        if (plan.Repositories.Length == 0)
        {
            await ShowMessageAsync(windowSystem, parent, "Nothing to update",
                "The selected decisions produce no upgrades. No files will be changed.", cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var reviewApproved = await ConfirmLargeAsync(
            windowSystem,
            parent,
            "Review immutable upgrade plan",
            PresentationText.Review(plan),
            "Run read-only preflight",
            cancellationToken).ConfigureAwait(false);
        if (!reviewApproved) throw new OperationCanceledException();

        var inspected = await context.RunWithProgress(
            "Read-only preflight",
            $"Inspecting {plan.Repositories.Length} repositories before any changes…",
            async (token, progress) =>
            {
                progress.Report("Checking tools, branches, remotes, paths, and reviewed package values…");
                return await preflight.InspectAsync(plan, token).ConfigureAwait(false);
            }).ConfigureAwait(false);
        if (inspected.IsDefault) throw new OperationCanceledException();

        var readyCount = inspected.Count(x => x.IsReady);
        var approved = await ConfirmLargeAsync(
            windowSystem,
            parent,
            "Preflight results — final approval",
            PresentationText.Preflight(inspected) +
                $"\n\n[bold]{readyCount} of {inspected.Length} repositories are ready.[/] " +
                "Only ready repositories will be changed.",
            "Upgrade ready repositories",
            cancellationToken).ConfigureAwait(false);
        if (!approved) return 0;

        var byRoot = inspected.ToDictionary(x => x.RepositoryRoot, PathComparer);
        var progressViewModel = new RepositoryProgressViewModel(plan.Repositories.Select(x => x.RepositoryRoot));
        var results = await DialogsApi.RunWithProgressAsync<ImmutableArray<RepositoryRunResult>>(
            windowSystem,
            "Upgrading repositories",
            PresentationText.Progress(progressViewModel.Snapshot()),
            async (token, progress) =>
            {
                var terminalCount = 0;
                var gate = new object();
                var reporter = new ImmediateProgress<ProgressEvent>(value =>
                {
                    lock (gate)
                    {
                        progressViewModel.Apply(value);
                        if (value.Stage is RunStage.Passed or RunStage.Failed or RunStage.Skipped) terminalCount++;
                        progress.Report(new ProgressUpdate(
                            (double)terminalCount / plan.Repositories.Length,
                            PresentationText.Progress(progressViewModel.Snapshot()),
                            false));
                    }
                });
                return await coordinator.ExecuteAsync(plan, byRoot, reporter, token).ConfigureAwait(false);
            },
            allowMarkup: true,
            parent: parent).ConfigureAwait(false);
        if (results.IsDefault) throw new OperationCanceledException();

        await ShowMessageAsync(
            windowSystem,
            parent,
            "Final summary",
            PresentationText.Summary(results),
            cancellationToken,
            width: 110,
            height: 34).ConfigureAwait(false);
        return results.Any(x => x.Status == RunStage.Failed) ? 1 : 0;
    }

    private async Task<AppConfiguration> ConfigureAsync(
        FlowContext context,
        AppConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var defaultFolder = string.IsNullOrWhiteSpace(configuration.ProjectsFolder)
            ? Environment.CurrentDirectory
            : configuration.ProjectsFolder;
        string folder;
        while (true)
        {
            var entered = await context.Prompt(
                "Projects folder",
                "Folder containing the Git repositories to scan:",
                defaultFolder).ConfigureAwait(false);
            if (entered is null) throw new OperationCanceledException();
            try
            {
                folder = Path.GetFullPath(string.IsNullOrWhiteSpace(entered) ? defaultFolder : entered.Trim());
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                folder = string.Empty;
            }
            if (Directory.Exists(folder)) break;
            var retry = await context.Confirm(
                "Folder not found",
                "That folder does not exist. Choose Retry to enter another path.",
                "Retry",
                "Cancel",
                NotificationSeverityEnum.Warning).ConfigureAwait(false);
            if (!retry) throw new OperationCanceledException();
        }

        var updated = configuration with
        {
            ProjectsFolder = folder
        };
        await configurationStore.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    private async Task<AppConfiguration> ManagePackageRulesAsync(
        ConsoleWindowSystem windowSystem,
        Window parent,
        FlowContext context,
        ImmutableArray<SelectionEntry> entries,
        AppConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var result = await context.RunWithProgress(
            "Reading package inventory",
            "Combining package declarations across all discovered projects…",
            (token, _) => Task.Run(() => inventory.Read(
                entries,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)), token)).ConfigureAwait(false);
        if (result is null) throw new OperationCanceledException();

        if (result.Warnings.Length > 0)
            await ShowMessageAsync(
                windowSystem,
                parent,
                "Package inventory warnings",
                string.Join('\n', result.Warnings.Select(x => $"• {PresentationText.Escape(x)}")),
                cancellationToken).ConfigureAwait(false);

        var viewModel = new PackageRulesViewModel(
            result.Occurrences,
            configuration.IgnoredPackages,
            configuration.ForcedPackageVersions);

        async Task EditPackageAsync(PackageRuleViewModel package, Window rulesWindow)
        {
            var ruleAction = await PresentAsync(
                windowSystem,
                rulesWindow,
                $"Package rule: {package.PackageId}",
                new PackageRuleDialogContent(package),
                cancellationToken,
                width: 64,
                height: 16).ConfigureAwait(true);
            if (ruleAction == PackageRuleDialogAction.Close) return;
            if (ruleAction == PackageRuleDialogAction.ToggleUpdatesEnabled)
            {
                package.ToggleIgnored();
                return;
            }
            if (ruleAction == PackageRuleDialogAction.ClearRule)
            {
                package.Clear();
                return;
            }
            if (!package.IsDiscovered)
            {
                await ShowMessageAsync(
                    windowSystem,
                    rulesWindow,
                    "Package versions unavailable",
                    "This package is not currently discovered, so no effective NuGet source can be queried.",
                    cancellationToken,
                    width: 76,
                    height: 14).ConfigureAwait(true);
                return;
            }

            var lookup = await context.RunWithProgress(
                "Loading package versions",
                $"Querying configured NuGet sources for every {package.PackageId} version…",
                (token, _) => versions.GetAllVersionsAsync(
                    package.ProjectPath!,
                    package.PackageId,
                    token)).ConfigureAwait(false);
            if (lookup is null) throw new OperationCanceledException();
            if (lookup.Error is not null || lookup.Versions.Length == 0)
            {
                var message = lookup.Error ?? "The configured NuGet sources returned no versions for this package.";
                await ShowMessageAsync(
                    windowSystem,
                    rulesWindow,
                    "Package versions unavailable",
                    PresentationText.Escape(message),
                    cancellationToken).ConfigureAwait(true);
                return;
            }

            var selection = await PresentAsync(
                windowSystem,
                rulesWindow,
                $"Select {package.PackageId} version",
                new PackageVersionSelectionContent(package.PackageId, lookup.Versions),
                cancellationToken,
                width: 92,
                height: 32).ConfigureAwait(true);
            if (selection is not null) package.Force(selection.Version);
        }

        var action = await PresentAsync(
            windowSystem,
            parent,
            "Package update rules",
            new PackageRulesContent(viewModel, EditPackageAsync),
            cancellationToken,
            width: 110,
            height: 34).ConfigureAwait(false);
        if (action is null) return configuration;

        var updated = configuration with
        {
            IgnoredPackages = viewModel.IgnoredPackages,
            ForcedPackageVersions = viewModel.ForcedVersions
        };
        await configurationStore.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    private static async Task<GitWorkflowOptions> ReadGitWorkflowAsync(
        FlowContext context,
        AppConfiguration configuration)
    {
        var useBase = await context.Confirm(
            "Base branch",
            "Switch to and fast-forward a base branch before applying updates? Choose Current branch to update each repository's checked-out branch without switching or pulling.",
            "Select base",
            "Current branch").ConfigureAwait(false);
        var baseBranch = useBase
            ? await ReadBranchAsync(
                context,
                "Base branch",
                "Base branch to switch to and synchronize:",
                configuration.DevelopmentBranch).ConfigureAwait(false)
            : null;

        var createBranch = await context.Confirm(
            "Update branch",
            "Create and switch to a new branch before changing packages? Any valid, currently unused Git branch name is accepted.",
            "Create branch",
            "Update directly").ConfigureAwait(false);
        var targetBranch = createBranch
            ? await ReadBranchAsync(
                context,
                "New update branch",
                "Enter the new branch name (for example dependency-updates/august):").ConfigureAwait(false)
            : null;

        var commitAndPush = await context.Confirm(
            "Commit and push",
            $"After restore, build, and tests pass, selectively commit the package files and push the resulting branch to {configuration.RemoteName}?",
            "Commit and push",
            "Leave uncommitted").ConfigureAwait(false);

        return new(configuration.RemoteName, baseBranch, targetBranch, commitAndPush);
    }

    private static async Task<string> ReadBranchAsync(
        FlowContext context,
        string title,
        string message,
        string? defaultValue = null)
    {
        while (true)
        {
            var entered = await context.Prompt(title, message, defaultValue).ConfigureAwait(false);
            if (entered is null) throw new OperationCanceledException();
            if (!string.IsNullOrWhiteSpace(entered)) return entered.Trim();
            var retry = await context.Confirm(
                "Branch required",
                "Enter a branch name, or cancel this run and choose a workflow that does not require one.",
                "Retry",
                "Cancel",
                NotificationSeverityEnum.Warning).ConfigureAwait(false);
            if (!retry) throw new OperationCanceledException();
        }
    }

    private static async Task<T?> PresentAsync<T>(
        ConsoleWindowSystem windowSystem,
        Window parent,
        string title,
        IFlowStepContent<T> content,
        CancellationToken cancellationToken,
        int width = 100,
        int height = 28)
    {
        var host = new ModalWindowHost(windowSystem, parent);
        var outcome = await host.PresentAsync(
            content,
            new FlowChrome(title, widthHint: width, heightHint: height, resizable: true),
            cancellationToken).ConfigureAwait(false);
        return outcome.Verdict == FlowVerdict.Cancel ? default : outcome.Value;
    }

    private static async Task ShowMessageAsync(
        ConsoleWindowSystem windowSystem,
        Window parent,
        string title,
        string message,
        CancellationToken cancellationToken,
        int width = 100,
        int height = 28) =>
        _ = await PresentAsync(
            windowSystem,
            parent,
            title,
            new ScrollableMessageContent(message),
            cancellationToken,
            width,
            height).ConfigureAwait(false);

    private static async Task<bool> ConfirmLargeAsync(
        ConsoleWindowSystem windowSystem,
        Window parent,
        string title,
        string message,
        string confirmLabel,
        CancellationToken cancellationToken)
    {
        var result = await PresentAsync(
            windowSystem,
            parent,
            title,
            new ScrollableConfirmationContent(message, confirmLabel),
            cancellationToken,
            width: 110,
            height: 34).ConfigureAwait(false);
        return result == true;
    }

    private static void ShowHelp() => Console.WriteLine(
        "dotnet-updater\n\n" +
        "Keyboard-driven SharpConsoleUI application for NuGet upgrades across Git repositories.\n" +
        "Run without arguments. Use Tab to move focus, arrows to navigate, Space to toggle checkboxes, " +
        "Enter to activate, and Esc to cancel a dialog. Ctrl+C requests cancellation between operations.");

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
