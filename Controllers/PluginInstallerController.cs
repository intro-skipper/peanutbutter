using Jellyfin.Plugin.PeanutButter.Services;
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
public sealed class PluginInstallerController : ControllerBase
{
    private readonly PluginZipInstaller _installer;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginInstallerController"/> class.
    /// </summary>
    public PluginInstallerController(
        IApplicationPaths applicationPaths,
        ILoggerFactory loggerFactory)
    {
        _installer = new PluginZipInstaller(
            applicationPaths.PluginsPath,
            loggerFactory.CreateLogger<PluginZipInstaller>());
    }

    /// <summary>
    /// Installs or updates a plugin from a ZIP file.
    /// </summary>
    /// <param name="file">The plugin ZIP sent as the multipart form field named <c>file</c>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The installation result.</returns>
    [HttpPost("Install")]
    [RequestSizeLimit(PluginZipInstaller.MaximumUploadBytes)]
    [ProducesResponseType(typeof(PluginInstallResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PluginInstallResult>> Install(
        IFormFile? file,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
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
            return Ok(result);
        }
        catch (PluginArchiveException exception)
        {
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
    }
}
