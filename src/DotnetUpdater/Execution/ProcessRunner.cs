using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace DotnetUpdater.Execution;

public sealed record ProcessRequest(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory);
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken);
}

public sealed class ProcessRunner(IRunLogger logger) : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo
        {
            FileName = request.FileName,
            WorkingDirectory = request.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in request.Arguments) start.ArgumentList.Add(argument);
        logger.Write($"> {request.FileName} {string.Join(' ', request.Arguments.Select(Redactor.Redact))}");

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.Write(ex.Message);
            return new(127, string.Empty, ex.Message);
        }
        // Commands are atomic cancellation boundaries: never abandon a Git or file-related
        // process halfway through and leave its final state unclear.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(CancellationToken.None);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        logger.Write(stdout);
        logger.Write(stderr);
        cancellationToken.ThrowIfCancellationRequested();
        return new(process.ExitCode, stdout, stderr);
    }
}

public interface IRunLogger
{
    string Path { get; }
    void Write(string text);
}

public sealed class FileRunLogger : IRunLogger
{
    private readonly object _gate = new();
    public string Path { get; }

    public FileRunLogger(string? directory = null)
    {
        var explicitDirectory = directory is not null;
        directory ??= System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dotnet-updater", "logs");
        try
        {
            Path = CreateLog(directory);
        }
        catch (Exception ex) when (!explicitDirectory && ex is IOException or UnauthorizedAccessException)
        {
            directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dotnet-updater", "logs");
            Path = CreateLog(directory);
        }
    }

    public void Write(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        lock (_gate) File.AppendAllText(Path, $"[{DateTimeOffset.UtcNow:O}] {Redactor.Redact(text)}{Environment.NewLine}");
    }

    private static string CreateLog(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, $"run-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
        using (File.Create(path)) { }
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }
}

public static partial class Redactor
{
    [GeneratedRegex("(?i)(https?://)([^/@\\s:]+):([^/@\\s]+)@")]
    private static partial Regex AuthenticatedUrl();
    [GeneratedRegex("(?i)(password|token|apikey|api_key)=([^&\\s]+)")]
    private static partial Regex QuerySecret();

    public static string Redact(string value) => QuerySecret().Replace(
        AuthenticatedUrl().Replace(value, "$1***:***@"), "$1=***");
}
