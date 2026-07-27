using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using MediaBrowser.Common.Plugins;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PeanutButter.Services;

/// <summary>
/// Safely stages and installs a Jellyfin plugin archive.
/// </summary>
public sealed partial class PluginZipInstaller
{
    /// <summary>
    /// Maximum accepted upload size. Plugin packages are normally only a few megabytes;
    /// the limit also prevents an authenticated request from consuming unbounded disk space.
    /// </summary>
    public const long MaximumUploadBytes = 100 * 1024 * 1024;

    /// <summary>
    /// Maximum uncompressed archive size.
    /// </summary>
    public const long MaximumExtractedBytes = 500 * 1024 * 1024;

    private const int MaximumEntryCount = 10_000;
    private const long MaximumMetadataBytes = 1 * 1024 * 1024;
    private readonly string _pluginsPath;
    private readonly string _stagingPath;
    private readonly ILogger<PluginZipInstaller> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginZipInstaller"/> class.
    /// </summary>
    /// <param name="pluginsPath">The Jellyfin plugin directory.</param>
    /// <param name="logger">The logger.</param>
    public PluginZipInstaller(string pluginsPath, ILogger<PluginZipInstaller> logger)
    {
        if (string.IsNullOrWhiteSpace(pluginsPath))
        {
            throw new ArgumentException("The plugin directory is required.", nameof(pluginsPath));
        }

        _pluginsPath = Path.GetFullPath(pluginsPath);
        _stagingPath = Path.Combine(_pluginsPath, ".plugin-installer-staging");
        _logger = logger;
    }

