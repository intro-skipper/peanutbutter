using Jellyfin.Plugin.PeanutButter.Configuration;
using Jellyfin.Plugin.PeanutButter.Services.GitHub;
using Xunit;

namespace Jellyfin.Plugin.PeanutButter.Tests.GitHub;

public sealed class GitHubSourceStoreTests
{
    [Fact]
    public void Upsert_IntoEmptyConfiguration_Appends()
    {
        var configuration = new PluginConfiguration();

        GitHubSourceStore.Upsert(configuration, MakeSource("owner", "repo"));

        var stored = Assert.Single(configuration.GitHubSources);
        Assert.Equal("owner", stored.Owner);
    }

    [Fact]
    public void Upsert_SameRepositoryCaseInsensitive_Replaces()
    {
        var configuration = new PluginConfiguration();
        GitHubSourceStore.Upsert(configuration, MakeSource("Owner", "Repo", version: "1.0.0.0"));

        GitHubSourceStore.Upsert(configuration, MakeSource("owner", "repo", version: "2.0.0.0"));

        var stored = Assert.Single(configuration.GitHubSources);
        Assert.Equal("2.0.0.0", stored.Version);
    }

    [Fact]
    public void Upsert_SamePluginIdFromDifferentRepo_Replaces()
    {
        var pluginId = Guid.NewGuid();
        var configuration = new PluginConfiguration();
        GitHubSourceStore.Upsert(configuration, MakeSource("original", "repo", pluginId: pluginId));

        GitHubSourceStore.Upsert(configuration, MakeSource("fork", "renamed", pluginId: pluginId));

        var stored = Assert.Single(configuration.GitHubSources);
        Assert.Equal("fork", stored.Owner);
    }

    [Fact]
    public void Upsert_UnknownPluginIds_FallBackToFolderNameMatch()
    {
        var configuration = new PluginConfiguration();
        GitHubSourceStore.Upsert(configuration, MakeSource("original", "repo", folderName: "MyPlugin"));

        GitHubSourceStore.Upsert(configuration, MakeSource("fork", "other", folderName: "myplugin"));

        var stored = Assert.Single(configuration.GitHubSources);
        Assert.Equal("fork", stored.Owner);
    }

    [Fact]
    public void Upsert_UnrelatedEntries_ArePreserved()
    {
        var configuration = new PluginConfiguration();
        GitHubSourceStore.Upsert(configuration, MakeSource("owner", "first", folderName: "First", pluginId: Guid.NewGuid()));
        GitHubSourceStore.Upsert(configuration, MakeSource("owner", "second", folderName: "Second", pluginId: Guid.NewGuid()));

        Assert.Equal(2, configuration.GitHubSources.Length);
    }

    [Fact]
    public void FindByRepo_MatchesCaseInsensitive()
    {
        var configuration = new PluginConfiguration();
        GitHubSourceStore.Upsert(configuration, MakeSource("Owner", "Repo"));

        Assert.NotNull(GitHubSourceStore.FindByRepo(configuration, "owner", "repo"));
        Assert.Null(GitHubSourceStore.FindByRepo(configuration, "owner", "other"));
    }

    [Fact]
    public void Remove_RemovesOnlyMatchingRepository()
    {
        var configuration = new PluginConfiguration();
        GitHubSourceStore.Upsert(configuration, MakeSource("owner", "first", folderName: "First", pluginId: Guid.NewGuid()));
        GitHubSourceStore.Upsert(configuration, MakeSource("owner", "second", folderName: "Second", pluginId: Guid.NewGuid()));

        Assert.True(GitHubSourceStore.Remove(configuration, "OWNER", "FIRST"));
        Assert.False(GitHubSourceStore.Remove(configuration, "owner", "first"));

        var remaining = Assert.Single(configuration.GitHubSources);
        Assert.Equal("second", remaining.Repo);
    }

    private static GitHubSource MakeSource(
        string owner,
        string repo,
        string version = "1.0.0.0",
        string folderName = "Plugin",
        Guid pluginId = default)
        => new()
        {
            Owner = owner,
            Repo = repo,
            Version = version,
            FolderName = folderName,
            PluginId = pluginId,
            PluginName = "Plugin",
            TagName = "v" + version,
            AssetId = 1,
            AssetName = "plugin.zip",
            InstalledAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
}
