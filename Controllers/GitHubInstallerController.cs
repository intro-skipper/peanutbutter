using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Jellyfin.Plugin.PeanutButter.Configuration;
using Jellyfin.Plugin.PeanutButter.Services;
using Jellyfin.Plugin.PeanutButter.Services.GitHub;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PeanutButter.Controllers;

/// <summary>
/// Installs and updates Jellyfin plugins directly from GitHub releases, admin-initiated
/// only: an administrator resolves a release, picks an asset, and the server downloads it
/// through <see cref="GitHubReleaseClient"/> into the same staged validation pipeline the
/// upload endpoint uses. Installed sources are recorded so releases can be re-checked for
/// updates on demand — there is no background polling and no automatic updating.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("Plugins/PeanutButter/GitHub")]
public sealed partial class GitHubInstallerController : ControllerBase
{
    private readonly GitHubReleaseClient _gitHubClient;
    private readonly PluginZipInstaller _installer;
    private readonly ILogger<GitHubInstallerController> _logger;
    private readonly IApplicationHost _applicationHost;
    private readonly string _downloadDirectory;

    /// <summary>
    /// Initializes a new instance of the <see cref="GitHubInstallerController"/> class.
    /// </summary>
    public GitHubInstallerController(
        GitHubReleaseClient gitHubClient,
        IApplicationPaths applicationPaths,
        ILoggerFactory loggerFactory,
        IApplicationHost applicationHost)
    {
        _gitHubClient = gitHubClient;
        _logger = loggerFactory.CreateLogger<GitHubInstallerController>();
        _applicationHost = applicationHost;
        _installer = new PluginZipInstaller(
            applicationPaths.PluginsPath,
            Path.Combine(applicationPaths.TempDirectory, "peanutbutter-installer-staging"),
            loggerFactory.CreateLogger<PluginZipInstaller>());
        _downloadDirectory = Path.Combine(applicationPaths.TempDirectory, "peanutbutter");
    }