    /// <summary>
    /// Installs an archive, replacing the matching installed plugin when present.
    /// </summary>
    /// <param name="archiveStream">The ZIP stream.</param>
    /// <param name="fileName">The client-supplied file name, used for diagnostics only.</param>
    /// <param name="length">The client-reported upload length.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result of the installation.</returns>
    public async Task<PluginInstallResult> InstallAsync(
        Stream archiveStream,
        string? fileName,
        long length,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(archiveStream);

        if (length <= 0)
        {
            throw new PluginArchiveException("The uploaded archive is empty.");
        }

        if (length > MaximumUploadBytes)
        {
            throw new PluginArchiveException(
                $"The archive is too large. The maximum upload size is {MaximumUploadBytes / 1024 / 1024} MB.");
        }

        Directory.CreateDirectory(_pluginsPath);
        Directory.CreateDirectory(_stagingPath);

        var operationId = Guid.NewGuid().ToString("N");
        var uploadPath = Path.Combine(_stagingPath, $"{operationId}.zip");
        var extractedPath = Path.Combine(_stagingPath, operationId);
        var movedExistingPath = string.Empty;
        var targetPath = string.Empty;
        var newDirectoryMoved = false;

        try
        {
            await using (var uploadFile = new FileStream(
                uploadPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await archiveStream.CopyToAsync(uploadFile, cancellationToken).ConfigureAwait(false);
            }

            Directory.CreateDirectory(extractedPath);
            await ExtractArchiveAsync(uploadPath, extractedPath, cancellationToken).ConfigureAwait(false);

            var archiveInfo = ReadArchiveInfo(extractedPath, _logger);
            var existingDirectory = FindExistingPlugin(archiveInfo);
            targetPath = existingDirectory?.FullPath
                ?? Path.Combine(_pluginsPath, archiveInfo.FolderName);

            EnsureSafePluginPath(targetPath);
            if (existingDirectory is null && Directory.Exists(targetPath))
            {
                throw new PluginArchiveException(
                    $"The destination folder '{Path.GetFileName(targetPath)}' already exists but does not identify the same plugin.");
            }

            if (existingDirectory is not null)
            {
                movedExistingPath = Path.Combine(
                    _stagingPath,
                    $"backup-{operationId}");
                Directory.Move(targetPath, movedExistingPath);
            }

            Directory.Move(extractedPath, targetPath);
            newDirectoryMoved = true;

            if (!string.IsNullOrEmpty(movedExistingPath))
            {
                Directory.Delete(movedExistingPath, recursive: true);
            }

            LogPluginInstalled(
                _logger,
                existingDirectory is null ? "Installed" : "Updated",
                archiveInfo.Name,
                archiveInfo.PluginId,
                archiveInfo.Version,
                targetPath,
                fileName ?? "uploaded archive");

            return new PluginInstallResult
            {
                Action = existingDirectory is null ? "Installed" : "Updated",
                Name = archiveInfo.Name,
                PluginId = archiveInfo.PluginId,
                Version = archiveInfo.Version,
                Directory = targetPath,
                RestartRequired = true
            };
        }
        catch (PluginArchiveException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            LogArchiveInstallError(_logger, exception, fileName);
            throw new PluginArchiveException(
                "The plugin could not be installed. Check the Jellyfin log for the underlying file-system error.",
                exception);
        }
        finally
        {
            if (!newDirectoryMoved && !string.IsNullOrEmpty(movedExistingPath) && Directory.Exists(movedExistingPath))
            {
                try
                {
                    if (Directory.Exists(targetPath))
                    {
                        Directory.Delete(targetPath, recursive: true);
                    }

                    Directory.Move(movedExistingPath, targetPath);
                }
                catch (Exception rollbackException) when (rollbackException is IOException or UnauthorizedAccessException)
                {
                    LogPluginRollbackFailure(_logger, rollbackException, targetPath, movedExistingPath);
                }
            }

            TryDeleteFile(uploadPath);
            TryDeleteDirectory(extractedPath);
        }
    }

    /// <summary>
    /// Verifies and installs a standalone managed plugin DLL.
    /// </summary>
    /// <param name="pluginStream">The DLL stream.</param>
    /// <param name="fileName">The client-supplied file name, used for diagnostics only.</param>
    /// <param name="length">The client-reported upload length.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result of the installation.</returns>
    public async Task<PluginInstallResult> InstallDllAsync(
        Stream pluginStream,
        string? fileName,
        long length,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pluginStream);

        if (length <= 0 || length > MaximumUploadBytes)
        {
            throw new PluginArchiveException(
                $"The DLL must be between 1 byte and {MaximumUploadBytes / 1024 / 1024} MB.");
        }

        Directory.CreateDirectory(_pluginsPath);
        Directory.CreateDirectory(_stagingPath);

        var operationId = Guid.NewGuid().ToString("N");
        var extractedPath = Path.Combine(_stagingPath, operationId);
        var dllName = Path.GetFileName(fileName);
        var movedExistingPath = string.Empty;
        var targetPath = string.Empty;
        var newDirectoryMoved = false;

        try
        {
            if (string.IsNullOrWhiteSpace(dllName)
                || !dllName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                || dllName is ".dll" or "..dll")
            {
                throw new PluginArchiveException("The uploaded file must have a .dll extension.");
            }

            Directory.CreateDirectory(extractedPath);
            var dllPath = Path.Combine(extractedPath, dllName);
            await using (var output = new FileStream(
                dllPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await pluginStream.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            var assemblyInfo = VerifyPluginAssembly(dllPath);
            var archiveInfo = new PluginArchiveInfo(
                null,
                assemblyInfo.AssemblyName,
                assemblyInfo.Version,
                SanitizeFolderName(assemblyInfo.AssemblyName));
            if (string.IsNullOrEmpty(archiveInfo.FolderName))
            {
                throw new PluginArchiveException("The DLL does not have a usable assembly name.");
            }

            var existingDirectory = FindExistingPlugin(archiveInfo);
            targetPath = existingDirectory?.FullPath
                ?? Path.Combine(_pluginsPath, archiveInfo.FolderName);
            EnsureSafePluginPath(targetPath);
            if (existingDirectory is null && Directory.Exists(targetPath))
            {
                throw new PluginArchiveException(
                    $"The destination folder '{Path.GetFileName(targetPath)}' already exists but does not identify the same plugin.");
            }

            if (existingDirectory is not null)
            {
                movedExistingPath = Path.Combine(_stagingPath, $"backup-{operationId}");
                Directory.Move(targetPath, movedExistingPath);
            }

            Directory.Move(extractedPath, targetPath);
            newDirectoryMoved = true;
            if (!string.IsNullOrEmpty(movedExistingPath))
            {
                Directory.Delete(movedExistingPath, recursive: true);
            }

            LogStandalonePluginInstalled(
                _logger,
                existingDirectory is null ? "Installed" : "Updated",
                archiveInfo.Name,
                archiveInfo.Version,
                targetPath,
                fileName ?? "uploaded DLL");

            return new PluginInstallResult
            {
                Action = existingDirectory is null ? "Installed" : "Updated",
                Name = archiveInfo.Name,
                Version = archiveInfo.Version,
                Directory = targetPath,
                RestartRequired = true
            };
        }
        catch (PluginArchiveException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is BadImageFormatException
            or FileLoadException
            or FileNotFoundException
            or IOException
            or UnauthorizedAccessException
            or ReflectionTypeLoadException)
        {
            LogDllInstallError(_logger, exception, fileName);
            throw new PluginArchiveException(
                "The DLL could not be installed. Check that it is a valid Jellyfin plugin and review the Jellyfin log.",
                exception);
        }
        finally
        {
            if (!newDirectoryMoved && !string.IsNullOrEmpty(movedExistingPath) && Directory.Exists(movedExistingPath))
            {
                try
                {
                    if (Directory.Exists(targetPath))
                    {
                        Directory.Delete(targetPath, recursive: true);
                    }

                    Directory.Move(movedExistingPath, targetPath);
                }
                catch (Exception rollbackException) when (rollbackException is IOException or UnauthorizedAccessException)
                {
                    LogDllRollbackFailure(_logger, rollbackException, targetPath, movedExistingPath);
                }
            }

            TryDeleteDirectory(extractedPath);
        }
    }

    private static async Task ExtractArchiveAsync(
        string archivePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var fileEntries = archive.Entries.Where(entry => !IsDirectory(entry)).ToArray();
        if (fileEntries.Length == 0)
        {
            throw new PluginArchiveException("The archive does not contain any files.");
        }

        if (fileEntries.Length > MaximumEntryCount)
        {
            throw new PluginArchiveException($"The archive contains more than {MaximumEntryCount} files.");
        }

        var normalizedPaths = fileEntries
            .Select(entry => NormalizeArchivePath(entry.FullName))
            .ToArray();
        var rootPrefix = FindCommonRootPrefix(normalizedPaths);
        var extractedBytes = 0L;
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < fileEntries.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = normalizedPaths[index];
            if (!string.IsNullOrEmpty(rootPrefix))
            {
                relativePath = relativePath[rootPrefix.Length..];
            }

            if (string.IsNullOrEmpty(relativePath))
            {
                throw new PluginArchiveException("The archive contains an invalid root entry.");
            }

            var destination = Path.GetFullPath(Path.Combine(destinationPath, relativePath));
            EnsureWithinDirectory(destinationPath, destination);
            if (!destinations.Add(destination))
            {
                throw new PluginArchiveException($"The archive contains duplicate file paths: '{relativePath}'.");
            }

            if (fileEntries[index].Length > MaximumExtractedBytes
                || extractedBytes > MaximumExtractedBytes - fileEntries[index].Length)
            {
                throw new PluginArchiveException(
                    $"The uncompressed archive is larger than the {MaximumExtractedBytes / 1024 / 1024} MB limit.");
            }

            extractedBytes += fileEntries[index].Length;
            var parent = Path.GetDirectoryName(destination);
            if (parent is null)
            {
                throw new PluginArchiveException("The archive contains an invalid file path.");
            }

            Directory.CreateDirectory(parent);
            await using var input = fileEntries[index].Open();
            await using var output = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }

        if (!Directory.EnumerateFiles(destinationPath, "*.dll", SearchOption.AllDirectories).Any())
        {
            throw new PluginArchiveException("The archive does not contain a plugin DLL.");
        }
    }

    private static PluginArchiveInfo ReadArchiveInfo(
        string extractedPath,
        ILogger logger)
    {
        var metadataPath = Directory.EnumerateFiles(extractedPath, "meta.json", SearchOption.AllDirectories)
            .FirstOrDefault(path => string.Equals(Path.GetFileName(path), "meta.json", StringComparison.OrdinalIgnoreCase));

        Guid? pluginId = null;
        var name = string.Empty;
        var version = string.Empty;
        if (metadataPath is not null)
        {
            var metadataInfo = new FileInfo(metadataPath);
            if (metadataInfo.Length > MaximumMetadataBytes)
            {
                throw new PluginArchiveException("The archive metadata file is too large.");
            }

            using var metadata = JsonDocument.Parse(File.ReadAllBytes(metadataPath));
            var root = metadata.RootElement;
            pluginId = ReadGuid(root, "guid") ?? ReadGuid(root, "id");
            name = ReadString(root, "name");
            version = ReadString(root, "version");
        }

        var dllPaths = Directory.EnumerateFiles(extractedPath, "*.dll", SearchOption.AllDirectories)
            .OrderBy(path => path.Length)
            .ToArray();
        var dllPath = dllPaths.First();
        DirectAssemblyInfo? verifiedAssembly = null;
        PluginArchiveException? lastVerificationFailure = null;
        foreach (var candidate in dllPaths)
        {
            try
            {
                verifiedAssembly = VerifyPluginAssembly(candidate);
                dllPath = candidate;
                break;
            }
            catch (PluginArchiveException exception)
            {
                lastVerificationFailure = exception;
            }
        }

        if (verifiedAssembly is null)
        {
            throw lastVerificationFailure
                ?? new PluginArchiveException("The archive does not contain a verifiable Jellyfin plugin DLL.");
        }

        var dllName = Path.GetFileNameWithoutExtension(dllPath);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = verifiedAssembly.AssemblyName
                .Replace("Jellyfin.Plugin.", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("Plugin.", string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        name = SanitizeFolderName(name);
        if (string.IsNullOrEmpty(name))
        {
            throw new PluginArchiveException("The archive does not identify a plugin name.");
        }

        if (!string.IsNullOrWhiteSpace(version) && !Version.TryParse(version, out _))
        {
            throw new PluginArchiveException($"The plugin version '{version}' is not a valid version.");
        }

        return new PluginArchiveInfo(
            pluginId,
            name,
            string.IsNullOrWhiteSpace(version) ? verifiedAssembly.Version : version,
            name);
    }

    private ExistingPlugin? FindExistingPlugin(PluginArchiveInfo archiveInfo)
    {
        if (!Directory.Exists(_pluginsPath))
        {
            return null;
        }

        foreach (var directory in Directory.EnumerateDirectories(_pluginsPath))
        {
            var directoryName = Path.GetFileName(directory);
            if (directoryName.StartsWith('.')
                || IsReparsePoint(directory))
            {
                continue;
            }

            var metadataPath = Directory.EnumerateFiles(directory, "meta.json", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();
            if (metadataPath is not null)
            {
                try
                {
                    using var metadata = JsonDocument.Parse(File.ReadAllBytes(metadataPath));
                    var existingId = ReadGuid(metadata.RootElement, "guid") ?? ReadGuid(metadata.RootElement, "id");
                    if (archiveInfo.PluginId.HasValue && existingId == archiveInfo.PluginId)
                    {
                        return new ExistingPlugin(directory);
                    }

                    var existingName = ReadString(metadata.RootElement, "name");
                    if (string.Equals(SanitizeFolderName(existingName), archiveInfo.FolderName, StringComparison.OrdinalIgnoreCase))
                    {
                        return new ExistingPlugin(directory);
                    }
                }
                catch (JsonException)
                {
                    LogMalformedMetadata(_logger, metadataPath);
                }
            }

            if (string.Equals(directoryName, archiveInfo.FolderName, StringComparison.OrdinalIgnoreCase))
            {
                return new ExistingPlugin(directory);
            }

            if (Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly)
                .Any(path => string.Equals(
                    Path.GetFileNameWithoutExtension(path),
                    archiveInfo.FolderName,
                    StringComparison.OrdinalIgnoreCase)))
            {
                return new ExistingPlugin(directory);
            }
        }

        return null;
    }

    private void EnsureSafePluginPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        EnsureWithinDirectory(_pluginsPath, fullPath);
        if (IsReparsePoint(fullPath))
        {
            throw new PluginArchiveException("The plugin destination is a reparse point and cannot be replaced safely.");
        }
    }

    private static void EnsureWithinDirectory(string directory, string path)
    {
        var fullDirectory = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new PluginArchiveException("The archive contains a path outside the plugin directory.");
        }
    }

    private static string NormalizeArchivePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized)
            || normalized.StartsWith('/')
            || normalized.Contains(':')
            || normalized.Contains('\0'))
        {
            throw new PluginArchiveException($"The archive contains an invalid path: '{path}'.");
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
        {
            throw new PluginArchiveException($"The archive contains a path traversal entry: '{path}'.");
        }

        return string.Join('/', segments);
    }

    private static string FindCommonRootPrefix(string[] paths)
    {
        if (paths.Length == 0)
        {
            return string.Empty;
        }

        var firstSegments = paths
            .Select(path => path.Split('/', 2)[0])
            .ToArray();
        if (firstSegments.Any(segment => string.IsNullOrEmpty(segment))
            || firstSegments.Any(segment => !string.Equals(segment, firstSegments[0], StringComparison.OrdinalIgnoreCase))
            || paths.Any(path => !path.Contains('/', StringComparison.Ordinal)))
        {
            return string.Empty;
        }

        return firstSegments[0] + "/";
    }

    private static bool IsDirectory(ZipArchiveEntry entry)
        => entry.FullName.EndsWith('/')
            || entry.Name.Length == 0;

    private static bool IsReparsePoint(string path)
        => Directory.Exists(path)
            && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static Guid? ReadGuid(JsonElement root, string propertyName)
    {
        var value = ReadString(root, propertyName);
        return Guid.TryParse(value, out var result) ? result : null;
    }

    private static string ReadString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }

    private static string SanitizeFolderName(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var sanitized = new string(value
            .Trim()
            .Select(character => invalidCharacters.Contains(character) ? '_' : character)
            .ToArray())
            .Trim('.', ' ');

        if (sanitized.Length > 120)
        {
            sanitized = sanitized[..120].TrimEnd('.', ' ');
        }

        return sanitized is "." or ".." ? string.Empty : sanitized;
    }

    private static DirectAssemblyInfo VerifyPluginAssembly(string path)
    {
        AssemblyName assemblyName;
        try
        {
            assemblyName = AssemblyName.GetAssemblyName(path);
        }
        catch (BadImageFormatException exception)
        {
            throw new PluginArchiveException("The DLL is not a valid managed .NET assembly.", exception);
        }

        using var loadContext = new PluginInspectionLoadContext(path);
        Assembly assembly;
        try
        {
            assembly = loadContext.LoadFromAssemblyPath(path);
        }
        catch (Exception exception) when (exception is FileLoadException or FileNotFoundException or BadImageFormatException)
        {
            throw new PluginArchiveException("The DLL could not be loaded for Jellyfin plugin verification.", exception);
        }

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            types = exception.Types
                .Where(type => type is not null)
                .Cast<Type>()
                .ToArray();

            if (ContainsPluginType(types))
            {
                return new DirectAssemblyInfo(
                    assemblyName.Name ?? Path.GetFileNameWithoutExtension(path),
                    assemblyName.Version?.ToString() ?? "0.0.0.0");
            }

            var loaderErrors = string.Join(
                "; ",
                exception.LoaderExceptions
                    .Where(error => error is not null)
                    .Select(error => error!.Message));
            throw new PluginArchiveException(
                $"The DLL could not be inspected because no loadable type implements Jellyfin's IPlugin interface. Type load errors: {loaderErrors}",
                exception);
        }

        if (!ContainsPluginType(types))
        {
            throw new PluginArchiveException(
                "The DLL does not contain a public concrete type implementing Jellyfin's IPlugin interface.");
        }

        return new DirectAssemblyInfo(
            assemblyName.Name ?? Path.GetFileNameWithoutExtension(path),
            assemblyName.Version?.ToString() ?? "0.0.0.0");
    }

    private static bool ContainsPluginType(IEnumerable<Type> types)
        => types.Any(type =>
            type.IsClass
            && !type.IsAbstract
            && type.IsPublic
            && typeof(IPlugin).IsAssignableFrom(type));

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException exception)
        {
            LogTemporaryFileDeleteFailure(_logger, exception, path);
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException exception)
        {
            LogTemporaryDirectoryDeleteFailure(_logger, exception, path);
        }
    }

    private sealed record ExistingPlugin(string FullPath);

    private sealed record PluginArchiveInfo(Guid? PluginId, string Name, string Version, string FolderName);

    private sealed record DirectAssemblyInfo(string AssemblyName, string Version);

    private sealed class PluginInspectionLoadContext : AssemblyLoadContext, IDisposable
    {
        private readonly AssemblyDependencyResolver _resolver;

        public PluginInspectionLoadContext(string pluginPath)
            : base(isCollectible: true)
        {
            _resolver = new AssemblyDependencyResolver(pluginPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Jellyfin's contracts must come from the already loaded server assemblies so
            // typeof(IPlugin).IsAssignableFrom(...) compares the same type identity.
            if (assemblyName.Name is not null
                && (assemblyName.Name.StartsWith("MediaBrowser.", StringComparison.Ordinal)
                    || assemblyName.Name.StartsWith("Jellyfin.", StringComparison.Ordinal)
                    || assemblyName.Name.StartsWith("Microsoft.", StringComparison.Ordinal)
                    || assemblyName.Name.StartsWith("System.", StringComparison.Ordinal)))
            {
                try
                {
                    return Assembly.Load(assemblyName);
                }
                catch (FileNotFoundException)
                {
                    return null;
                }
            }

            var dependencyPath = _resolver.ResolveAssemblyToPath(assemblyName);
            return dependencyPath is null ? null : LoadFromAssemblyPath(dependencyPath);
        }

        public void Dispose()
        {
            Unload();
        }
    }

    [LoggerMessage(EventId = 1000, Level = LogLevel.Information, Message = "{Action} Jellyfin plugin {PluginName} ({PluginId}) version {Version} in {PluginPath} from {ArchiveName}")]
    private static partial void LogPluginInstalled(
        ILogger logger,
        string action,
        string pluginName,
        Guid? pluginId,
        string version,
        string pluginPath,
        string archiveName);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Error, Message = "Unable to install the uploaded Jellyfin plugin archive {ArchiveName}")]
    private static partial void LogArchiveInstallError(ILogger logger, Exception exception, string? archiveName);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Critical, Message = "Unable to roll back plugin update for {PluginPath}; the previous plugin is staged at {BackupPath}")]
    private static partial void LogPluginRollbackFailure(ILogger logger, Exception exception, string pluginPath, string backupPath);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Information, Message = "{Action} standalone Jellyfin plugin {PluginName} version {Version} in {PluginPath} from {ArchiveName}")]
    private static partial void LogStandalonePluginInstalled(
        ILogger logger,
        string action,
        string pluginName,
        string version,
        string pluginPath,
        string archiveName);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Error, Message = "Unable to install the uploaded Jellyfin plugin DLL {FileName}")]
    private static partial void LogDllInstallError(ILogger logger, Exception exception, string? fileName);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Critical, Message = "Unable to roll back standalone plugin update for {PluginPath}; the previous plugin is staged at {BackupPath}")]
    private static partial void LogDllRollbackFailure(ILogger logger, Exception exception, string pluginPath, string backupPath);

    [LoggerMessage(EventId = 1006, Level = LogLevel.Warning, Message = "Ignoring malformed plugin metadata at {MetadataPath}")]
    private static partial void LogMalformedMetadata(ILogger logger, string metadataPath);

    [LoggerMessage(EventId = 1007, Level = LogLevel.Warning, Message = "Unable to remove temporary file {TemporaryPath}")]
    private static partial void LogTemporaryFileDeleteFailure(ILogger logger, Exception exception, string temporaryPath);

    [LoggerMessage(EventId = 1008, Level = LogLevel.Warning, Message = "Unable to remove temporary directory {TemporaryPath}")]
    private static partial void LogTemporaryDirectoryDeleteFailure(ILogger logger, Exception exception, string temporaryPath);
}

/// <summary>
/// The result returned after an archive is installed.
/// </summary>
public sealed class PluginInstallResult
{
    /// <summary>Gets the operation performed.</summary>
    public required string Action { get; init; }

    /// <summary>Gets the plugin name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the plugin GUID, when the archive supplied one.</summary>
    public Guid? PluginId { get; init; }

    /// <summary>Gets the installed plugin version.</summary>
    public required string Version { get; init; }

    /// <summary>Gets the installed plugin directory.</summary>
    public required string Directory { get; init; }

    /// <summary>Gets a value indicating whether Jellyfin must restart.</summary>
    public bool RestartRequired { get; init; }
}

/// <summary>
/// Indicates that an uploaded plugin archive failed validation.
/// </summary>
public sealed class PluginArchiveException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginArchiveException"/> class.
    /// </summary>
    public PluginArchiveException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginArchiveException"/> class.
    /// </summary>
    public PluginArchiveException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
