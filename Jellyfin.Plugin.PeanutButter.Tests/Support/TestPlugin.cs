using Jellyfin.Plugin.PeanutButter.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.PeanutButter.Tests.Support;

/// <summary>
/// A second plugin assembly used as a safe, non-self payload for installer tests.
/// </summary>
public sealed class TestPlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
    : BasePlugin<PluginConfiguration>(applicationPaths, xmlSerializer)
{
    /// <inheritdoc />
    public override string Name => "Test Plugin";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("99999999-8888-7777-6666-555555555555");
}
