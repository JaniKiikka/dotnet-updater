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
    private readonly ConcurrentDictionary<string, Task<ResolutionSnapshot>> snapshots = new(PathComparer);

    public NuGetVersionService(IProcessRunner processRunner, IAllPackageVersionsSource? allVersionsSource = null)
    {
        this.processRunner = processRunner;
        this.allVersionsSource = allVersionsSource ?? new ConfiguredNuGetVersionSource();
    }

    public static int DefaultMaxConcurrency { get; } = Math.Clamp(Environment.ProcessorCount, 2, 8);

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

        var groups = sources.Select(source => new GroupInput(
            source.Key,
            source.ToImmutableArray(),
            source.Where(IsSupported).ToImmutableArray())).ToArray();
        var contextInputs = groups
            .SelectMany(group => group.Supported.Select(occurrence => (Occurrence: occurrence, group.PackageId)))
            .GroupBy(x => NormalizePath(x.Occurrence.ProjectPath), PathComparer)
            .Select(group => new ContextInput(
                group.Key,
                group.Select(x => x.PackageId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToImmutableArray()))
            .OrderBy(x => x.ProjectPath, PathComparer)
            .ToArray();
        var contextCounts = groups.ToDictionary(
            group => group.PackageId,
            group => group.Supported.Select(x => NormalizePath(x.ProjectPath)).Distinct(PathComparer).Count(),
            StringComparer.OrdinalIgnoreCase);
        var contexts = new ConcurrentDictionary<string, ContextResult>(PathComparer);

        await Parallel.ForEachAsync(
            contextInputs,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = maxConcurrency
            },
            async (context, token) =>
            {
                contexts[context.ProjectPath] = await LoadContextAsync(
                    context,
                    context.PackageIds.Where(packageId => contextCounts[packageId] > 1),
                    token).ConfigureAwait(false);
            }).ConfigureAwait(false);

        var resolved = ImmutableArray.CreateBuilder<PackageGroup>(groups.Length);
        for (var index = 0; index < groups.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var group = groups[index];
            resolved.Add(Resolve(group, contexts));
            progress?.Report($"Resolved {index + 1} of {groups.Length}: {group.PackageId}");
        }
        return resolved.ToImmutable();
    }

    public async Task<PackageGroup> ResolveAsync(
        IGrouping<string, PackageOccurrence> source,
        CancellationToken cancellationToken)
    {
        var result = await ResolveAllAsync([source], null, cancellationToken, DefaultMaxConcurrency)
            .ConfigureAwait(false);
        return result[0];
    }

    private async Task<ContextResult> LoadContextAsync(
        ContextInput context,
        IEnumerable<string> packagesNeedingAvailability,
        CancellationToken cancellationToken)
    {
        ResolutionSnapshot minor;
        ResolutionSnapshot major;
        try
        {
            minor = await GetSnapshotAsync(context.ProjectPath, QueryMode.HighestMinor, cancellationToken)
                .ConfigureAwait(false);
            major = await GetSnapshotAsync(context.ProjectPath, QueryMode.LatestMajor, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            var error = $"NuGet query failed for {context.ProjectPath}: {ex.Message}";
            minor = ResolutionSnapshot.Failed(error);
            major = ResolutionSnapshot.Failed(error);
        }

        var availability = ImmutableDictionary.CreateBuilder<string, PackageVersionLookup>(StringComparer.OrdinalIgnoreCase);
        foreach (var packageId in packagesNeedingAvailability)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                availability[packageId] = await allVersionsSource.GetAllAsync(
                    context.ProjectPath,
                    packageId,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                availability[packageId] = new([], ex.Message);
            }
        }
        return new(context.ProjectPath, minor, major, availability.ToImmutable());
    }

    private static PackageGroup Resolve(
        GroupInput group,
        IReadOnlyDictionary<string, ContextResult> contexts)
    {
        if (group.Supported.Length == 0)
            return new(group.PackageId, group.Occurrences, null, null, "No supported literal versions.");

        var occurrencesByContext = group.Supported
            .GroupBy(x => NormalizePath(x.ProjectPath), PathComparer)
            .OrderBy(x => x.Key, PathComparer)
            .ToArray();
        var errors = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var occurrenceContext in occurrencesByContext)
        {
            var result = contexts[occurrenceContext.Key];
            if (result.Minor.Error is not null) errors.Add(result.Minor.Error);
            if (result.Major.Error is not null) errors.Add(result.Major.Error);
        }

        var currentByMajor = group.Supported
            .Select(x => ParseCurrent(x.CurrentVersion))
            .GroupBy(x => x.Major)
            .ToDictionary(x => x.Key, x => x.OrderDescending().First());

        if (occurrencesByContext.Length == 1)
        {
            var context = contexts[occurrencesByContext[0].Key];
            var hasMinor = context.Minor.Packages.TryGetValue(group.PackageId, out var latestMinor);
            var singleMinorTargets = currentByMajor.ToImmutableDictionary(
                item => item.Key,
                item => hasMinor && latestMinor.Major == item.Key ? latestMinor : item.Value);
            var highestMajor = currentByMajor.Keys.Max();
            var majorTarget = context.Major.Packages.TryGetValue(group.PackageId, out var latestMajor)
                ? latestMajor
                : currentByMajor[highestMajor];
            return new(
                group.PackageId,
                group.Occurrences,
                singleMinorTargets[highestMajor],
                majorTarget,
                JoinErrors(errors))
            {
                LatestMinorByMajor = singleMinorTargets
            };
        }

        var availableByContext = new Dictionary<string, ImmutableHashSet<SemanticVersion>>(PathComparer);
        foreach (var occurrenceContext in occurrencesByContext)
        {
            var context = contexts[occurrenceContext.Key];
            if (!context.Availability.TryGetValue(group.PackageId, out var lookup))
            {
                errors.Add($"Version availability was not loaded for {occurrenceContext.Key}.");
                continue;
            }
            if (lookup.Error is not null)
                errors.Add($"{occurrenceContext.Key}: {lookup.Error}");
            availableByContext[occurrenceContext.Key] = lookup.Versions
                .Select(ParseOptional)
                .Where(x => x is { IsPrerelease: false })
                .Select(x => x!.Value)
                .ToImmutableHashSet();
        }

        if (availableByContext.Count != occurrencesByContext.Length ||
            occurrencesByContext.Any(x => availableByContext[x.Key].Count == 0))
            return new(group.PackageId, group.Occurrences, null, null, JoinErrors(errors) ??
                "No stable versions were returned by every NuGet resolution context.");

        var common = availableByContext.Values
            .Skip(1)
            .Aggregate(availableByContext.Values.First(),
                (result, versions) => result.Intersect(versions).ToImmutableHashSet());
        if (common.Count == 0)
        {
            errors.Add($"No common stable version exists across these NuGet resolution contexts: {string.Join("; ", occurrencesByContext.Select(x => x.Key))}.");
            return new(group.PackageId, group.Occurrences, null, null, JoinErrors(errors));
        }

        var minorTargets = ImmutableDictionary.CreateBuilder<int, SemanticVersion>();
        foreach (var major in currentByMajor.Keys.Order())
        {
            var affected = occurrencesByContext
                .Where(context => context.Any(x => ParseCurrent(x.CurrentVersion).Major == major))
                .ToArray();
            var candidates = affected
                .Select(context => availableByContext[context.Key])
                .Skip(1)
                .Aggregate(availableByContext[affected[0].Key],
                    (result, versions) => result.Intersect(versions).ToImmutableHashSet())
                .Where(version => version.Major == major)
                .Where(version => affected.All(context =>
                {
                    var hasSnapshot = contexts[context.Key].Minor.Packages.TryGetValue(group.PackageId, out var snapshot);
                    var fallback = context.Select(x => ParseCurrent(x.CurrentVersion)).Where(x => x.Major == major).Max();
                    var upperBound = hasSnapshot && snapshot.Major == major ? snapshot : fallback;
                    return version.CompareTo(upperBound) <= 0;
                }))
                .OrderDescending()
                .ToArray();
            if (candidates.Length > 0)
                minorTargets[major] = candidates[0];
            else
                errors.Add($"No common stable target exists for current major {major} across its NuGet resolution contexts.");
        }

        var majorCandidates = common
            .Where(version => occurrencesByContext.All(context =>
            {
                var hasSnapshot = contexts[context.Key].Major.Packages.TryGetValue(group.PackageId, out var snapshot);
                var fallback = context.Select(x => ParseCurrent(x.CurrentVersion)).Max();
                return version.CompareTo(hasSnapshot ? snapshot : fallback) <= 0;
            }))
            .OrderDescending()
            .ToArray();
        if (majorCandidates.Length == 0)
            errors.Add("No common stable target is compatible with every NuGet resolution context.");

        var highestCurrentMajor = currentByMajor.Keys.Max();
        return new(
            group.PackageId,
            group.Occurrences,
            minorTargets.GetValueOrDefault(highestCurrentMajor),
            majorCandidates.Length == 0 ? null : majorCandidates[0],
            JoinErrors(errors))
        {
            LatestMinorByMajor = minorTargets.ToImmutable()
        };
    }

    private Task<ResolutionSnapshot> GetSnapshotAsync(
        string projectPath,
        QueryMode mode,
        CancellationToken cancellationToken)
    {
        var key = $"{NormalizePath(projectPath)}|{mode}";
        return GetOrCreateSnapshotAsync(key, projectPath, mode, cancellationToken);
    }

    private async Task<ResolutionSnapshot> GetOrCreateSnapshotAsync(
        string key,
        string projectPath,
        QueryMode mode,
        CancellationToken cancellationToken)
    {
        var task = snapshots.GetOrAdd(key, _ => LookupSnapshotCoreAsync(projectPath, mode, cancellationToken));
        try
        {
            var result = await task.ConfigureAwait(false);
            if (result.Error is not null)
                snapshots.TryRemove(new KeyValuePair<string, Task<ResolutionSnapshot>>(key, task));
            return result;
        }
        catch
        {
            snapshots.TryRemove(new KeyValuePair<string, Task<ResolutionSnapshot>>(key, task));
            throw;
        }
    }

    private async Task<ResolutionSnapshot> LookupSnapshotCoreAsync(
        string projectPath,
        QueryMode mode,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "package", "list", "--project", projectPath, "--outdated", "--format", "json", "--no-restore"
        };
        if (mode == QueryMode.HighestMinor) arguments.Add("--highest-minor");
        var result = await processRunner.RunAsync(
            new("dotnet", arguments, Path.GetDirectoryName(projectPath)!),
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
            return ResolutionSnapshot.Failed($"NuGet query failed for {projectPath}; restore the project and verify its configured sources.");

        using var json = JsonDocument.Parse(result.StandardOutput);
        var packages = new Dictionary<string, List<SemanticVersion>>(StringComparer.OrdinalIgnoreCase);
        Walk(json.RootElement, packages);
        return new(packages
            .Select(item => new
            {
                item.Key,
                Latest = item.Value.Where(x => !x.IsPrerelease).OrderDescending().Cast<SemanticVersion?>().FirstOrDefault()
            })
            .Where(x => x.Latest is not null)
            .ToImmutableDictionary(x => x.Key, x => x.Latest!.Value, StringComparer.OrdinalIgnoreCase), null);
    }

    private static void Walk(JsonElement element, Dictionary<string, List<SemanticVersion>> packages)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            string? id = null;
            string? latest = null;
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("id") && property.Value.ValueKind == JsonValueKind.String)
                    id = property.Value.GetString();
                if (property.NameEquals("latestVersion") && property.Value.ValueKind == JsonValueKind.String)
                    latest = property.Value.GetString();
                Walk(property.Value, packages);
            }
            if (id is not null && SemanticVersion.TryParse(latest, out var parsed))
            {
                if (!packages.TryGetValue(id, out var versions)) packages[id] = versions = [];
                versions.Add(parsed);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray()) Walk(child, packages);
        }
    }

    private static bool IsSupported(PackageOccurrence occurrence) =>
        occurrence.UnsupportedReason is null && SemanticVersion.TryParse(occurrence.CurrentVersion, out _);

    private static SemanticVersion ParseCurrent(string value)
    {
        SemanticVersion.TryParse(value, out var version);
        return version;
    }

    private static SemanticVersion? ParseOptional(string value) =>
        SemanticVersion.TryParse(value, out var version) ? version : null;

    private static string NormalizePath(string path) => Path.GetFullPath(path);

    private static string? JoinErrors(IEnumerable<string> errors)
    {
        var values = errors.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray();
        return values.Length == 0 ? null : string.Join(" ", values);
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private enum QueryMode { HighestMinor, LatestMajor }

    private sealed record GroupInput(
        string PackageId,
        ImmutableArray<PackageOccurrence> Occurrences,
        ImmutableArray<PackageOccurrence> Supported);

    private sealed record ContextInput(string ProjectPath, ImmutableArray<string> PackageIds);

    private sealed record ContextResult(
        string ProjectPath,
        ResolutionSnapshot Minor,
        ResolutionSnapshot Major,
        ImmutableDictionary<string, PackageVersionLookup> Availability);

    private sealed record ResolutionSnapshot(
        ImmutableDictionary<string, SemanticVersion> Packages,
        string? Error)
    {
        public static ResolutionSnapshot Failed(string error) =>
            new(ImmutableDictionary<string, SemanticVersion>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase), error);
    }
}
