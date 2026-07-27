using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PeanutButter.Services.GitHub;

/// <summary>
/// Fetches release metadata and downloads release assets from GitHub with a hardened
/// network posture: only server-built <c>api.github.com</c> URIs are requested, redirects
/// are followed manually against a GitHub-only host allowlist, response bodies are size
/// capped, and downloads count actual bytes while hashing for digest verification.
/// This client sends no credentials of any kind; if a GitHub token is ever introduced it
/// must be attached per-request and only when the request host is <c>api.github.com</c>.
/// </summary>
public sealed partial class GitHubReleaseClient
{
    /// <summary>Maximum accepted release-metadata response size.</summary>
    public const long MaximumApiResponseBytes = 5 * 1024 * 1024;

    private const int MaxRedirectHops = 3;
    private const int CopyBufferSize = 64 * 1024;
    private static readonly TimeSpan _metadataTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _downloadTimeout = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly ProductInfoHeaderValue _userAgent = new(
        "Jellyfin-Plugin-PeanutButter",
        typeof(GitHubReleaseClient).Assembly.GetName().Version?.ToString() ?? "0.0.0.0");

    private readonly HttpClient _httpClient;
    private readonly ILogger<GitHubReleaseClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="GitHubReleaseClient"/> class.
    /// </summary>
    /// <param name="httpClient">
    /// The HTTP client to send requests with. Its handler should have automatic redirects
    /// disabled (see <c>PluginServiceRegistrator</c>); the client's own timeout is replaced
    /// by this type's per-operation deadlines.
    /// </param>
    /// <param name="logger">The logger.</param>
    public GitHubReleaseClient(HttpClient httpClient, ILogger<GitHubReleaseClient> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        _logger = logger;
    }

