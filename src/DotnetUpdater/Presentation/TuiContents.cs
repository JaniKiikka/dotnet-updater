using System.Collections.Immutable;
using DotnetUpdater.Configuration;
using DotnetUpdater.Domain;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Flows;
using SharpConsoleUI.Layout;
using Ctl = SharpConsoleUI.Builders.Controls;

namespace DotnetUpdater.Presentation;

internal sealed record EntrySelection(ImmutableArray<SelectionEntry> Entries);

internal sealed class EntrySelectionContent : IFlowStepContent<EntrySelection>
{
    private readonly TaskCompletionSource<EntrySelection?> completion = NewCompletion<EntrySelection>();
    private readonly ImmutableArray<SelectionEntry> entries;

    public EntrySelectionContent(ImmutableArray<SelectionEntry> entries) => this.entries = entries;

    public Task<EntrySelection?> Completion => completion.Task;
    public event Action? StateChanged;

    public IWindowControl BuildContent(FlowChrome chrome)
    {
        var status = Ctl.Markup().Build();
        var items = entries.Select(entry => new ListItem(
            $"{entry.DisplayName}  ·  {Path.GetFileName(entry.RepositoryRoot)}  ·  {entry.ProjectPaths.Length} project(s)")
        {
            Tag = entry,
            IsChecked = true
        }).ToArray();
        var list = Ctl.List("Solutions and standalone projects")
            .AddItems(items)
            .WithCheckboxMode()
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .Build();

        void UpdateStatus()
        {
            var count = list.GetCheckedItems().Count;
            status.SetContent([$"[dim]{count} of {entries.Length} selected · Space toggles · ↑/↓ navigates · Tab reaches actions[/]"]);
            StateChanged?.Invoke();
        }

        list.CheckedItemsChanged += (_, _) => UpdateStatus();
        var panel = Ctl.ScrollablePanel()
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .Build();
        panel.AddControl(Ctl.Markup()
            .AddLine("[bold]Choose everything that should share this upgrade plan.[/]")
            .AddLine("[dim]Projects referenced by a selected solution are included automatically.[/]")
            .WithMargin(1)
            .Build());
        panel.AddControl(list);
        panel.AddControl(status);
        panel.AddControl(Ctl.Button("Select all").OnClick((_, _) => list.SetAllChecked(true)).Build());
        panel.AddControl(Ctl.Button("Clear selection").OnClick((_, _) => list.SetAllChecked(false)).Build());
        panel.AddControl(Ctl.Button("Continue")
            .OnClick((_, _) =>
            {
                var selected = list.GetCheckedItems()
                    .Select(x => (SelectionEntry)x.Tag!)
                    .ToImmutableArray();
                if (selected.Length == 0)
                {
                    status.SetContent(["[red]Select at least one entry before continuing.[/]"]);
                    return;
                }
                completion.TrySetResult(new EntrySelection(selected));
            })
            .Build());
        panel.AddControl(Ctl.Button("Cancel").OnClick((_, _) => completion.TrySetCanceled()).Build());
        UpdateStatus();
        return panel;
    }

    private static TaskCompletionSource<T?> NewCompletion<T>() where T : class =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class ChoiceListContent<T> : IFlowStepContent<T> where T : class
{
    private readonly TaskCompletionSource<T?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string description;
    private readonly IReadOnlyList<(string Label, string Description, T Value)> choices;

    public ChoiceListContent(string description, IReadOnlyList<(string Label, string Description, T Value)> choices)
    {
        this.description = description;
        this.choices = choices;
    }

    public Task<T?> Completion => completion.Task;
    public event Action? StateChanged;

    public IWindowControl BuildContent(FlowChrome chrome)
    {
        var panel = Ctl.ScrollablePanel().WithVerticalAlignment(VerticalAlignment.Fill).Build();
        panel.AddControl(Ctl.Markup(PresentationText.Escape(description)).WithMargin(1).Build());
        var items = choices.Select(choice => new ListItem($"{choice.Label} — {choice.Description}") { Tag = choice.Value });
        var list = Ctl.List("Choose an option")
            .AddItems(items)
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .OnItemActivated((_, item) => completion.TrySetResult((T)item.Tag!))
            .Build();
        panel.AddControl(list);
        panel.AddControl(Ctl.Markup("[dim]↑/↓ selects · Enter confirms · Esc cancels[/]").Build());
        StateChanged?.Invoke();
        return panel;
    }
}

internal sealed record ManualDecisionSelection(ImmutableArray<PackageDecision> Decisions);

internal enum PackageRulesActionKind { Save }

internal sealed record PackageRulesAction(PackageRulesActionKind Kind);

internal enum PackageRuleDialogAction { Close, ToggleUpdatesEnabled, SelectVersion, ClearRule }

internal sealed class PackageRuleDialogContent : IFlowStepContent<PackageRuleDialogAction>
{
    private readonly TaskCompletionSource<PackageRuleDialogAction> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly PackageRuleViewModel package;

