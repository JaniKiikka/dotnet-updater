using SharpConsoleUI;

namespace DotnetUpdater.Presentation;

public interface IConsoleShortcutRegistry
{
    void Register(ConsoleModifiers modifiers, ConsoleKey key, Func<bool> handler);
}

public sealed class ConsoleCancellationInput(
    IConsoleShortcutRegistry shortcuts,
    Action requestCancellation)
{
    public void Register() => shortcuts.Register(
        ConsoleModifiers.Control,
        ConsoleKey.C,
        () =>
        {
            requestCancellation();
            return true;
        });
}

internal sealed class SharpConsoleShortcutRegistry(ConsoleWindowSystem windowSystem)
    : IConsoleShortcutRegistry
{
    public void Register(ConsoleModifiers modifiers, ConsoleKey key, Func<bool> handler) =>
        windowSystem.RegisterGlobalShortcut(modifiers, key, handler);
}
