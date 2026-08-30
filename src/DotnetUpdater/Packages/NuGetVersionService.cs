using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using DotnetUpdater.Domain;
using DotnetUpdater.Execution;

namespace DotnetUpdater.Packages;

public sealed class NuGetVersionService(IProcessRunner processRunner)
{
    private readonly ConcurrentDictionary<string, Task<PackageVersions>> _cache = new(StringComparer.OrdinalIgnoreCase);

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
