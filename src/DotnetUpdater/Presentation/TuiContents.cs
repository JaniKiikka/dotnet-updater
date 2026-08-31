using System.Collections.Immutable;
using DotnetUpdater.Configuration;
using DotnetUpdater.Domain;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Flows;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Themes;
using Ctl = SharpConsoleUI.Builders.Controls;

namespace DotnetUpdater.Presentation;

internal sealed record EntrySelection(ImmutableArray<SelectionEntry> Entries);

internal static class TuiChrome
{
    private static readonly Color FocusedButtonForeground = new("#07111F");

    public static IWindowControl View(IWindowControl body, params ButtonControl[] buttons) => Ctl.Grid()
        .Columns([GridLength.Star()])
        .Rows([GridLength.Star(), GridLength.Cells(1), GridLength.Auto()])
        .Place(body, 0, 0)
        .Place(Ctl.RuleBuilder().WithColorRole(ColorRole.Secondary).Build(), 1, 0)
        .Place(ActionRow(buttons), 2, 0)
        .WithAlignment(HorizontalAlignment.Stretch)
        .WithVerticalAlignment(VerticalAlignment.Fill)
        .Build();

    private static HorizontalGridControl ActionRow(IEnumerable<ButtonControl> buttons)
    {
        var controls = buttons
            .SelectMany(button => new IWindowControl[]
                { button, Ctl.Markup("[dim]|[/]").Build() })
            .SkipLast(1);
        return HorizontalGridControl.FromControls(controls, HorizontalAlignment.Right);
    }

    public static ButtonControl Primary(string label, EventHandler<ButtonControl> onClick) =>
        Ctl.Button(label)
            .WithColorRole(ColorRole.Primary)
            .Outline(true)
            .WithFocusedColors(FocusedButtonForeground, new Color("#38BDF8"))
            .OnClick(onClick)
            .Build();

    public static ButtonControl Secondary(string label, EventHandler<ButtonControl> onClick) =>
        Ctl.Button(label)
            .WithColorRole(ColorRole.Secondary)
            .Outline(true)
            .WithFocusedColors(FocusedButtonForeground, new Color("#818CF8"))
            .OnClick(onClick)
            .Build();

    public static ButtonControl Danger(string label, EventHandler<ButtonControl> onClick) =>
        Ctl.Button(label)
            .WithColorRole(ColorRole.Danger)
            .Outline(true)
            .WithFocusedColors(FocusedButtonForeground, new Color("#FB7185"))
            .OnClick(onClick)
            .Build();

    public static ScrollablePanelControl ScrollableBody() => Ctl.ScrollablePanel()
        .WithVerticalAlignment(VerticalAlignment.Fill)
        .WithScrollbar(true)
        .WithMargin(1, 0, 1, 0)
        .Build();
}

internal sealed class EntrySelectionContent : IFlowStepContent<EntrySelection>
{
    private readonly TaskCompletionSource<EntrySelection?> completion = NewCompletion<EntrySelection>();
    private readonly ImmutableArray<SelectionEntry> entries;
    private ListControl? list;

    public EntrySelectionContent(ImmutableArray<SelectionEntry> entries) => this.entries = entries;

    public Task<EntrySelection?> Completion => completion.Task;
    public event Action? StateChanged;

    public IWindowControl BuildContent(FlowChrome chrome)
    {
        var status = Ctl.Markup().Build();
        var items = entries.Select(entry => new ListItem(
            $"{entry.DisplayName}  ·  {Path.GetFileName(entry.RepositoryRoot)}  ·  {entry.ProjectPaths.Length} project(s)",
            "◈",
            new Color("#38BDF8"))
        {
            Tag = entry,
            IsChecked = true
        }).ToArray();
        list = Ctl.List("Choose solutions and projects to upgrade")
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
        var panel = TuiChrome.ScrollableBody();
        panel.AddControl(list);
        panel.AddControl(status);
        UpdateStatus();
        return TuiChrome.View(
            panel,
            TuiChrome.Secondary("Select all", (_, _) => list.SetAllChecked(true)),
            TuiChrome.Secondary("Clear", (_, _) => list.SetAllChecked(false)),
            TuiChrome.Danger("Cancel", (_, _) => completion.TrySetCanceled()),
            TuiChrome.Primary("Continue", (_, _) =>
            {
                var selected = list.GetCheckedItems()
                    .Select(x => (SelectionEntry)x.Tag!)
                    .ToImmutableArray();
                if (selected.Length == 0)
                {
                    status.SetContent(["[red]Select at least one entry before continuing.[/]"]);
                    StateChanged?.Invoke();
                    return;
                }
                completion.TrySetResult(new EntrySelection(selected));
            }));
    }

