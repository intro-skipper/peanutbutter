using System.Diagnostics.CodeAnalysis;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.PeanutButter.Configuration;

/// <summary>
/// Configuration for the plugin installer.
/// </summary>
public sealed class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the GitHub repositories plugins have been installed from. An
    /// array-with-setter is required: Jellyfin persists this type with XmlSerializer and
    /// its configuration API rehydrates it from JSON, and a get-only collection would be
    /// silently dropped on the JSON leg.
    /// </summary>
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "XmlSerializer + System.Text.Json round-trip contract")]
    public GitHubSource[] GitHubSources { get; set; } = [];
}