    /// <summary>
    /// Resolves a GitHub release so the administrator can pick an asset to install.
    /// </summary>
    /// <param name="request">The repository reference and optional tag.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The release with its assets and a recommended asset when unambiguous.</returns>
    [HttpPost("Resolve")]
    [ProducesResponseType(typeof(GitHubReleaseInfo), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<GitHubReleaseInfo>> Resolve(
        [FromBody] ResolveGitHubReleaseRequest request,
        CancellationToken cancellationToken)
    {
        if (!GitHubReleaseLogic.TryParseRepository(request.Repository, out var owner, out var repo))
        {
            return GitHubSourceRejected("Enter a repository as 'owner/repo' or paste its github.com URL.");
        }

        var tag = string.IsNullOrWhiteSpace(request.Tag) ? null : request.Tag.Trim();
        if (tag is not null && !GitHubReleaseLogic.IsValidTag(tag))
        {
            return GitHubSourceRejected("The release tag contains unsupported characters.");
        }

        try
        {
            var release = await _gitHubClient.GetReleaseAsync(owner, repo, tag, cancellationToken)
                .ConfigureAwait(false);
            return Ok(ToReleaseInfo(owner, repo, release));
        }
        catch (GitHubSourceException exception)
        {
            return MapGitHubFailure(exception, owner, repo);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new EmptyResult();
        }
    }

    /// <summary>
    /// Downloads a resolved release asset and installs it through the staged validation
    /// pipeline. The release is re-fetched by tag and the asset must belong to it, so a
    /// stale or forged asset id cannot smuggle other content in.
    /// </summary>
    /// <param name="request">The pinned coordinates from a prior resolve.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The installation result.</returns>
    [HttpPost("Install")]
    [ProducesResponseType(typeof(GitHubInstallResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<GitHubInstallResult>> Install(
        [FromBody] InstallGitHubAssetRequest request,
        CancellationToken cancellationToken)
    {
        if (!GitHubReleaseLogic.IsValidOwner(request.Owner)
            || !GitHubReleaseLogic.IsValidRepo(request.Repo)
            || !GitHubReleaseLogic.IsValidTag(request.Tag))
        {
            return GitHubSourceRejected("The GitHub owner, repository, or tag is not valid.");
        }

        try
        {
            var release = await _gitHubClient.GetReleaseAsync(request.Owner, request.Repo, request.Tag, cancellationToken)
                .ConfigureAwait(false);
            var asset = release.Assets.FirstOrDefault(candidate => candidate.Id == request.AssetId);
            if (asset is null)
            {
                return Problem(
                    title: "GitHub release not found",
                    detail: $"The release '{release.TagName}' has no asset with id {request.AssetId}. Resolve the release again.",
                    statusCode: StatusCodes.Status404NotFound);
            }

            if (GitHubReleaseLogic.ClassifyAsset(asset.Name) == GitHubAssetKind.Other)
            {
                return GitHubSourceRejected($"The asset '{asset.Name}' is not a plugin ZIP or DLL.");
            }

            await using var downloaded = await _gitHubClient.DownloadAssetAsync(
                request.Owner,
                request.Repo,
                asset,
                _downloadDirectory,
                cancellationToken).ConfigureAwait(false);

            PluginInstallResult result;
            await using (var content = downloaded.OpenRead())
            {
                result = asset.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    ? await _installer.InstallDllAsync(
                        content,
                        asset.Name,
                        downloaded.Length,
                        request.ConfirmOlderVersion,
                        cancellationToken).ConfigureAwait(false)
                    : await _installer.InstallAsync(
                        content,
                        asset.Name,
                        downloaded.Length,
                        request.ConfirmOlderVersion,
                        cancellationToken).ConfigureAwait(false);
            }

            RecordInstalledSource(request, release.TagName, asset, downloaded, result);
            _applicationHost.NotifyPendingRestart();
            LogGitHubInstallComplete(_logger, result.Action, result.Name, result.Version, request.Owner, request.Repo, release.TagName);
            return Ok(new GitHubInstallResult
            {
                Install = result,
                DigestVerified = downloaded.DigestVerified
            });
        }
        catch (GitHubSourceException exception)
        {
            return MapGitHubFailure(exception, request.Owner, request.Repo);
        }
        catch (PluginDowngradeException exception)
        {
            LogDowngradeConfirmationRequired(_logger, exception, request.Owner, request.Repo);
            var problem = new ProblemDetails
            {
                Title = "Older plugin version requires confirmation",
                Detail = exception.Message,
                Status = StatusCodes.Status409Conflict
            };
            problem.Extensions["requiresConfirmation"] = true;
            problem.Extensions["installedVersion"] = exception.InstalledVersion;
            problem.Extensions["requestedVersion"] = exception.RequestedVersion;
            return Conflict(problem);
        }
        catch (PluginArchiveException exception)
        {
            LogRejectedGitHubInstall(_logger, exception, request.Owner, request.Repo);
            return BadRequest(new ProblemDetails
            {
                Title = "Plugin archive rejected",
                Detail = exception.Message,
                Status = StatusCodes.Status400BadRequest
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new EmptyResult();
        }
        catch (Exception exception)
        {
            LogUnexpectedGitHubError(_logger, exception, request.Owner, request.Repo);
            return StatusCode(StatusCodes.Status500InternalServerError, new ProblemDetails
            {
                Title = "Plugin installation failed",
                Detail = "The server could not complete the plugin installation. Check the Jellyfin log for details.",
                Status = StatusCodes.Status500InternalServerError
            });
        }
    }

    /// <summary>
    /// Lists the GitHub repositories plugins have been installed from.
    /// </summary>
    /// <returns>The tracked sources.</returns>
    [HttpGet("Sources")]
    [ProducesResponseType(typeof(IReadOnlyList<GitHubSource>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<GitHubSource>> GetSources()
        => Ok(Plugin.Instance?.Configuration.GitHubSources ?? []);

    /// <summary>
    /// Checks one tracked source for a newer release. This contacts GitHub, which is why it
    /// is a POST; nothing is installed until the administrator explicitly installs.
    /// </summary>
    /// <param name="request">The tracked repository to check.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The comparison between the installed and latest release.</returns>
    [HttpPost("CheckUpdate")]
    [ProducesResponseType(typeof(GitHubUpdateCheckResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<GitHubUpdateCheckResult>> CheckUpdate(
        [FromBody] CheckGitHubUpdateRequest request,
        CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration;
        var source = configuration is null
            ? null
            : GitHubSourceStore.FindByRepo(configuration, request.Owner, request.Repo);
        if (source is null)
        {
            return Problem(
                title: "Unknown GitHub source",
                detail: $"No plugin is tracked from '{request.Owner}/{request.Repo}'.",
                statusCode: StatusCodes.Status404NotFound);
        }

        try
        {
            var latest = await _gitHubClient.GetReleaseAsync(source.Owner, source.Repo, null, cancellationToken)
                .ConfigureAwait(false);
            var matchingAsset = latest.Assets.FirstOrDefault(asset =>
                string.Equals(asset.Name, source.AssetName, StringComparison.OrdinalIgnoreCase));
            if (matchingAsset is null)
            {
                var recommendedId = GitHubReleaseLogic.SelectRecommendedAsset(latest.Assets, source.Repo);
                matchingAsset = latest.Assets.FirstOrDefault(asset => asset.Id == recommendedId);
            }
            var decision = GitHubReleaseLogic.DetermineUpdate(
                source.Version,
                source.TagName,
                source.Sha256Digest,
                latest.TagName,
                matchingAsset?.Digest);
            return Ok(new GitHubUpdateCheckResult
            {
                Owner = source.Owner,
                Repo = source.Repo,
                InstalledTag = source.TagName,
                InstalledVersion = source.Version,
                UpdateAvailable = decision.UpdateAvailable,
                ComparisonMethod = decision.ComparisonMethod,
                Latest = ToReleaseInfo(source.Owner, source.Repo, latest)
            });
        }
        catch (GitHubSourceException exception)
        {
            return MapGitHubFailure(exception, source.Owner, source.Repo);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new EmptyResult();
        }
    }

    /// <summary>
    /// Stops tracking a repository. The installed plugin itself is not touched.
    /// </summary>
    /// <param name="owner">The repository owner.</param>
    /// <param name="repo">The repository name.</param>
    /// <returns>No content on success.</returns>
    [HttpDelete("Sources")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public ActionResult RemoveSource([FromQuery][Required] string owner, [FromQuery][Required] string repo)
    {
        var plugin = Plugin.Instance;
        if (plugin is not null)
        {
            lock (GitHubSourceStore.SyncRoot)
            {
                if (GitHubSourceStore.Remove(plugin.Configuration, owner, repo))
                {
                    plugin.SaveConfiguration();
                    return NoContent();
                }
            }
        }

        return Problem(
            title: "Unknown GitHub source",
            detail: $"No plugin is tracked from '{owner}/{repo}'.",
            statusCode: StatusCodes.Status404NotFound);
    }

    private static GitHubReleaseInfo ToReleaseInfo(string owner, string repo, GitHubRelease release)
        => new()
        {
            Owner = owner,
            Repo = repo,
            Tag = release.TagName,
            ReleaseName = release.Name,
            Prerelease = release.Prerelease,
            PublishedAt = release.PublishedAt,
            Assets = [.. release.Assets.Select(static asset => new GitHubAssetInfo
            {
                Id = asset.Id,
                Name = asset.Name,
                Size = asset.Size,
                Digest = asset.Digest,
                Kind = GitHubReleaseLogic.ClassifyAsset(asset.Name).ToString(),
                Installable = GitHubReleaseLogic.IsInstallable(asset)
            })],
            RecommendedAssetId = GitHubReleaseLogic.SelectRecommendedAsset(release.Assets, repo)
        };

    private void RecordInstalledSource(
        InstallGitHubAssetRequest request,
        string tagName,
        GitHubReleaseAsset asset,
        GitHubDownloadedAsset downloaded,
        PluginInstallResult result)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        try
        {
            var record = new GitHubSource
            {
                PluginId = result.PluginId ?? Guid.Empty,
                FolderName = GitHubReleaseLogic.StripVersionFolderSuffix(Path.GetFileName(result.Directory)),
                PluginName = result.Name,
                Owner = request.Owner,
                Repo = request.Repo,
                TagName = tagName,
                AssetId = asset.Id,
                AssetName = asset.Name,
                Version = result.Version,
                Sha256Digest = downloaded.Sha256Hex,
                InstalledAtUtc = DateTime.UtcNow
            };
            lock (GitHubSourceStore.SyncRoot)
            {
                GitHubSourceStore.Upsert(plugin.Configuration, record);
                plugin.SaveConfiguration();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // The plugin is installed; a failed bookkeeping write must not fail the request.
            LogSourceRecordingFailed(_logger, exception, request.Owner, request.Repo);
        }
    }

    private ObjectResult GitHubSourceRejected(string detail)
        => BadRequest(new ProblemDetails
        {
            Title = "GitHub source rejected",
            Detail = detail,
            Status = StatusCodes.Status400BadRequest
        });

    private ActionResult MapGitHubFailure(GitHubSourceException exception, string owner, string repo)
    {
        switch (exception.Reason)
        {
            case GitHubSourceFailureReason.InvalidRequest:
            case GitHubSourceFailureReason.TooLarge:
                LogRejectedGitHubInstall(_logger, exception, owner, repo);
                return GitHubSourceRejected(exception.Message);
            case GitHubSourceFailureReason.NotFound:
                return Problem(
                    title: "GitHub release not found",
                    detail: exception.Message,
                    statusCode: StatusCodes.Status404NotFound);
            case GitHubSourceFailureReason.RateLimited:
                LogRateLimitSurfaced(_logger, owner, repo);
                if (exception.RateLimitResetsAt is { } resetsAt)
                {
                    var seconds = Math.Max(0, (long)(resetsAt - DateTimeOffset.UtcNow).TotalSeconds);
                    Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                }

                return Problem(
                    title: "GitHub rate limit exceeded",
                    detail: exception.Message,
                    statusCode: StatusCodes.Status429TooManyRequests);
            case GitHubSourceFailureReason.DigestMismatch:
                return Problem(
                    title: "Downloaded asset failed digest verification",
                    detail: exception.Message,
                    statusCode: StatusCodes.Status409Conflict);
            case GitHubSourceFailureReason.TimedOut:
                return Problem(
                    title: "GitHub request timed out",
                    detail: exception.Message,
                    statusCode: StatusCodes.Status504GatewayTimeout);
            case GitHubSourceFailureReason.Upstream:
            case GitHubSourceFailureReason.InvalidResponse:
            case GitHubSourceFailureReason.RedirectRejected:
            default:
                LogUpstreamSurfaced(_logger, exception, owner, repo);
                return Problem(
                    title: "GitHub request failed",
                    detail: exception.Message,
                    statusCode: StatusCodes.Status502BadGateway);
        }
    }

    [LoggerMessage(EventId = 2100, Level = LogLevel.Warning, Message = "Rejected GitHub install request for {Owner}/{Repo}")]
    private static partial void LogRejectedGitHubInstall(ILogger logger, Exception exception, string owner, string repo);

    [LoggerMessage(EventId = 2101, Level = LogLevel.Warning, Message = "GitHub rate limit surfaced to the administrator for {Owner}/{Repo}")]
    private static partial void LogRateLimitSurfaced(ILogger logger, string owner, string repo);

    [LoggerMessage(EventId = 2102, Level = LogLevel.Error, Message = "GitHub request for {Owner}/{Repo} failed upstream")]
    private static partial void LogUpstreamSurfaced(ILogger logger, Exception exception, string owner, string repo);

    [LoggerMessage(EventId = 2103, Level = LogLevel.Error, Message = "Unexpected error installing from GitHub {Owner}/{Repo}")]
    private static partial void LogUnexpectedGitHubError(ILogger logger, Exception exception, string owner, string repo);

    [LoggerMessage(EventId = 2104, Level = LogLevel.Warning, Message = "Installed from {Owner}/{Repo} but could not record the source for update checks")]
    private static partial void LogSourceRecordingFailed(ILogger logger, Exception exception, string owner, string repo);

    [LoggerMessage(EventId = 2105, Level = LogLevel.Information, Message = "{Action} {PluginName} {Version} from GitHub {Owner}/{Repo} tag {Tag}")]
    private static partial void LogGitHubInstallComplete(ILogger logger, string action, string pluginName, string version, string owner, string repo, string tag);

    [LoggerMessage(EventId = 2106, Level = LogLevel.Warning, Message = "GitHub install for {Owner}/{Repo} requires downgrade confirmation")]
    private static partial void LogDowngradeConfirmationRequired(ILogger logger, Exception exception, string owner, string repo);
}

/// <summary>
/// Request to resolve a GitHub release.
/// </summary>
public sealed class ResolveGitHubReleaseRequest
{
    /// <summary>Gets the repository as <c>owner/repo</c> or a pasted github.com URL.</summary>
    [Required]
    public required string Repository { get; init; }

    /// <summary>Gets the exact release tag; empty resolves the latest release.</summary>
    public string? Tag { get; init; }
}

/// <summary>
/// A resolved GitHub release presented for asset selection.
/// </summary>
public sealed class GitHubReleaseInfo
{
    /// <summary>Gets the repository owner.</summary>
    public required string Owner { get; init; }

    /// <summary>Gets the repository name.</summary>
    public required string Repo { get; init; }

    /// <summary>Gets the release tag.</summary>
    public required string Tag { get; init; }

    /// <summary>Gets the human-readable release title, when set.</summary>
    public string? ReleaseName { get; init; }

    /// <summary>Gets a value indicating whether the release is a prerelease.</summary>
    public bool Prerelease { get; init; }

    /// <summary>Gets the publication timestamp, when available.</summary>
    public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>Gets the release assets, including non-installable ones for display.</summary>
    public required IReadOnlyList<GitHubAssetInfo> Assets { get; init; }

    /// <summary>Gets the asset to preselect, or null when the administrator must choose.</summary>
    public long? RecommendedAssetId { get; init; }
}

/// <summary>
/// A release asset presented for selection.
/// </summary>
public sealed class GitHubAssetInfo
{
    /// <summary>Gets GitHub's identifier for the asset.</summary>
    public long Id { get; init; }

    /// <summary>Gets the asset file name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the asset size in bytes.</summary>
    public long Size { get; init; }

    /// <summary>Gets the digest GitHub published for the asset, when available.</summary>
    public string? Digest { get; init; }

    /// <summary>Gets the asset classification: Zip, Dll, or Other.</summary>
    public required string Kind { get; init; }

    /// <summary>Gets a value indicating whether the asset can be installed.</summary>
    public bool Installable { get; init; }
}

/// <summary>
/// Request to download and install a resolved release asset.
/// </summary>
public sealed class InstallGitHubAssetRequest
{
    /// <summary>Gets the repository owner.</summary>
    [Required]
    public required string Owner { get; init; }

    /// <summary>Gets the repository name.</summary>
    [Required]
    public required string Repo { get; init; }

    /// <summary>Gets the exact release tag from the resolve step.</summary>
    [Required]
    public required string Tag { get; init; }

    /// <summary>Gets the asset id from the resolve step.</summary>
    [Range(1, long.MaxValue)]
    public long AssetId { get; init; }

    /// <summary>Gets a value indicating whether an explicitly requested downgrade may proceed.</summary>
    public bool ConfirmOlderVersion { get; init; }
}

/// <summary>
/// The result of a GitHub-sourced installation.
/// </summary>
public sealed class GitHubInstallResult
{
    /// <summary>Gets the installer result.</summary>
    public required PluginInstallResult Install { get; init; }

    /// <summary>
    /// Gets a value indicating whether the download matched a digest GitHub published.
    /// False means GitHub offered no digest for the asset (common for older releases), not
    /// that verification failed — a failed verification aborts the install.
    /// </summary>
    public bool DigestVerified { get; init; }
}

/// <summary>
/// Request to check one tracked repository for a newer release.
/// </summary>
public sealed class CheckGitHubUpdateRequest
{
    /// <summary>Gets the repository owner.</summary>
    [Required]
    public required string Owner { get; init; }

    /// <summary>Gets the repository name.</summary>
    [Required]
    public required string Repo { get; init; }
}

/// <summary>
/// The outcome of an update check for one tracked repository.
/// </summary>
public sealed class GitHubUpdateCheckResult
{
    /// <summary>Gets the repository owner.</summary>
    public required string Owner { get; init; }

    /// <summary>Gets the repository name.</summary>
    public required string Repo { get; init; }

    /// <summary>Gets the tag the installed plugin came from.</summary>
    public required string InstalledTag { get; init; }

    /// <summary>Gets the installed plugin version.</summary>
    public required string InstalledVersion { get; init; }

    /// <summary>Gets a value indicating whether the latest release looks newer than the installed one.</summary>
    public bool UpdateAvailable { get; init; }

    /// <summary>Gets how the decision was made: "version" (parsed comparison) or "tag" (identity comparison).</summary>
    public required string ComparisonMethod { get; init; }

    /// <summary>Gets the latest release, ready to feed into the install endpoint.</summary>
    public required GitHubReleaseInfo Latest { get; init; }
}