    private static TaskCompletionSource<T?> NewCompletion<T>() where T : class =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class ChoiceListContent<T> : IFlowStepContent<T> where T : class
{
    private readonly TaskCompletionSource<T?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string description;
    private readonly IReadOnlyList<(string Label, string Description, T Value)> choices;
    private ListControl? list;

    public ChoiceListContent(string description, IReadOnlyList<(string Label, string Description, T Value)> choices)
    {
        this.description = description;
        this.choices = choices;
    }

    public Task<T?> Completion => completion.Task;
    public event Action? StateChanged;

    public IWindowControl BuildContent(FlowChrome chrome)
    {
        var panel = TuiChrome.ScrollableBody();
        panel.AddControl(Ctl.Markup(PresentationText.Escape(description)).WithMargin(1).Build());
        var items = choices.Select(choice => new ListItem(
            $"{choice.Label} — {choice.Description}",
            "◆",
            new Color("#818CF8"))
        {
            Tag = choice.Value
        });
        list = Ctl.List("Choose an option")
            .AddItems(items)
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .OnItemActivated((_, item) => completion.TrySetResult((T)item.Tag!))
            .Build();
        panel.AddControl(list);
        panel.AddControl(Ctl.Markup("[dim]↑/↓ selects · Enter confirms · Esc cancels[/]").Build());
        StateChanged?.Invoke();
        return TuiChrome.View(
            panel,
            TuiChrome.Danger("Cancel", (_, _) => completion.TrySetCanceled()),
            TuiChrome.Primary("Select", (_, _) =>
            {
                if (list.SelectedItem?.Tag is T value) completion.TrySetResult(value);
            }));
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
        var currentRule = package.State switch
        {
            PackageRuleState.Ignored => "updates disabled",
            PackageRuleState.Forced => $"forced to {package.ForcedVersion}",
            _ => "updates enabled"
        };

        var panel = TuiChrome.ScrollableBody();
        panel.AddControl(Ctl.Markup()
            .AddLine($"[bold]{PresentationText.Escape(package.PackageId)}[/]")
            .AddLine($"[dim]Current rule: {PresentationText.Escape(currentRule)}[/]")
            .AddLine("")
            .AddLine("Choose an action from the toolbar below.")
            .WithMargin(1)
            .Build());
        var updatesEnabled = package.State != PackageRuleState.Ignored;
        StateChanged?.Invoke();
        return TuiChrome.View(
            panel,
            TuiChrome.Danger("Close", (_, _) => completion.TrySetResult(PackageRuleDialogAction.Close)),
            TuiChrome.Secondary("Clear rule", (_, _) => completion.TrySetResult(PackageRuleDialogAction.ClearRule)),
            TuiChrome.Secondary(updatesEnabled ? "Disable updates" : "Enable updates",
                (_, _) => completion.TrySetResult(PackageRuleDialogAction.ToggleUpdatesEnabled)),
            TuiChrome.Primary("Force version", (_, _) => completion.TrySetResult(PackageRuleDialogAction.SelectVersion)));
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
        static (string Icon, Color Color) PackageStyle(PackageRuleViewModel package) => package.State switch
        {
            PackageRuleState.Ignored => ("⊘", new Color("#FB7185")),
            PackageRuleState.Forced => ("◆", new Color("#C084FC")),
            _ => ("●", new Color("#34D399"))
        };

        static void ApplyPackageStyle(ListItem item, PackageRuleViewModel package)
        {
            var style = PackageStyle(package);
            item.Text = package.DisplayText;
            item.Icon = style.Icon;
            item.IconColor = style.Color;
        }

        var items = viewModel.Items.Select(package =>
        {
            var style = PackageStyle(package);
            return new ListItem(package.DisplayText, style.Icon, style.Color) { Tag = package };
        }).ToArray();
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
                ApplyPackageStyle(item, package);
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

        list = Ctl.List("Detected packages")
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
            status.SetContent([$"[#FB7185]{ignored} ignored[/] · [#C084FC]{forced} forced[/] · [dim]Click / Space / Enter edits · ↑/↓ navigates[/]"]);
            StateChanged?.Invoke();
        }

        var panel = TuiChrome.ScrollableBody();
        // panel.AddControl(Ctl.Markup()
        //     .AddLine("[bold]Select a package to change its update rule.[/]")
        //     .AddLine("[dim][IGNORED] packages are skipped. [FORCED] packages are set to the shown version on every run, including downgrades and prereleases.[/]")
        //     .AddLine("[dim]Saved packages not found in this scan stay visible so their rules can be cleared.[/]")
        //     .WithMargin(1)
        //     .Build());
        panel.AddControl(list);
        panel.AddControl(status);
        UpdateStatus();
        return TuiChrome.View(
            panel,
            TuiChrome.Danger("Back without saving", (_, _) => completion.TrySetCanceled()),
            TuiChrome.Primary("Save package rules", (_, _) => completion.TrySetResult(new(PackageRulesActionKind.Save))));
    }
}