    /// <summary>
    /// Fetches release metadata for validated repository coordinates: the latest release
    /// when <paramref name="tag"/> is null, otherwise the release for that exact tag.
    /// </summary>
    /// <param name="owner">The repository owner; must satisfy <see cref="GitHubReleaseLogic.IsValidOwner"/>.</param>
    /// <param name="repo">The repository name; must satisfy <see cref="GitHubReleaseLogic.IsValidRepo"/>.</param>
    /// <param name="tag">The optional exact tag; must satisfy <see cref="GitHubReleaseLogic.IsValidTag"/> when set.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The parsed release.</returns>
    /// <exception cref="GitHubSourceException">The coordinates are invalid or GitHub could not serve the release.</exception>
    public async Task<GitHubRelease> GetReleaseAsync(
        string owner,
        string repo,
        string? tag,
        CancellationToken cancellationToken)
    {
        EnsureValidCoordinates(owner, repo, tag);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_metadataTimeout);
        try
        {
            using var response = await SendWithRedirectsAsync(
                GitHubReleaseLogic.BuildReleaseUri(owner, repo, tag),
                "application/vnd.github+json",
                deadline.Token).ConfigureAwait(false);
            ThrowForFailureStatus(response, owner, repo);

            var body = await ReadCappedBodyAsync(response, deadline.Token).ConfigureAwait(false);
            GitHubRelease? release;
            try
            {
                release = JsonSerializer.Deserialize<GitHubRelease>(body, _jsonOptions);
            }
            catch (JsonException exception)
            {
                throw new GitHubSourceException(
                    GitHubSourceFailureReason.InvalidResponse,
                    "GitHub returned a release document that could not be parsed.",
                    exception);
            }

            if (release is null || string.IsNullOrEmpty(release.TagName))
            {
                throw new GitHubSourceException(
                    GitHubSourceFailureReason.InvalidResponse,
                    "GitHub returned an empty release document.");
            }

            return release;
        }
        catch (HttpRequestException exception)
        {
            LogRequestFailed(_logger, exception, owner, repo);
            throw new GitHubSourceException(
                GitHubSourceFailureReason.Upstream,
                "GitHub could not be reached. Check the server's network connectivity and try again.",
                exception);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GitHubSourceException(
                GitHubSourceFailureReason.TimedOut,
                "GitHub did not answer the release request in time.");
        }
    }

    /// <summary>
    /// Downloads a release asset to a uniquely named temporary file in
    /// <paramref name="destinationDirectory"/>, counting actual bytes against the
    /// installer's upload cap and computing a SHA-256 while streaming. When GitHub
    /// published a digest for the asset the download is verified against it.
    /// </summary>
    /// <param name="owner">The repository owner; must satisfy <see cref="GitHubReleaseLogic.IsValidOwner"/>.</param>
    /// <param name="repo">The repository name; must satisfy <see cref="GitHubReleaseLogic.IsValidRepo"/>.</param>
    /// <param name="asset">The asset, as returned inside a release fetched from this client.</param>
    /// <param name="destinationDirectory">The directory to place the temporary file in; created when missing.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A handle to the downloaded file; disposing it deletes the file.</returns>
    /// <exception cref="GitHubSourceException">The download failed, exceeded limits, or did not match the published digest.</exception>
    public async Task<GitHubDownloadedAsset> DownloadAssetAsync(
        string owner,
        string repo,
        GitHubReleaseAsset asset,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        EnsureValidCoordinates(owner, repo, tag: null);
        if (asset.Id <= 0)
        {
            throw new GitHubSourceException(
                GitHubSourceFailureReason.InvalidRequest,
                "The release asset identifier is not valid.");
        }

        if (asset.Size <= 0 || asset.Size > PluginZipInstaller.MaximumUploadBytes)
        {
            throw new GitHubSourceException(
                GitHubSourceFailureReason.TooLarge,
                $"The asset '{asset.Name}' must be between 1 byte and {PluginZipInstaller.MaximumUploadBytes / 1024 / 1024} MB.");
        }

        Directory.CreateDirectory(destinationDirectory);
        var extension = Path.GetExtension(asset.Name);
        var filePath = Path.Combine(
            destinationDirectory,
            $"github-asset-{Guid.NewGuid():N}{extension}");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_downloadTimeout);
        try
        {
            using var response = await SendWithRedirectsAsync(
                GitHubReleaseLogic.BuildAssetUri(owner, repo, asset.Id),
                "application/octet-stream",
                deadline.Token).ConfigureAwait(false);
            ThrowForFailureStatus(response, owner, repo);

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength is > PluginZipInstaller.MaximumUploadBytes)
            {
                throw new GitHubSourceException(
                    GitHubSourceFailureReason.TooLarge,
                    $"The asset '{asset.Name}' is larger than the {PluginZipInstaller.MaximumUploadBytes / 1024 / 1024} MB limit.");
            }

            var (totalBytes, sha256Hex) = await CopyToFileAsync(response, filePath, asset.Name, deadline.Token)
                .ConfigureAwait(false);
            var digestVerified = VerifyDigest(asset, sha256Hex);
            LogDownloadComplete(_logger, asset.Name, totalBytes, sha256Hex);
            return new GitHubDownloadedAsset(filePath, asset.Name, totalBytes, sha256Hex, digestVerified);
        }
        catch (HttpRequestException exception)
        {
            TryDeleteFile(filePath);
            LogRequestFailed(_logger, exception, owner, repo);
            throw new GitHubSourceException(
                GitHubSourceFailureReason.Upstream,
                "The asset download failed. Check the server's network connectivity and try again.",
                exception);
        }
        catch (IOException exception)
        {
            TryDeleteFile(filePath);
            throw new GitHubSourceException(
                GitHubSourceFailureReason.Upstream,
                "The asset download could not be written to disk. Check the Jellyfin log for details.",
                exception);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryDeleteFile(filePath);
            throw new GitHubSourceException(
                GitHubSourceFailureReason.TimedOut,
                $"Downloading '{asset.Name}' did not finish within {_downloadTimeout.TotalMinutes:0} minutes.");
        }
        catch (Exception)
        {
            TryDeleteFile(filePath);
            throw;
        }
    }

    private static void EnsureValidCoordinates(string owner, string repo, string? tag)
    {
        if (!GitHubReleaseLogic.IsValidOwner(owner))
        {
            throw new GitHubSourceException(
                GitHubSourceFailureReason.InvalidRequest,
                "The GitHub owner name is not valid.");
        }

        if (!GitHubReleaseLogic.IsValidRepo(repo))
        {
            throw new GitHubSourceException(
                GitHubSourceFailureReason.InvalidRequest,
                "The GitHub repository name is not valid.");
        }

        if (tag is not null && !GitHubReleaseLogic.IsValidTag(tag))
        {
            throw new GitHubSourceException(
                GitHubSourceFailureReason.InvalidRequest,
                "The release tag contains unsupported characters.");
        }
    }

    /// <summary>
    /// Sends a GET request following at most <see cref="MaxRedirectHops"/> redirects
    /// manually. Every hop — including the first — must pass the GitHub host allowlist,
    /// and each hop uses a fresh request message so no header can carry over.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRedirectsAsync(
        Uri initialUri,
        string accept,
        CancellationToken cancellationToken)
    {
        var uri = initialUri;
        for (var hop = 0; hop <= MaxRedirectHops; hop++)
        {
            if (!GitHubReleaseLogic.IsAllowedDownloadUri(uri))
            {
                // Redirect URLs can carry short-lived signatures in the query; log the host only.
                LogRedirectRejected(_logger, uri.IsAbsoluteUri ? uri.Host : "(relative)");
                throw new GitHubSourceException(
                    GitHubSourceFailureReason.RedirectRejected,
                    "GitHub redirected the request to a host outside the allowed GitHub infrastructure.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd(accept);
            request.Headers.UserAgent.Add(_userAgent);
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MovedPermanently
                or HttpStatusCode.Found
                or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect
                or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null || !location.IsAbsoluteUri)
                {
                    LogRedirectRejected(_logger, "(missing or relative Location)");
                    throw new GitHubSourceException(
                        GitHubSourceFailureReason.RedirectRejected,
                        "GitHub sent a redirect without a usable absolute target.");
                }

                uri = location;
                continue;
            }

            return response;
        }

        LogTooManyRedirects(_logger, initialUri.Host, MaxRedirectHops);
        throw new GitHubSourceException(
            GitHubSourceFailureReason.RedirectRejected,
            "GitHub sent too many redirects for a single request.");
    }

    private void ThrowForFailureStatus(HttpResponseMessage response, string owner, string repo)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new GitHubSourceException(
                GitHubSourceFailureReason.NotFound,
                $"GitHub has no matching repository, release, or asset for '{owner}/{repo}'.");
        }

        if ((response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            && IsRateLimited(response))
        {
            var resetsAt = ReadRateLimitReset(response);
            LogRateLimited(_logger, resetsAt);
            var resetHint = resetsAt is null
                ? "later"
                : $"after {resetsAt.Value.ToLocalTime():HH:mm}";
            throw new GitHubSourceException(
                GitHubSourceFailureReason.RateLimited,
                $"GitHub's rate limit for unauthenticated requests (60 per hour) is exhausted. Try again {resetHint}.")
            {
                RateLimitResetsAt = resetsAt
            };
        }

        var status = (int)response.StatusCode;
        LogUpstreamFailure(_logger, status, owner, repo);
        throw new GitHubSourceException(
            GitHubSourceFailureReason.Upstream,
            $"GitHub answered with HTTP {status}. Try again later.");
    }

    private async Task<byte[]> ReadCappedBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffered = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            int read;
            while ((read = await body.ReadAsync(buffer.AsMemory(0, CopyBufferSize), cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffered.Length + read > MaximumApiResponseBytes)
                {
                    LogApiResponseTooLarge(_logger, MaximumApiResponseBytes);
                    throw new GitHubSourceException(
                        GitHubSourceFailureReason.InvalidResponse,
                        "GitHub returned an implausibly large release document.");
                }

                await buffered.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return buffered.ToArray();
    }

    private async Task<(long TotalBytes, string Sha256Hex)> CopyToFileAsync(
        HttpResponseMessage response,
        string filePath,
        string assetName,
        CancellationToken cancellationToken)
    {
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(
            filePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            var totalBytes = 0L;
            int read;
            while ((read = await body.ReadAsync(buffer.AsMemory(0, CopyBufferSize), cancellationToken).ConfigureAwait(false)) > 0)
            {
                totalBytes += read;
                if (totalBytes > PluginZipInstaller.MaximumUploadBytes)
                {
                    // The advertised size and Content-Length are advisory; only counted bytes are trusted.
                    LogDownloadTooLarge(_logger, assetName, PluginZipInstaller.MaximumUploadBytes);
                    throw new GitHubSourceException(
                        GitHubSourceFailureReason.TooLarge,
                        $"The asset '{assetName}' exceeded the {PluginZipInstaller.MaximumUploadBytes / 1024 / 1024} MB download limit.");
                }

                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            var sha256Hex = Convert.ToHexStringLower(hash.GetHashAndReset());
            return (totalBytes, sha256Hex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private bool VerifyDigest(GitHubReleaseAsset asset, string actualSha256Hex)
    {
        if (!GitHubReleaseLogic.TryParseSha256Digest(asset.Digest, out var expectedHex))
        {
            LogDigestUnavailable(_logger, asset.Name);
            return false;
        }

        var expected = Convert.FromHexString(expectedHex);
        var actual = Convert.FromHexString(actualSha256Hex);
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            LogDigestMismatch(_logger, asset.Name);
            throw new GitHubSourceException(
                GitHubSourceFailureReason.DigestMismatch,
                $"The downloaded bytes for '{asset.Name}' do not match the digest GitHub published. The release may have been modified; retry and investigate before installing.");
        }

        return true;
    }

    private static bool IsRateLimited(HttpResponseMessage response)
        => response.Headers.TryGetValues("x-ratelimit-remaining", out var values)
            && values.FirstOrDefault() == "0";

    private static DateTimeOffset? ReadRateLimitReset(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("x-ratelimit-reset", out var values)
            && long.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch))
        {
            return DateTimeOffset.FromUnixTimeSeconds(epoch);
        }

        return null;
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogTempFileCleanupFailure(_logger, exception, path);
        }
    }

    [LoggerMessage(EventId = 2200, Level = LogLevel.Error, Message = "GitHub request for {Owner}/{Repo} failed")]
    private static partial void LogRequestFailed(ILogger logger, Exception exception, string owner, string repo);

    [LoggerMessage(EventId = 2201, Level = LogLevel.Warning, Message = "GitHub rate limit exhausted; resets at {ResetsAt}")]
    private static partial void LogRateLimited(ILogger logger, DateTimeOffset? resetsAt);

    [LoggerMessage(EventId = 2202, Level = LogLevel.Warning, Message = "Rejected GitHub redirect to host {Host}")]
    private static partial void LogRedirectRejected(ILogger logger, string host);

    [LoggerMessage(EventId = 2203, Level = LogLevel.Warning, Message = "Aborted download of {AssetName}: exceeded {MaximumBytes} bytes")]
    private static partial void LogDownloadTooLarge(ILogger logger, string assetName, long maximumBytes);

    [LoggerMessage(EventId = 2204, Level = LogLevel.Warning, Message = "GitHub published no digest for {AssetName}; installing without digest verification")]
    private static partial void LogDigestUnavailable(ILogger logger, string assetName);

    [LoggerMessage(EventId = 2205, Level = LogLevel.Error, Message = "Digest mismatch for downloaded asset {AssetName}")]
    private static partial void LogDigestMismatch(ILogger logger, string assetName);

    [LoggerMessage(EventId = 2206, Level = LogLevel.Information, Message = "Downloaded {AssetName}: {TotalBytes} bytes, sha256 {Sha256Hex}")]
    private static partial void LogDownloadComplete(ILogger logger, string assetName, long totalBytes, string sha256Hex);

    [LoggerMessage(EventId = 2207, Level = LogLevel.Warning, Message = "GitHub API response exceeded the {MaximumBytes} byte cap")]
    private static partial void LogApiResponseTooLarge(ILogger logger, long maximumBytes);

    [LoggerMessage(EventId = 2208, Level = LogLevel.Warning, Message = "Too many GitHub redirects starting from {Host} (limit {MaxHops})")]
    private static partial void LogTooManyRedirects(ILogger logger, string host, int maxHops);

    [LoggerMessage(EventId = 2209, Level = LogLevel.Warning, Message = "Unable to remove temporary download file {TemporaryPath}")]
    private static partial void LogTempFileCleanupFailure(ILogger logger, Exception exception, string temporaryPath);

    [LoggerMessage(EventId = 2210, Level = LogLevel.Error, Message = "GitHub answered HTTP {StatusCode} for {Owner}/{Repo}")]
    private static partial void LogUpstreamFailure(ILogger logger, int statusCode, string owner, string repo);
}