    public PackageRuleDialogContent(PackageRuleViewModel package) => this.package = package;

    public Task<PackageRuleDialogAction> Completion => completion.Task;
    public event Action? StateChanged;

    public IWindowControl BuildContent(FlowChrome chrome)
    {
        var updatesEnabled = package.State != PackageRuleState.Ignored;
        var toggleLabel = updatesEnabled ? "Disable updates" : "Enable updates";
        var currentRule = package.State switch
        {
            PackageRuleState.Ignored => "updates disabled",
            PackageRuleState.Forced => $"forced to {package.ForcedVersion}",
            _ => "updates enabled"
        };

        var actions = new[]
        {
            new ListItem(toggleLabel) { Tag = PackageRuleDialogAction.ToggleUpdatesEnabled },
            new ListItem("Force selected version") { Tag = PackageRuleDialogAction.SelectVersion },
            new ListItem("Clear selected rule") { Tag = PackageRuleDialogAction.ClearRule },
            new ListItem("Close dialog") { Tag = PackageRuleDialogAction.Close }
        };
        Window? hookedWindow = null;
        ListControl? list = null;

        void CompleteSelected()
        {
            if (list?.SelectedItem?.Tag is PackageRuleDialogAction action)
                completion.TrySetResult(action);
        }

        void HandlePreviewKeyPressed(object? sender, KeyPressedEventArgs args)
        {
            if (!args.AlreadyHandled && list?.HasFocus == true && args.KeyInfo.Key == ConsoleKey.Spacebar)
            {
                args.Handled = true;
                CompleteSelected();
            }
        }

        list = Ctl.List("Choose an action")
            .AddItems(actions)
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .OnItemActivated((_, _) => CompleteSelected())
            .OnGotFocus((_, _, window) =>
            {
                if (ReferenceEquals(hookedWindow, window)) return;
                hookedWindow = window;
                window.PreviewKeyPressed += HandlePreviewKeyPressed;
            })
            .OnLostFocus((_, _, window) =>
            {
                window.PreviewKeyPressed -= HandlePreviewKeyPressed;
                if (ReferenceEquals(hookedWindow, window)) hookedWindow = null;
            })
            .Build();
        list.MouseClick += (_, _) => CompleteSelected();

        var panel = Ctl.ScrollablePanel().WithVerticalAlignment(VerticalAlignment.Fill).Build();
        panel.AddControl(Ctl.Markup()
            .AddLine($"[bold]{PresentationText.Escape(package.PackageId)}[/]")
            .AddLine($"[dim]Current rule: {PresentationText.Escape(currentRule)}[/]")
            .WithMargin(1)
            .Build());
        panel.AddControl(list);
        panel.AddControl(Ctl.Markup("[dim]↑/↓ selects · Click / Space / Enter confirms · Esc closes[/]").Build());
        StateChanged?.Invoke();
        return panel;
    }
}

internal sealed class PackageRulesContent : IFlowStepContent<PackageRulesAction>
{
    private readonly TaskCompletionSource<PackageRulesAction?> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly PackageRulesViewModel viewModel;
    private readonly Func<PackageRuleViewModel, Window, Task> editPackage;

    public PackageRulesContent(
        PackageRulesViewModel viewModel,
        Func<PackageRuleViewModel, Window, Task> editPackage)
    {
        this.viewModel = viewModel;
        this.editPackage = editPackage;
    }

    public Task<PackageRulesAction?> Completion => completion.Task;
    public event Action? StateChanged;