internal sealed record PackageVersionSelection(string Version);

internal sealed class PackageVersionSelectionContent : IFlowStepContent<PackageVersionSelection>
{
    private readonly TaskCompletionSource<PackageVersionSelection?> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string packageId;
    private readonly ImmutableArray<string> versions;
    private ListControl? list;

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
        list = Ctl.List("Available versions")
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
        var panel = TuiChrome.ScrollableBody();
        panel.AddControl(Ctl.Markup()
            .AddLine($"[bold]Force {PresentationText.Escape(packageId)} to an exact version.[/]")
            .AddLine("[dim]The list contains every version returned by the configured NuGet sources, including prereleases.[/]")
            .WithMargin(1)
            .Build());
        panel.AddControl(search);
        panel.AddControl(list);
        panel.AddControl(status);
        Filter(string.Empty);
        return TuiChrome.View(
            panel,
            TuiChrome.Danger("Cancel", (_, _) => completion.TrySetCanceled()),
            TuiChrome.Primary("Use selected version", (_, _) =>
            {
                if (list.SelectedItem is { } item) completion.TrySetResult(new(item.Text));
            }));
    }
}

internal sealed class PackageDecisionContent : IFlowStepContent<ManualDecisionSelection>
{
    private readonly TaskCompletionSource<ManualDecisionSelection?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ImmutableArray<PackageDecisionViewModel> decisions;
    private ListControl? list;
    private Action? cycleSelected;

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
        static (string Icon, Color Color) DecisionStyle(PackageDecisionViewModel decision) => decision.Choice switch
        {
            UpgradeChoice.LatestMajor => ("▲", new Color("#FBBF24")),
            UpgradeChoice.LatestMinor => ("↑", new Color("#38BDF8")),
            UpgradeChoice.ExactVersion => ("◆", new Color("#C084FC")),
            _ => ("·", new Color("#7C8DA6"))
        };

        var items = decisions.Select(decision =>
        {
            var style = DecisionStyle(decision);
            return new ListItem(decision.DisplayText, style.Icon, style.Color) { Tag = decision };
        }).ToArray();
        list = Ctl.List("Package decisions")
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
            var style = DecisionStyle(decision);
            item.Text = decision.DisplayText;
            item.Icon = style.Icon;
            item.IconColor = style.Color;
            UpdateStatus();
        }

        cycleSelected = () =>
        {
            if (list.SelectedItem is { } item) Cycle(item);
        };

        var panel = TuiChrome.ScrollableBody();
        panel.AddControl(Ctl.Markup()
            .AddLine("[bold]Set one decision per package.[/]")
            .AddLine("[dim]Choices cycle: no update → latest minor → latest major. Persistent forced versions are shown but cannot be changed here.[/]")
            .WithMargin(1)
            .Build());
        panel.AddControl(list);
        panel.AddControl(status);
        UpdateStatus();
        return TuiChrome.View(
            panel,
            TuiChrome.Secondary("Cycle selected", (_, _) => cycleSelected?.Invoke()),
            TuiChrome.Danger("Cancel", (_, _) => completion.TrySetCanceled()),
            TuiChrome.Primary("Apply decisions", (_, _) => completion.TrySetResult(new ManualDecisionSelection(
                decisions.Select(x => x.ToDecision()).ToImmutableArray()))));
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
        var panel = TuiChrome.ScrollableBody();
        panel.AddControl(Ctl.Markup(message).WithMargin(1).Build());
        StateChanged?.Invoke();
        return TuiChrome.View(
            panel,
            TuiChrome.Primary(closeLabel, (_, _) => completion.TrySetResult(true)));
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
        var panel = TuiChrome.ScrollableBody();
        panel.AddControl(Ctl.Markup(message).WithMargin(1).Build());
        StateChanged?.Invoke();
        return TuiChrome.View(
            panel,
            TuiChrome.Danger("Cancel", (_, _) => completion.TrySetCanceled()),
            TuiChrome.Primary(confirmLabel, (_, _) => completion.TrySetResult(true)));
    }
}
