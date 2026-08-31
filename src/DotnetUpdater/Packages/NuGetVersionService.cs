using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using DotnetUpdater.Domain;
using DotnetUpdater.Execution;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGetVersion = NuGet.Versioning.NuGetVersion;
using NuGetVersionComparer = NuGet.Versioning.VersionComparer;

namespace DotnetUpdater.Packages;

public sealed record PackageVersionLookup(ImmutableArray<string> Versions, string? Error);

public interface IAllPackageVersionsSource
{
    Task<PackageVersionLookup> GetAllAsync(string projectPath, string packageId, CancellationToken cancellationToken);
}

public sealed class ConfiguredNuGetVersionSource : IAllPackageVersionsSource
{
    public async Task<PackageVersionLookup> GetAllAsync(
        string projectPath,
        string packageId,
        CancellationToken cancellationToken)
    {
        var settings = Settings.LoadDefaultSettings(Path.GetDirectoryName(projectPath));
        var sourceProvider = new PackageSourceProvider(settings);
        var repositories = new SourceRepositoryProvider(sourceProvider, Repository.Provider.GetCoreV3())
            .GetRepositories()
            .ToArray();
        var versions = new HashSet<NuGetVersion>(NuGetVersionComparer.VersionReleaseMetadata);
        var errors = new List<string>();
        var successfulSources = 0;

        using var cache = new SourceCacheContext();
        foreach (var repository in repositories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var resource = await repository.GetResourceAsync<FindPackageByIdResource>(cancellationToken)
                    .ConfigureAwait(false);
                if (resource is null)
                    throw new InvalidOperationException("The source does not support package version lookup.");
                var found = await resource.GetAllVersionsAsync(
                    packageId,
                    cache,
                    NullLogger.Instance,
                    cancellationToken).ConfigureAwait(false);
                versions.UnionWith(found);
                successfulSources++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"{repository.PackageSource.Name}: {ex.Message}");
            }
        }

        var ordered = versions
            .OrderByDescending(x => x, NuGetVersionComparer.VersionReleaseMetadata)
            .Select(x => x.ToNormalizedString())
            .ToImmutableArray();
        var error = successfulSources == 0
            ? errors.Count == 0
                ? "No enabled NuGet sources are configured for this project."
                : $"All configured NuGet sources failed: {string.Join("; ", errors)}"
            : null;
        return new(ordered, error);
    }
}

public sealed class NuGetVersionService
{
    private readonly IProcessRunner processRunner;
    private readonly IAllPackageVersionsSource allVersionsSource;

    public NuGetVersionService(IProcessRunner processRunner, IAllPackageVersionsSource? allVersionsSource = null)
    {
        this.processRunner = processRunner;
        this.allVersionsSource = allVersionsSource ?? new ConfiguredNuGetVersionSource();
    }

    public static int DefaultMaxConcurrency { get; } = Math.Clamp(Environment.ProcessorCount, 2, 8);

    private readonly ConcurrentDictionary<string, Task<PackageVersions>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public Task<PackageVersionLookup> GetAllVersionsAsync(
        string projectPath,
        string packageId,
        CancellationToken cancellationToken) =>
        allVersionsSource.GetAllAsync(projectPath, packageId, cancellationToken);

    public Task<ImmutableArray<PackageGroup>> ResolveAllAsync(
        IReadOnlyList<IGrouping<string, PackageOccurrence>> sources,
        IProgress<string>? progress,
        CancellationToken cancellationToken) =>
        ResolveAllAsync(sources, progress, cancellationToken, DefaultMaxConcurrency);

    public async Task<ImmutableArray<PackageGroup>> ResolveAllAsync(
        IReadOnlyList<IGrouping<string, PackageOccurrence>> sources,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        int maxConcurrency)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);

        var resolved = new PackageGroup[sources.Count];
        var completed = 0;
        var progressGate = new object();
        await Parallel.ForEachAsync(
            Enumerable.Range(0, sources.Count),
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = maxConcurrency
            },
            async (index, token) =>
            {
                var source = sources[index];
                resolved[index] = await ResolveAsync(source, token).ConfigureAwait(false);
                lock (progressGate)
                {
                    completed++;
                    progress?.Report($"Resolved {completed} of {sources.Count}: {source.Key}");
                }
            }).ConfigureAwait(false);

        return resolved.ToImmutableArray();
    }

    public async Task<PackageGroup> ResolveAsync(IGrouping<string, PackageOccurrence> source, CancellationToken cancellationToken)
    {
        var occurrences = source.ToImmutableArray();
        var supported = occurrences.Where(x => x.UnsupportedReason is null && SemanticVersion.TryParse(x.CurrentVersion, out _)).ToArray();
        if (supported.Length == 0) return new(source.Key, occurrences, null, null, "No supported literal versions.");
        var baseline = supported.MaxBy(x => { SemanticVersion.TryParse(x.CurrentVersion, out var v); return v.Major; })!;
        try
        {
            var minor = await LookupAsync(baseline.ProjectPath, source.Key, true, cancellationToken);
            var major = await LookupAsync(baseline.ProjectPath, source.Key, false, cancellationToken);
            SemanticVersion.TryParse(baseline.CurrentVersion, out var current);
            var minorTarget = minor.Latest ?? current;
            var maxCurrent = supported.Select(x => { SemanticVersion.TryParse(x.CurrentVersion, out var v); return v; }).Max();
            var majorTarget = major.Latest ?? maxCurrent;
            return new(source.Key, occurrences,
                minorTarget.Major == current.Major ? minorTarget : current,
                majorTarget.CompareTo(maxCurrent) >= 0 ? majorTarget : maxCurrent,
                minor.Error ?? major.Error);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return new(source.Key, occurrences, null, null, ex.Message);
        }
    }

    private Task<PackageVersions> LookupAsync(string project, string packageId, bool highestMinor, CancellationToken cancellationToken) =>
        _cache.GetOrAdd($"{project}|{packageId}|{highestMinor}", _ => LookupCoreAsync(project, packageId, highestMinor, cancellationToken));

    private async Task<PackageVersions> LookupCoreAsync(string project, string packageId, bool highestMinor, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "package", "list", "--project", project, "--outdated", "--format", "json", "--no-restore" };
        if (highestMinor) arguments.Add("--highest-minor");
        var result = await processRunner.RunAsync(new("dotnet", arguments, Path.GetDirectoryName(project)!), cancellationToken);
        if (!result.Succeeded) return new(null, "NuGet query failed; restore the project and verify its configured sources.");
        using var json = JsonDocument.Parse(result.StandardOutput);
        var versions = new List<SemanticVersion>();
        Walk(json.RootElement, packageId, versions);
        var stable = versions.Where(x => !x.IsPrerelease).OrderDescending().ToArray();
        return new(stable.Length == 0 ? null : stable[0], null);
    }

    private static void Walk(JsonElement element, string packageId, List<SemanticVersion> versions)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            string? id = null;
            string? latest = null;
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("id") && property.Value.ValueKind == JsonValueKind.String) id = property.Value.GetString();
                if (property.NameEquals("latestVersion") && property.Value.ValueKind == JsonValueKind.String) latest = property.Value.GetString();
                Walk(property.Value, packageId, versions);
            }
            if (string.Equals(id, packageId, StringComparison.OrdinalIgnoreCase) && SemanticVersion.TryParse(latest, out var parsed)) versions.Add(parsed);
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) Walk(child, packageId, versions);
    }

    private sealed record PackageVersions(SemanticVersion? Latest, string? Error);
}
