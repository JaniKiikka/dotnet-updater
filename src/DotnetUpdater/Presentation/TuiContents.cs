using System.Collections.Immutable;
using DotnetUpdater.Configuration;
using DotnetUpdater.Domain;
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

internal enum PackageRulesActionKind { Save, SelectVersion }

internal sealed record PackageRulesAction(PackageRulesActionKind Kind, PackageRuleViewModel? Package = null);

internal sealed class PackageRulesContent : IFlowStepContent<PackageRulesAction>
{
    private readonly TaskCompletionSource<PackageRulesAction?> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly PackageRulesViewModel viewModel;

    public PackageRulesContent(PackageRulesViewModel viewModel) => this.viewModel = viewModel;

    public Task<PackageRulesAction?> Completion => completion.Task;
    public event Action? StateChanged;

    public IWindowControl BuildContent(FlowChrome chrome)
    {
        var status = Ctl.Markup().Build();
        var items = viewModel.Items.Select(package => new ListItem(package.DisplayText) { Tag = package }).ToArray();
        var list = Ctl.List("Package update rules")
            .AddItems(items)
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .Build();

        void UpdateStatus()
        {
            var ignored = viewModel.Items.Count(x => x.State == PackageRuleState.Ignored);
            var forced = viewModel.Items.Count(x => x.State == PackageRuleState.Forced);
            status.SetContent([$"[dim]{ignored} ignored · {forced} forced to exact versions · ↑/↓ navigates · Tab reaches actions[/]"]);
            StateChanged?.Invoke();
        }

        void Refresh(ListItem item)
        {
            item.Text = ((PackageRuleViewModel)item.Tag!).DisplayText;
            UpdateStatus();
        }

        var panel = Ctl.ScrollablePanel().WithVerticalAlignment(VerticalAlignment.Fill).Build();
        panel.AddControl(Ctl.Markup()
            .AddLine("[bold]Choose which packages are ignored and which are forced to exact versions.[/]")
            .AddLine("[dim][IGNORED] packages are skipped. [FORCED] packages are set to the shown version on every run, including downgrades and prereleases.[/]")
            .AddLine("[dim]Saved packages not found in this scan stay visible so their rules can be cleared.[/]")
            .WithMargin(1)
            .Build());
        panel.AddControl(list);
        panel.AddControl(status);
        panel.AddControl(Ctl.Button("Toggle ignored")
            .OnClick((_, _) =>
            {
                if (list.SelectedItem is not { } item) return;
                ((PackageRuleViewModel)item.Tag!).ToggleIgnored();
                Refresh(item);
            })
            .Build());
        panel.AddControl(Ctl.Button("Force selected version")
            .OnClick((_, _) =>
            {
                if (list.SelectedItem?.Tag is not PackageRuleViewModel package) return;
                if (!package.IsDiscovered)
                {
                    status.SetContent(["[yellow]This package is not currently discovered, so no effective NuGet source can be queried.[/]"]);
                    return;
                }
                completion.TrySetResult(new(PackageRulesActionKind.SelectVersion, package));
            })
            .Build());
        panel.AddControl(Ctl.Button("Clear selected rule")
            .OnClick((_, _) =>
            {
                if (list.SelectedItem is not { } item) return;
                ((PackageRuleViewModel)item.Tag!).Clear();
                Refresh(item);
            })
            .Build());
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
