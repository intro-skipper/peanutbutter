namespace Jellyfin.Plugin.PeanutButter.Configuration;

/// <summary>
/// Records where a plugin installed through the GitHub flow came from, so the
/// administrator can check that repository for newer releases later. This type must
/// round-trip through BOTH <see cref="System.Xml.Serialization.XmlSerializer"/> (Jellyfin's
/// configuration persistence) and System.Text.Json (Jellyfin's plugin-configuration API
/// replaces the whole object from a JSON body) — keep it a plain POCO with public settable
/// properties of primitive types.
/// </summary>
public sealed class GitHubSource
{
    /// <summary>Gets or sets the installed plugin's GUID; <see cref="Guid.Empty"/> when the archive supplied none.</summary>
    public Guid PluginId { get; set; }

    /// <summary>
    /// Gets or sets the plugin's base folder name WITHOUT the trailing version suffix
    /// (installs land in versioned folders such as <c>Name_1.2.3.4</c>, so the versioned
    /// name would change on every update and break identity matching).
    /// </summary>
    public string FolderName { get; set; } = string.Empty;

    /// <summary>Gets or sets the human-readable plugin name.</summary>
    public string PluginName { get; set; } = string.Empty;

    /// <summary>Gets or sets the GitHub repository owner.</summary>
    public string Owner { get; set; } = string.Empty;

    /// <summary>Gets or sets the GitHub repository name.</summary>
    public string Repo { get; set; } = string.Empty;

    /// <summary>Gets or sets the release tag the plugin was installed from.</summary>
    public string TagName { get; set; } = string.Empty;

    /// <summary>Gets or sets GitHub's identifier of the installed asset.</summary>
    public long AssetId { get; set; }

    /// <summary>Gets or sets the file name of the installed asset.</summary>
    public string AssetName { get; set; } = string.Empty;

    /// <summary>Gets or sets the installed plugin version as reported by the installer.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Gets or sets the lowercase hex SHA-256 of the downloaded asset; empty when unknown.</summary>
    public string Sha256Digest { get; set; } = string.Empty;

    /// <summary>Gets or sets when the install happened, in UTC.</summary>
    public DateTime InstalledAtUtc { get; set; }
}
