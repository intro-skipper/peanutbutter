using Jellyfin.Plugin.PeanutButter.Configuration;

namespace Jellyfin.Plugin.PeanutButter.Services.GitHub;

/// <summary>
/// Pure mutation logic for the tracked GitHub sources in <see cref="PluginConfiguration"/>.
/// Callers that persist the result must hold <see cref="SyncRoot"/> around the mutation and
/// the save so concurrent admin actions cannot interleave a read-modify-write.
/// </summary>
internal static class GitHubSourceStore
{
    /// <summary>Serializes configuration mutation + save sequences.</summary>
    internal static readonly Lock SyncRoot = new();

    /// <summary>
    /// Inserts or replaces the record for an installed source. Any entry for the same
    /// repository (case-insensitive owner/repo) or for the same installed plugin (matching
    /// GUID when both are known, otherwise matching folder name) is replaced — one
    /// installed plugin keeps exactly one source, and the most recent install wins.
    /// </summary>
    internal static void Upsert(PluginConfiguration configuration, GitHubSource source)
    {
        var retained = configuration.GitHubSources
            .Where(existing => !IsSameRepository(existing, source.Owner, source.Repo)
                && !IsSamePlugin(existing, source))
            .ToList();
        retained.Add(source);
        configuration.GitHubSources = [.. retained];
    }

    /// <summary>Finds the tracked record for a repository, if any.</summary>
    internal static GitHubSource? FindByRepo(PluginConfiguration configuration, string owner, string repo)
        => configuration.GitHubSources.FirstOrDefault(source => IsSameRepository(source, owner, repo));

    /// <summary>Removes the tracked record for a repository.</summary>
    /// <returns>Whether a record was removed.</returns>
    internal static bool Remove(PluginConfiguration configuration, string owner, string repo)
    {
        var retained = configuration.GitHubSources
            .Where(source => !IsSameRepository(source, owner, repo))
            .ToArray();
        if (retained.Length == configuration.GitHubSources.Length)
        {
            return false;
        }

        configuration.GitHubSources = retained;
        return true;
    }

    private static bool IsSameRepository(GitHubSource source, string owner, string repo)
        => string.Equals(source.Owner, owner, StringComparison.OrdinalIgnoreCase)
            && string.Equals(source.Repo, repo, StringComparison.OrdinalIgnoreCase);

    private static bool IsSamePlugin(GitHubSource existing, GitHubSource candidate)
    {
        if (existing.PluginId != Guid.Empty && candidate.PluginId != Guid.Empty)
        {
            return existing.PluginId == candidate.PluginId;
        }

        return !string.IsNullOrEmpty(existing.FolderName)
            && string.Equals(existing.FolderName, candidate.FolderName, StringComparison.OrdinalIgnoreCase);
    }
}
