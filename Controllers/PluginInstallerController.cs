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

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginInstallerController"/> class.
    /// </summary>
    public PluginInstallerController(
        IApplicationPaths applicationPaths,
        ILoggerFactory loggerFactory,
        IApplicationHost applicationHost)
    {
        _logger = loggerFactory.CreateLogger<PluginInstallerController>();
        _applicationHost = applicationHost;
        _installer = new PluginZipInstaller(
            applicationPaths.PluginsPath,
            loggerFactory.CreateLogger<PluginZipInstaller>());
    }

    /// <summary>
    /// Installs or updates a plugin from a ZIP file.
    /// </summary>
    /// <param name="file">The plugin ZIP or DLL sent as the multipart form field named <c>file</c>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The installation result.</returns>
    [HttpPost("Install")]
    [RequestSizeLimit(PluginZipInstaller.MaximumUploadBytes)]
    [ProducesResponseType(typeof(PluginInstallResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<PluginInstallResult>> Install(
        IFormFile? file,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            LogInvalidInstallRequest(_logger, file?.FileName);
            return BadRequest(new ProblemDetails
            {
                Title = "Plugin archive is required",
                Detail = "Send a non-empty ZIP file in the multipart form field named 'file'.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        try
        {
            await using var stream = file.OpenReadStream();
            var result = file.FileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? await _installer.InstallDllAsync(
                    stream,
                    file.FileName,
                    file.Length,
                    cancellationToken).ConfigureAwait(false)
                : await _installer.InstallAsync(
                    stream,
                    file.FileName,
                    file.Length,
                    cancellationToken).ConfigureAwait(false);
            _applicationHost.NotifyPendingRestart();
            return Ok(result);
        }
        catch (PluginArchiveException exception)
        {
            LogRejectedInstall(_logger, exception, file.FileName);
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
            LogUnexpectedInstallError(_logger, exception, file.FileName);
            return StatusCode(StatusCodes.Status500InternalServerError, new ProblemDetails
            {
                Title = "Plugin installation failed",
                Detail = "The server could not complete the plugin installation. Check the Jellyfin log for details.",
                Status = StatusCodes.Status500InternalServerError
            });
        }
    }

    [LoggerMessage(EventId = 2000, Level = LogLevel.Warning, Message = "Rejected plugin installation request for {FileName}")]
    private static partial void LogInvalidInstallRequest(ILogger logger, string? fileName);

    [LoggerMessage(EventId = 2001, Level = LogLevel.Warning, Message = "Rejected plugin installation for {FileName}")]
    private static partial void LogRejectedInstall(ILogger logger, Exception exception, string fileName);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Error, Message = "Unexpected error installing plugin {FileName}")]
    private static partial void LogUnexpectedInstallError(ILogger logger, Exception exception, string fileName);
}
