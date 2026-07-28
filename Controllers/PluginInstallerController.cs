using System.Globalization;
using System.Net.Http;
using Jellyfin.Plugin.PeanutButter.Services;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PeanutButter.Controllers;

/// <summary>
/// Receives Jellyfin plugin archives from an administrator.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("Plugins/PeanutButter")]
public sealed partial class PluginInstallerController : ControllerBase
{
    private readonly PluginZipInstaller _installer;
    private readonly ILogger<PluginInstallerController> _logger;
    private readonly IApplicationHost _applicationHost;
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginInstallerController"/> class.
    /// </summary>
    public PluginInstallerController(
        IApplicationPaths applicationPaths,
        ILoggerFactory loggerFactory,
        IApplicationHost applicationHost,
        IHttpClientFactory httpClientFactory)
    {
        _logger = loggerFactory.CreateLogger<PluginInstallerController>();
        _applicationHost = applicationHost;
        _httpClientFactory = httpClientFactory;
        _installer = new PluginZipInstaller(
            applicationPaths.PluginsPath,
            Path.Combine(applicationPaths.TempDirectory, "peanutbutter-installer-staging"),
            loggerFactory.CreateLogger<PluginZipInstaller>());
    }

    /// <summary>
    /// Installs or updates a plugin from a ZIP file.
    /// </summary>
    /// <param name="file">The plugin ZIP or DLL sent as the multipart form field named <c>file</c>.</param>
    /// <param name="url">An HTTPS URL to a plugin ZIP or DLL.</param>
    /// <param name="formUrl">An HTTPS URL supplied as the multipart form field named <c>url</c>.</param>
    /// <param name="confirmOlderVersion">Whether an explicitly requested downgrade may proceed.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The installation result.</returns>
    [HttpPost("Install")]
    [RequestSizeLimit(PluginZipInstaller.MaximumUploadBytes)]
    [ProducesResponseType(typeof(PluginInstallResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<PluginInstallResult>> Install(
        IFormFile? file,
        [FromQuery] bool confirmOlderVersion,
        [FromQuery(Name = "url")] string? url,
        [FromForm(Name = "url")] string? formUrl,
        CancellationToken cancellationToken)
    {
        var sourceUrl = string.IsNullOrWhiteSpace(formUrl) ? url : formUrl;
        var hasFile = file is not null && file.Length > 0;
        var hasUrl = !string.IsNullOrWhiteSpace(sourceUrl);
        if (hasFile == hasUrl)
        {
            LogInvalidInstallRequest(_logger, file?.FileName ?? sourceUrl);
            return BadRequest(new ProblemDetails
            {
                Title = "One plugin source is required",
                Detail = "Send either a non-empty ZIP or DLL in 'file', or an HTTPS plugin URL in 'url'.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        string? downloadedFilePath = null;
        try
        {
            PluginInstallResult result;
            string sourceName;
            if (hasUrl)
            {
                var downloadedFile = await DownloadPluginAsync(sourceUrl!, cancellationToken).ConfigureAwait(false);
                downloadedFilePath = downloadedFile.FilePath;
                sourceName = downloadedFile.FileName;
                await using var stream = new FileStream(
                    downloadedFile.FilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                result = sourceName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    ? await _installer.InstallDllAsync(
                        stream,
                        sourceName,
                        downloadedFile.Length,
                        confirmOlderVersion,
                        cancellationToken).ConfigureAwait(false)
                    : await _installer.InstallAsync(
                        stream,
                        sourceName,
                        downloadedFile.Length,
                        confirmOlderVersion,
                        cancellationToken).ConfigureAwait(false);
            }
            else
            {
                sourceName = file!.FileName;
                await using var stream = file.OpenReadStream();
                result = sourceName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    ? await _installer.InstallDllAsync(
                        stream,
                        sourceName,
                        file.Length,
                        confirmOlderVersion,
                        cancellationToken).ConfigureAwait(false)
                    : await _installer.InstallAsync(
                        stream,
                        sourceName,
                        file.Length,
                        confirmOlderVersion,
                        cancellationToken).ConfigureAwait(false);
            }

            _applicationHost.NotifyPendingRestart();
            return Ok(result);
        }
        catch (PluginDowngradeException exception)
        {
            LogDowngradeConfirmationRequired(_logger, exception, file?.FileName ?? sourceUrl!);
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
            LogRejectedInstall(_logger, exception, file?.FileName ?? sourceUrl!);
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
            LogUnexpectedInstallError(_logger, exception, file?.FileName ?? sourceUrl!);
            return StatusCode(StatusCodes.Status500InternalServerError, new ProblemDetails
            {
                Title = "Plugin installation failed",
                Detail = "The server could not complete the plugin installation. Check the Jellyfin log for details.",
                Status = StatusCodes.Status500InternalServerError
            });
        }
        finally
        {
            if (downloadedFilePath is not null)
            {
                TryDeleteDownloadedFile(downloadedFilePath);
            }
        }
    }

    private async Task<DownloadedPlugin> DownloadPluginAsync(string sourceUrl, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(sourceUrl.Trim(), UriKind.Absolute, out var sourceUri)
            || !string.Equals(sourceUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new PluginArchiveException("Plugin URLs must be absolute HTTPS URLs.");
        }

        var downloadUri = NormalizeGitHubArtifactUrl(sourceUri);
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(10);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PeanutButter-Plugin-Installer/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/zip");

        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync(
                downloadUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new PluginArchiveException("The plugin URL could not be downloaded.", exception);
        }

        using (response)
        {
            return await ReadDownloadedPluginAsync(response, downloadUri, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<DownloadedPlugin> ReadDownloadedPluginAsync(
        HttpResponseMessage response,
        Uri downloadUri,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new PluginArchiveException(
                $"The plugin URL returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        if (response.Content.Headers.ContentLength > PluginZipInstaller.MaximumUploadBytes)
        {
            throw new PluginArchiveException(
                $"The downloaded plugin is too large. The maximum size is {PluginZipInstaller.MaximumUploadBytes / 1024 / 1024} MB.");
        }

        var temporaryPath = Path.GetTempFileName();
        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[64 * 1024];
            long length = 0;
            int bytesRead;
            while ((bytesRead = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                length += bytesRead;
                if (length > PluginZipInstaller.MaximumUploadBytes)
                {
                    throw new PluginArchiveException(
                        $"The downloaded plugin is too large. The maximum size is {PluginZipInstaller.MaximumUploadBytes / 1024 / 1024} MB.");
                }

                await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            return new DownloadedPlugin(temporaryPath, length, GetDownloadedFileName(response, downloadUri));
        }
        catch (HttpRequestException exception)
        {
            TryDeleteDownloadedFile(temporaryPath);
            throw new PluginArchiveException("The plugin URL could not be downloaded.", exception);
        }
        catch
        {
            TryDeleteDownloadedFile(temporaryPath);
            throw;
        }
    }

    private static Uri NormalizeGitHubArtifactUrl(Uri sourceUri)
    {
        if (!string.Equals(sourceUri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return sourceUri;
        }

        var segments = sourceUri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();
        if (segments.Length != 7
            || !string.Equals(segments[2], "actions", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(segments[3], "runs", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(segments[5], "artifacts", StringComparison.OrdinalIgnoreCase)
            || !long.TryParse(segments[4], NumberStyles.None, CultureInfo.InvariantCulture, out _)
            || !long.TryParse(segments[6], NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            return sourceUri;
        }

        return new Uri(
            $"https://api.github.com/repos/{segments[0]}/{segments[1]}/actions/artifacts/{segments[6]}/zip");
    }

    private static string GetDownloadedFileName(HttpResponseMessage response, Uri downloadUri)
    {
        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
            ?? Path.GetFileName(downloadUri.LocalPath);
        fileName = Path.GetFileName(fileName);
        return string.IsNullOrWhiteSpace(fileName) ? "plugin.zip" : fileName;
    }

    private static void TryDeleteDownloadedFile(string path)
    {
        try
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
        catch (IOException)
        {
            // The installation result is already known; cleanup is best effort.
        }
    }

    private sealed record DownloadedPlugin(string FilePath, long Length, string FileName);

    [LoggerMessage(EventId = 2000, Level = LogLevel.Warning, Message = "Rejected plugin installation request for {FileName}")]
    private static partial void LogInvalidInstallRequest(ILogger logger, string? fileName);

    [LoggerMessage(EventId = 2001, Level = LogLevel.Warning, Message = "Rejected plugin installation for {FileName}")]
    private static partial void LogRejectedInstall(ILogger logger, Exception exception, string fileName);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Error, Message = "Unexpected error installing plugin {FileName}")]
    private static partial void LogUnexpectedInstallError(ILogger logger, Exception exception, string fileName);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Warning, Message = "Downgrade confirmation required for plugin {FileName}")]
    private static partial void LogDowngradeConfirmationRequired(ILogger logger, Exception exception, string fileName);
}
