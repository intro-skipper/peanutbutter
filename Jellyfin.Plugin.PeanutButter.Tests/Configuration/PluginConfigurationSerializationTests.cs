using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Serialization;
using Jellyfin.Plugin.PeanutButter.Configuration;
using Xunit;

namespace Jellyfin.Plugin.PeanutButter.Tests.Configuration;

/// <summary>
/// Enforces the serializer contract on <see cref="PluginConfiguration"/>: Jellyfin persists
/// it with XmlSerializer and rehydrates it from JSON through the plugin-configuration API,
/// so the whole object graph must survive both round trips. If a change to the
/// configuration types breaks one of these tests, the change loses admin data in production.
/// </summary>
public sealed class PluginConfigurationSerializationTests
{
    [Fact]
    public void XmlSerializer_RoundTripsGitHubSources()
    {
        var original = MakeConfiguration();
        var serializer = new XmlSerializer(typeof(PluginConfiguration));

        using var buffer = new MemoryStream();
        serializer.Serialize(buffer, original);
        buffer.Position = 0;
        using var reader = XmlReader.Create(buffer, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var restored = Assert.IsType<PluginConfiguration>(serializer.Deserialize(reader));

        AssertConfigurationsEqual(original, restored);
    }

    [Fact]
    public void SystemTextJson_RoundTripsGitHubSources()
    {
        var original = MakeConfiguration();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var json = JsonSerializer.Serialize(original, options);
        var restored = JsonSerializer.Deserialize<PluginConfiguration>(json, options);

        Assert.NotNull(restored);
        AssertConfigurationsEqual(original, restored);
    }

    [Fact]
    public void XmlSerializedForm_IsReadableText()
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var buffer = new MemoryStream();
        serializer.Serialize(buffer, MakeConfiguration());

        var xml = Encoding.UTF8.GetString(buffer.ToArray());
        Assert.Contains("GitHubSources", xml, StringComparison.Ordinal);
        Assert.Contains("intro-skipper", xml, StringComparison.Ordinal);
    }

    private static PluginConfiguration MakeConfiguration()
        => new()
        {
            GitHubSources =
            [
                new GitHubSource
                {
                    PluginId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
                    FolderName = "Intro Skipper",
                    PluginName = "Intro Skipper",
                    Owner = "intro-skipper",
                    Repo = "intro-skipper",
                    TagName = "12.0/v1.12.0.1",
                    AssetId = 42,
                    AssetName = "intro-skipper-v1.12.0.1.zip",
                    Version = "1.12.0.1",
                    Sha256Digest = new string('a', 64),
                    InstalledAtUtc = new DateTime(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc)
                },
                new GitHubSource
                {
                    FolderName = "Other",
                    PluginName = "Other",
                    Owner = "someone",
                    Repo = "other-plugin",
                    TagName = "v2.0",
                    AssetId = 7,
                    AssetName = "other.dll",
                    Version = "2.0.0.0",
                    InstalledAtUtc = new DateTime(2026, 7, 1, 8, 30, 0, DateTimeKind.Utc)
                }
            ]
        };

    private static void AssertConfigurationsEqual(PluginConfiguration expected, PluginConfiguration actual)
    {
        Assert.Equal(expected.GitHubSources.Length, actual.GitHubSources.Length);
        for (var index = 0; index < expected.GitHubSources.Length; index++)
        {
            var left = expected.GitHubSources[index];
            var right = actual.GitHubSources[index];
            Assert.Equal(left.PluginId, right.PluginId);
            Assert.Equal(left.FolderName, right.FolderName);
            Assert.Equal(left.PluginName, right.PluginName);
            Assert.Equal(left.Owner, right.Owner);
            Assert.Equal(left.Repo, right.Repo);
            Assert.Equal(left.TagName, right.TagName);
            Assert.Equal(left.AssetId, right.AssetId);
            Assert.Equal(left.AssetName, right.AssetName);
            Assert.Equal(left.Version, right.Version);
            Assert.Equal(left.Sha256Digest, right.Sha256Digest);
            Assert.Equal(left.InstalledAtUtc, right.InstalledAtUtc);
        }
    }
}
