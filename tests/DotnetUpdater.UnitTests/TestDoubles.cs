namespace DotnetUpdater.UnitTests;

internal sealed class FixedPath(string path) : IConfigurationPathProvider
{
    public string GetPath() => path;
}

internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        "dotnet-updater-tests",
        Guid.NewGuid().ToString("N"));

    public TempDirectory() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, true);
        }
        catch (IOException)
        {
        }
    }
}

internal sealed class RecordingRunner(Func<ProcessRequest, ProcessResult> response) : IProcessRunner
{
    public List<ProcessRequest> Requests { get; } = [];

    public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(response(request));
    }
}

internal sealed class ConcurrencyTrackingRunner(TimeSpan delay) : IProcessRunner
{
    private readonly object _gate = new();
    private int _active;

    public int MaximumConcurrency { get; private set; }

    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _active++;
            MaximumConcurrency = Math.Max(MaximumConcurrency, _active);
        }

        try
        {
            await Task.Delay(delay, cancellationToken);
            var project = request.Arguments.SkipWhile(x => x != "--project").Skip(1).First();
            var packageId = Path.GetFileNameWithoutExtension(project);
            var latest = request.Arguments.Contains("--highest-minor") ? "1.9.0" : "2.0.0";
            var json = $$"""{"projects":[{"frameworks":[{"topLevelPackages":[{"id":"{{packageId}}","latestVersion":"{{latest}}"}]}]}]}""";
            return new(0, json, string.Empty);
        }
        finally
        {
            lock (_gate) _active--;
        }
    }
}

internal sealed class RecordingProgress<T> : IProgress<T>
{
    private readonly object _gate = new();
    private readonly List<T> _messages = [];

    public IReadOnlyList<T> Messages
    {
        get
        {
            lock (_gate) return _messages.ToArray();
        }
    }

    public void Report(T value)
    {
        lock (_gate) _messages.Add(value);
    }
}

internal sealed class RecordingAllVersionsSource(PackageVersionLookup response) : IAllPackageVersionsSource
{
    public (string ProjectPath, string PackageId)? Request { get; private set; }

    public Task<PackageVersionLookup> GetAllAsync(
        string projectPath,
        string packageId,
        CancellationToken cancellationToken)
    {
        Request = (projectPath, packageId);
        return Task.FromResult(response);
    }
}

internal sealed class ContextualAllVersionsSource(
    Func<string, string, PackageVersionLookup> response) : IAllPackageVersionsSource
{
    public Task<PackageVersionLookup> GetAllAsync(
        string projectPath,
        string packageId,
        CancellationToken cancellationToken) =>
        Task.FromResult(response(projectPath, packageId));
}

internal sealed class CancelOnceRunner : IProcessRunner
{
    private int requestCount;

    public int RequestCount => requestCount;

    public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref requestCount) == 1)
            return Task.FromCanceled<ProcessResult>(new CancellationToken(canceled: true));
        var latest = request.Arguments.Contains("--highest-minor") ? "1.9.0" : "2.0.0";
        return Task.FromResult(new ProcessResult(
            0,
            $$"""{"id":"Example.Package","latestVersion":"{{latest}}"}""",
            string.Empty));
    }
}

internal sealed class RecordingShortcutRegistry : IConsoleShortcutRegistry
{
    public ConsoleModifiers Modifiers { get; private set; }
    public ConsoleKey Key { get; private set; }
    public Func<bool>? Handler { get; private set; }

    public void Register(ConsoleModifiers modifiers, ConsoleKey key, Func<bool> handler)
    {
        Modifiers = modifiers;
        Key = key;
        Handler = handler;
    }
}