    public IWindowControl BuildContent(FlowChrome chrome)
    {
        var status = Ctl.Markup().Build();
        var items = viewModel.Items.Select(package => new ListItem(package.DisplayText) { Tag = package }).ToArray();
        Window? hookedWindow = null;
        ListControl? list = null;
        var editing = false;

        async Task EditSelectedPackageAsync(Window window)
        {
            if (editing || list?.SelectedItem is not { Tag: PackageRuleViewModel package } item) return;
            editing = true;
            try
            {
                await editPackage(package, window).ConfigureAwait(true);
                item.Text = package.DisplayText;
                UpdateStatus();
            }
            catch (OperationCanceledException)
            {
                // Closing a nested dialog leaves the package-rules screen open.
            }
            catch (Exception ex)
            {
                UpdateStatus(ex.Message);
            }
            finally
            {
                editing = false;
            }
        }

        void HandlePreviewKeyPressed(object? sender, KeyPressedEventArgs args)
        {
            if (!args.AlreadyHandled && list?.HasFocus == true && args.KeyInfo.Key == ConsoleKey.Spacebar)
            {
                args.Handled = true;
                if (hookedWindow is not null) _ = EditSelectedPackageAsync(hookedWindow);
            }
        }

        list = Ctl.List("Package update rules")
            .AddItems(items)
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .OnItemActivated((_, _, window) => _ = EditSelectedPackageAsync(window))
            .OnGotFocus((_, _, window) =>
            {
                if (ReferenceEquals(hookedWindow, window)) return;
                hookedWindow = window;
                window.PreviewKeyPressed += HandlePreviewKeyPressed;
            })
            .OnLostFocus((_, _, window) =>
            {
                window.PreviewKeyPressed -= HandlePreviewKeyPressed;
                if (ReferenceEquals(hookedWindow, window)) hookedWindow = null;
            })
            .Build();
        list.MouseClick += (_, args) =>
        {
            var window = args.SourceWindow ?? hookedWindow;
            if (window is not null) _ = EditSelectedPackageAsync(window);
        };

        void UpdateStatus(string? error = null)
        {
            if (error is not null)
            {
                status.SetContent([$"[red]{PresentationText.Escape(error)}[/]"]);
                StateChanged?.Invoke();
                return;
            }
            var ignored = viewModel.Items.Count(x => x.State == PackageRuleState.Ignored);
            var forced = viewModel.Items.Count(x => x.State == PackageRuleState.Forced);
            status.SetContent([$"[dim]{ignored} ignored · {forced} forced to exact versions · Click / Space / Enter edits · ↑/↓ navigates[/]"]);
            StateChanged?.Invoke();
        }

        var panel = Ctl.ScrollablePanel().WithVerticalAlignment(VerticalAlignment.Fill).Build();
        panel.AddControl(Ctl.Markup()
            .AddLine("[bold]Select a package to change its update rule.[/]")
            .AddLine("[dim][IGNORED] packages are skipped. [FORCED] packages are set to the shown version on every run, including downgrades and prereleases.[/]")
            .AddLine("[dim]Saved packages not found in this scan stay visible so their rules can be cleared.[/]")
            .WithMargin(1)
            .Build());
        panel.AddControl(list);
        panel.AddControl(status);
        panel.AddControl(Ctl.Button("Save package rules")
            .OnClick((_, _) => completion.TrySetResult(new(PackageRulesActionKind.Save)))
            .Build());
        panel.AddControl(Ctl.Button("Back without saving").OnClick((_, _) => completion.TrySetCanceled()).Build());
        UpdateStatus();
        return panel;
    }
}

internal sealed record PackageVersionSelection(string Version);

internal sealed class PackageVersionSelectionContent : IFlowStepContent<PackageVersionSelection>
{
    private readonly TaskCompletionSource<PackageVersionSelection?> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string packageId;
    private readonly ImmutableArray<string> versions;

    public PackageVersionSelectionContent(string packageId, ImmutableArray<string> versions)
    {
        this.packageId = packageId;
        this.versions = versions;
    }

    public Task<PackageVersionSelection?> Completion => completion.Task;
    public event Action? StateChanged;

    public IWindowControl BuildContent(FlowChrome chrome)
    {
        var status = Ctl.Markup().Build();
        var list = Ctl.List("Available versions")
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .OnItemActivated((_, item) => completion.TrySetResult(new(item.Text)))
            .Build();

        void Filter(string search)
        {
            var filtered = PackageVersionSearch.Filter(versions, search)
                .Select(x => new ListItem(x))
                .ToList();
            list.Items = filtered;
            status.SetContent([$"[dim]{filtered.Count} of {versions.Length} versions shown · stable and prerelease releases included[/]"]);
            StateChanged?.Invoke();
        }

        var search = Ctl.Prompt("Search: ")
            .WithPlaceholder("type any part of a version")
            .OnInputChanged((_, value) => Filter(value))
            .Build();
        var panel = Ctl.ScrollablePanel().WithVerticalAlignment(VerticalAlignment.Fill).Build();
        panel.AddControl(Ctl.Markup()
            .AddLine($"[bold]Force {PresentationText.Escape(packageId)} to an exact version.[/]")
            .AddLine("[dim]The list contains every version returned by the configured NuGet sources, including prereleases.[/]")
            .WithMargin(1)
            .Build());
        panel.AddControl(search);
        panel.AddControl(list);
        panel.AddControl(status);
        panel.AddControl(Ctl.Button("Use selected version")
            .OnClick((_, _) =>
            {
                if (list.SelectedItem is { } item) completion.TrySetResult(new(item.Text));
            })
            .Build());
        panel.AddControl(Ctl.Button("Cancel").OnClick((_, _) => completion.TrySetCanceled()).Build());
        Filter(string.Empty);
        return panel;
    }
}

internal sealed class PackageDecisionContent : IFlowStepContent<ManualDecisionSelection>
{
    private readonly TaskCompletionSource<ManualDecisionSelection?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ImmutableArray<PackageDecisionViewModel> decisions;

