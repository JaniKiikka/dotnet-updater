using System.Collections.Immutable;
using System.Text.Json;

namespace DotnetUpdater.Configuration;

public sealed record AppConfiguration(
    string ProjectsFolder,
    ImmutableArray<string> IgnoredPackages,
    string DevelopmentBranch,
    string RemoteName)
{
    public static AppConfiguration Default(string projectsFolder = "") =>
        new(projectsFolder, [], "development", "origin");
}

public sealed record ConfigurationLoadResult(AppConfiguration Configuration, string? Warning);

public interface IConfigurationPathProvider { string GetPath(); }

public sealed class UserConfigurationPathProvider : IConfigurationPathProvider
{
    public string GetPath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(root, "dotnet-updater", "settings.json");
    }
}

public interface IConfigurationStore
{
    Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(AppConfiguration configuration, CancellationToken cancellationToken);
}

public sealed class JsonConfigurationStore(IConfigurationPathProvider pathProvider) : IConfigurationStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken)
    {
        var path = pathProvider.GetPath();
        if (!File.Exists(path)) return new(AppConfiguration.Default(), null);
        try
        {
            await using var stream = File.OpenRead(path);
            var value = await JsonSerializer.DeserializeAsync<AppConfiguration>(stream, Options, cancellationToken);
            if (value is null) return new(AppConfiguration.Default(), $"Configuration at {path} was empty.");
            return new(Normalize(value), null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new(AppConfiguration.Default(), $"Could not read {path}: {ex.Message}");
        }
    }

    public async Task SaveAsync(AppConfiguration configuration, CancellationToken cancellationToken)
    {
        var path = pathProvider.GetPath();
        var directory = Path.GetDirectoryName(path) ?? throw new IOException("Configuration path has no directory.");
        Directory.CreateDirectory(directory);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(stream, Normalize(configuration), Options, cancellationToken);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static AppConfiguration Normalize(AppConfiguration configuration)
    {
        var ignored = configuration.IgnoredPackages
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        return configuration with
        {
            ProjectsFolder = configuration.ProjectsFolder?.Trim() ?? string.Empty,
            IgnoredPackages = ignored,
            DevelopmentBranch = string.IsNullOrWhiteSpace(configuration.DevelopmentBranch) ? "development" : configuration.DevelopmentBranch.Trim(),
            RemoteName = string.IsNullOrWhiteSpace(configuration.RemoteName) ? "origin" : configuration.RemoteName.Trim()
        };
    }
}
