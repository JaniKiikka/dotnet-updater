namespace DotnetUpdater.UnitTests;

[TestClass]
public sealed class ConsoleCancellationInputTests
{
    [TestMethod]
    public void CtrlCRequestsCancellationAndConsumesTheKey()
    {
        var shortcuts = new RecordingShortcutRegistry();
        using var cancellation = new CancellationTokenSource();
        new ConsoleCancellationInput(shortcuts, cancellation.Cancel).Register();

        Assert.AreEqual(ConsoleModifiers.Control, shortcuts.Modifiers);
        Assert.AreEqual(ConsoleKey.C, shortcuts.Key);
        Assert.IsNotNull(shortcuts.Handler);
        Assert.IsTrue(shortcuts.Handler());
        Assert.IsTrue(cancellation.IsCancellationRequested);
    }
}
