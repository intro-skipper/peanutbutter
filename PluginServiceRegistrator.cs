using System.Net;
using Jellyfin.Plugin.PeanutButter.Services.GitHub;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.PeanutButter;

/// <summary>
/// Registers this plugin's services in Jellyfin's dependency injection container at server
/// startup. The body must never throw: a throwing registrator marks the whole plugin as
/// malfunctioned and disables it.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <summary>
    /// Registers <see cref="GitHubReleaseClient"/> as a typed HTTP client whose handler is
    /// locked down for the GitHub download flow: automatic redirects are disabled so every
    /// hop can be validated against the GitHub host allowlist, and no cookies or ambient
    /// credentials are ever attached. (If DI registration were ever unavailable, the
    /// fallback is a lazily created static <see cref="HttpClient"/> over the same handler.)
    /// </summary>
    /// <param name="serviceCollection">The server's service collection.</param>
    /// <param name="applicationHost">The server application host.</param>
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection
            .AddHttpClient<GitHubReleaseClient>()
            .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                PooledConnectionLifetime = TimeSpan.FromMinutes(15),
                UseCookies = false,
                Credentials = null
            });
    }
}