    public PackageDecisionContent(
        ImmutableArray<PackageGroup> groups,
        IReadOnlyDictionary<string, string> forcedVersions) =>
        decisions = groups.Select(x => new PackageDecisionViewModel(
            x,
            forcedVersions.GetValueOrDefault(x.PackageId))).ToImmutableArray();

    public Task<ManualDecisionSelection?> Completion => completion.Task;
    public event Action? StateChanged;

    public IWindowControl BuildContent(FlowChrome chrome)
    {
        var status = Ctl.Markup().Build();
        var items = decisions.Select(decision => new ListItem(decision.DisplayText) { Tag = decision }).ToArray();
        var list = Ctl.List("Package decisions")
            .AddItems(items)
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .OnItemActivated((_, item) => Cycle(item))
            .Build();

        void UpdateStatus()
        {
            var updates = decisions.Count(x => x.Choice is UpgradeChoice.LatestMinor or UpgradeChoice.LatestMajor or UpgradeChoice.ExactVersion);
            var majors = decisions.Count(x => x.Choice == UpgradeChoice.LatestMajor);
            var forced = decisions.Count(x => x.IsForced);
            status.SetContent([$"[dim]{updates} update(s) · {majors} major · {forced} forced · Enter cycles unforced packages[/]"]);
            StateChanged?.Invoke();
        }

        void Cycle(ListItem item)
        {
            var decision = (PackageDecisionViewModel)item.Tag!;
            decision.Cycle();
            item.Text = decision.DisplayText;
            UpdateStatus();
        }

        var panel = Ctl.ScrollablePanel().WithVerticalAlignment(VerticalAlignment.Fill).Build();
        panel.AddControl(Ctl.Markup()
            .AddLine("[bold]Set one decision per package.[/]")
            .AddLine("[dim]Choices cycle: no update → latest minor → latest major. Persistent forced versions are shown but cannot be changed here.[/]")
            .WithMargin(1)
            .Build());
        panel.AddControl(list);
        panel.AddControl(status);
        panel.AddControl(Ctl.Button("Cycle selected")
            .OnClick((_, _) => { if (list.SelectedItem is { } item) Cycle(item); })
            .Build());
        panel.AddControl(Ctl.Button("Apply decisions")
            .OnClick((_, _) => completion.TrySetResult(new ManualDecisionSelection(
                decisions.Select(x => x.ToDecision()).ToImmutableArray())))
            .Build());
        panel.AddControl(Ctl.Button("Cancel").OnClick((_, _) => completion.TrySetCanceled()).Build());
        UpdateStatus();
        return panel;
    }
}

internal sealed class ScrollableMessageContent : IFlowStepContent<bool>
{
    private readonly TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string message;
    private readonly string closeLabel;

    public ScrollableMessageContent(string message, string closeLabel = "Close")
    {
        this.message = message;
        this.closeLabel = closeLabel;
    }

    public Task<bool> Completion => completion.Task;
    public event Action? StateChanged;

    public IWindowControl BuildContent(FlowChrome chrome)
    {
        var panel = Ctl.ScrollablePanel().WithVerticalAlignment(VerticalAlignment.Fill).Build();
        panel.AddControl(Ctl.Markup(message).WithMargin(1).Build());
        panel.AddControl(Ctl.Button(closeLabel).OnClick((_, _) => completion.TrySetResult(true)).Build());
        StateChanged?.Invoke();
        return panel;
    }
}

internal sealed class ScrollableConfirmationContent : IFlowStepContent<bool>
{
    private readonly TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string message;
    private readonly string confirmLabel;

    public ScrollableConfirmationContent(string message, string confirmLabel)
    {
        this.message = message;
        this.confirmLabel = confirmLabel;
    }

    public Task<bool> Completion => completion.Task;
    public event Action? StateChanged;

    public IWindowControl BuildContent(FlowChrome chrome)
    {
        var panel = Ctl.ScrollablePanel().WithVerticalAlignment(VerticalAlignment.Fill).Build();
        panel.AddControl(Ctl.Markup(message).WithMargin(1).Build());
        panel.AddControl(Ctl.Button(confirmLabel).OnClick((_, _) => completion.TrySetResult(true)).Build());
        panel.AddControl(Ctl.Button("Cancel").OnClick((_, _) => completion.TrySetCanceled()).Build());
        StateChanged?.Invoke();
        return panel;
    }
}
