using System.Text;
using Jellyfin.Plugin.PeanutButter.Services;
using Jellyfin.Plugin.PeanutButter.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.PeanutButter.Tests.Installer;

/// <summary>
/// Exercises the full staged-validate-install pipeline against a temporary plugins
/// directory, using the compiled plugin assembly as a genuine installable payload.
/// </summary>
public sealed class PluginZipInstallerIntegrationTests : IDisposable
{
    private static readonly Guid _pluginGuid = Guid.Parse("99999999-8888-7777-6666-555555555555");

    private readonly TempDirectory _pluginsDirectory = new();
    private readonly TempDirectory _stagingDirectory = new();
    private readonly PluginZipInstaller _installer;

    public PluginZipInstallerIntegrationTests()
    {
        _installer = new PluginZipInstaller(
            _pluginsDirectory.Path,
            Path.Combine(_stagingDirectory.Path, "staging"),
            NullLogger<PluginZipInstaller>.Instance);
    }

    public void Dispose()
    {
        _pluginsDirectory.Dispose();
        _stagingDirectory.Dispose();
    }

    [Fact]
    public void Constructor_StagingInsidePluginsDirectory_Throws()
    {
        // Jellyfin's plugin discovery enumerates every top-level folder under the plugin directory and
        // searches it recursively for DLLs, so a staging folder kept inside it is loaded as a bogus plugin
        // as soon as a backup or a partial upload is left behind.
        var exception = Assert.Throws<ArgumentException>(() => new PluginZipInstaller(
            _pluginsDirectory.Path,
            Path.Combine(_pluginsDirectory.Path, ".plugin-installer-staging"),
            NullLogger<PluginZipInstaller>.Instance));

        Assert.Equal("stagingPath", exception.ParamName);
    }

    [Fact]
    public async Task InstallAsync_StandardPluginZip_InstallsIntoVersionedFolder()
    {
        using var zip = PluginZipBuilder.BuildPluginZip(_pluginGuid, "Test Plugin", "1.2.3.4");

        var result = await _installer.InstallAsync(
            zip,
            "test-plugin.zip",
            zip.Length,
            confirmOlderVersion: false,
            TestContext.Current.CancellationToken);

        Assert.Equal("Installed", result.Action);
        Assert.Equal(_pluginGuid, result.PluginId);
        Assert.Equal("1.2.3.4", result.Version);
        Assert.True(result.RestartRequired);
        var installedDirectory = Path.Combine(_pluginsDirectory.Path, "Test Plugin_1.2.3.4");
        Assert.Equal(installedDirectory, result.Directory);
        Assert.True(File.Exists(Path.Combine(installedDirectory, PluginZipBuilder.PluginDllName)));
        Assert.True(File.Exists(Path.Combine(installedDirectory, "meta.json")));
        AssertStagingIsClean();
    }

    [Fact]
    public async Task InstallAsync_NewerVersion_InstallsSideBySideAndKeepsOldFolder()
    {
        var existing = SeedInstalledPlugin("Test Plugin_1.0.0.0", "1.0.0.0");
        using var zip = PluginZipBuilder.BuildPluginZip(_pluginGuid, "Test Plugin", "2.0.0.0");

        var result = await _installer.InstallAsync(
            zip,
            "test-plugin.zip",
            zip.Length,
            confirmOlderVersion: false,
            TestContext.Current.CancellationToken);

        Assert.Equal("Updated", result.Action);
        Assert.Equal(Path.Combine(_pluginsDirectory.Path, "Test Plugin_2.0.0.0"), result.Directory);
        Assert.True(Directory.Exists(existing), "the previous versioned folder must remain for Jellyfin's version selection");
        AssertStagingIsClean();
    }

    [Fact]
    public async Task InstallAsync_SameVersion_ReplacesExistingFolder()
    {
        var existing = SeedInstalledPlugin("Test Plugin_1.2.3.4", "1.2.3.4");
        var marker = Path.Combine(existing, "stale-marker.txt");
        await File.WriteAllTextAsync(marker, "old", TestContext.Current.CancellationToken);
        using var zip = PluginZipBuilder.BuildPluginZip(_pluginGuid, "Test Plugin", "1.2.3.4");

        var result = await _installer.InstallAsync(
            zip,
            "test-plugin.zip",
            zip.Length,
            confirmOlderVersion: false,
            TestContext.Current.CancellationToken);

        Assert.Equal("Updated", result.Action);
        Assert.Equal(existing, result.Directory);
        Assert.False(File.Exists(marker), "a complete package replaces the folder content");
        Assert.True(File.Exists(Path.Combine(existing, PluginZipBuilder.PluginDllName)));
        AssertStagingIsClean();
    }

    [Fact]
    public async Task InstallAsync_SameVersion_NestedExistingPackage_IsReplaced()
    {
        var existing = Path.Combine(_pluginsDirectory.Path, "Test Plugin_1.2.3.4", "package");
        Directory.CreateDirectory(existing);
        await File.WriteAllBytesAsync(
            Path.Combine(existing, "meta.json"),
            PluginZipBuilder.MetaJson(_pluginGuid, "Test Plugin", "1.2.3.4"),
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(existing, PluginZipBuilder.PluginDllName),
            PluginZipBuilder.PluginDllBytes,
            TestContext.Current.CancellationToken);
        using var zip = PluginZipBuilder.BuildPluginZip(_pluginGuid, "Test Plugin", "1.2.3.4");

        var result = await _installer.InstallAsync(
            zip,
            "github-plugin.zip",
            zip.Length,
            confirmOlderVersion: false,
            TestContext.Current.CancellationToken);

        Assert.Equal("Updated", result.Action);
        Assert.Equal(Path.Combine(_pluginsDirectory.Path, "Test Plugin_1.2.3.4"), result.Directory);
        Assert.True(File.Exists(Path.Combine(result.Directory, "meta.json")));
        Assert.False(Directory.Exists(Path.Combine(result.Directory, "package")));
        AssertStagingIsClean();
    }

    [Fact]
    public async Task InstallAsync_SameVersion_DllOnlyZip_NestedExistingPackage_ReplacesNestedAssembly()
    {
        var existing = Path.Combine(_pluginsDirectory.Path, "Test Plugin_1.2.3.4", "package");
        Directory.CreateDirectory(existing);
        await File.WriteAllBytesAsync(
            Path.Combine(existing, "meta.json"),
            PluginZipBuilder.MetaJson(_pluginGuid, "Test Plugin", "1.2.3.4"),
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(existing, PluginZipBuilder.PluginDllName),
            PluginZipBuilder.PluginDllBytes,
            TestContext.Current.CancellationToken);
        using var zip = PluginZipBuilder.Build((PluginZipBuilder.PluginDllName, PluginZipBuilder.PluginDllBytes));

        var result = await _installer.InstallAsync(
            zip,
            "github-build.zip",
            zip.Length,
            confirmOlderVersion: false,
            TestContext.Current.CancellationToken);

        Assert.Equal("Updated", result.Action);
        Assert.Equal(Path.Combine(_pluginsDirectory.Path, "Test Plugin_1.2.3.4"), result.Directory);
        Assert.True(File.Exists(Path.Combine(result.Directory, "package", PluginZipBuilder.PluginDllName)));
        Assert.False(File.Exists(Path.Combine(result.Directory, PluginZipBuilder.PluginDllName)));
        var pluginAssemblies = Directory.EnumerateFiles(
            result.Directory,
            PluginZipBuilder.PluginDllName,
            SearchOption.AllDirectories);
        Assert.Single(pluginAssemblies);
        var metadata = await File.ReadAllTextAsync(
            Path.Combine(result.Directory, "meta.json"),
            TestContext.Current.CancellationToken);
        Assert.Contains($"package/{PluginZipBuilder.PluginDllName}", metadata, StringComparison.Ordinal);
        AssertStagingIsClean();
    }

    [Fact]
    public async Task InstallAsync_OlderVersionWithoutConfirmation_ThrowsDowngrade()
    {
        SeedInstalledPlugin("Test Plugin_2.0.0.0", "2.0.0.0");
        using var zip = PluginZipBuilder.BuildPluginZip(_pluginGuid, "Test Plugin", "1.0.0.0");

        var exception = await Assert.ThrowsAsync<PluginDowngradeException>(
            () => _installer.InstallAsync(
                zip,
                "test-plugin.zip",
                zip.Length,
                confirmOlderVersion: false,
                TestContext.Current.CancellationToken));

        Assert.Equal("2.0.0.0", exception.InstalledVersion);
        Assert.Equal("1.0.0.0", exception.RequestedVersion);
        AssertStagingIsClean();
    }

    [Fact]
    public async Task InstallAsync_OlderVersionConfirmed_ReplacesExisting()
    {
        var existing = SeedInstalledPlugin("Test Plugin_2.0.0.0", "2.0.0.0");
        using var zip = PluginZipBuilder.BuildPluginZip(_pluginGuid, "Test Plugin", "1.0.0.0");

        var result = await _installer.InstallAsync(
            zip,
            "test-plugin.zip",
            zip.Length,
            confirmOlderVersion: true,
            TestContext.Current.CancellationToken);

        Assert.Equal("Updated", result.Action);
        Assert.Equal(existing, result.Directory);
        AssertStagingIsClean();
    }

    [Theory]
    [InlineData("../evil.dll")]
    [InlineData("/evil.dll")]
    [InlineData("C:\\evil.dll")]
    public async Task InstallAsync_TraversalEntries_RejectedWithoutSideEffects(string entryPath)
    {
        using var zip = PluginZipBuilder.Build(
            (entryPath, PluginZipBuilder.PluginDllBytes),
            (PluginZipBuilder.PluginDllName, PluginZipBuilder.PluginDllBytes));

        await Assert.ThrowsAsync<PluginArchiveException>(
            () => _installer.InstallAsync(
                zip,
                "evil.zip",
                zip.Length,
                confirmOlderVersion: false,
                TestContext.Current.CancellationToken));

        Assert.DoesNotContain(
            Directory.EnumerateDirectories(_pluginsDirectory.Path),
            static directory => !Path.GetFileName(directory).StartsWith('.'));
        AssertStagingIsClean();
    }

    [Fact]
    public async Task InstallAsync_ManifestlessUpdate_InheritsMetadataAndResources()
    {
        // Seed a version below the test assembly's own version so the DLL-only artifact
        // counts as an upgrade rather than a downgrade.
        var existing = SeedInstalledPlugin("Test Plugin_0.0.0.1", "0.0.0.1");
        var resource = Path.Combine(existing, "resource.txt");
        await File.WriteAllTextAsync(resource, "keep me", TestContext.Current.CancellationToken);
        using var zip = PluginZipBuilder.Build((PluginZipBuilder.PluginDllName, PluginZipBuilder.PluginDllBytes));

        var result = await _installer.InstallAsync(
            zip,
            "workflow-artifact.zip",
            zip.Length,
            confirmOlderVersion: false,
            TestContext.Current.CancellationToken);

        // The DLL-only artifact carries no meta.json, so the installed manifest is inherited
        // with its version rewritten to the uploaded assembly's version, and supporting
        // files from the old install are retained.
        Assert.Equal("Updated", result.Action);
        Assert.Equal(PluginZipBuilder.PluginDllVersion, result.Version);
        var metadata = await File.ReadAllTextAsync(
            Path.Combine(result.Directory, "meta.json"),
            TestContext.Current.CancellationToken);
        Assert.Contains(PluginZipBuilder.PluginDllVersion, metadata, StringComparison.Ordinal);
        Assert.Contains(_pluginGuid.ToString(), metadata, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("keep me", await File.ReadAllTextAsync(
            Path.Combine(result.Directory, "resource.txt"),
            TestContext.Current.CancellationToken));
        AssertStagingIsClean();
    }

    [Fact]
    public async Task InstallAsync_UnrelatedFolderWithTargetName_Rejected()
    {
        Directory.CreateDirectory(Path.Combine(_pluginsDirectory.Path, "Test Plugin_1.2.3.4"));
        using var zip = PluginZipBuilder.BuildPluginZip(_pluginGuid, "Test Plugin", "1.2.3.4");

        var exception = await Assert.ThrowsAsync<PluginArchiveException>(
            () => _installer.InstallAsync(
                zip,
                "test-plugin.zip",
                zip.Length,
                confirmOlderVersion: false,
                TestContext.Current.CancellationToken));

        Assert.Contains("already exists", exception.Message, StringComparison.OrdinalIgnoreCase);
        AssertStagingIsClean();
    }

    [Fact]
    public async Task InstallAsync_ZipWithoutPluginDll_Rejected()
    {
        using var zip = PluginZipBuilder.Build(("MediaBrowser.Model.dll", PluginZipBuilder.NonPluginDllBytes));

        await Assert.ThrowsAsync<PluginArchiveException>(
            () => _installer.InstallAsync(
                zip,
                "not-a-plugin.zip",
                zip.Length,
                confirmOlderVersion: false,
                TestContext.Current.CancellationToken));
        AssertStagingIsClean();
    }

    [Fact]
    public async Task InstallAsync_EmptyArchive_Rejected()
    {
        using var zip = PluginZipBuilder.Build();

        var exception = await Assert.ThrowsAsync<PluginArchiveException>(
            () => _installer.InstallAsync(
                zip,
                "empty.zip",
                zip.Length,
                confirmOlderVersion: false,
                TestContext.Current.CancellationToken));

        Assert.Contains("does not contain any files", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallAsync_DuplicateCaseDifferingEntries_Rejected()
    {
        using var zip = PluginZipBuilder.Build(
            ("Plugin.dll", PluginZipBuilder.PluginDllBytes),
            ("plugin.dll", PluginZipBuilder.PluginDllBytes));

        var exception = await Assert.ThrowsAsync<PluginArchiveException>(
            () => _installer.InstallAsync(
                zip,
                "duplicates.zip",
                zip.Length,
                confirmOlderVersion: false,
                TestContext.Current.CancellationToken));

        Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
        AssertStagingIsClean();
    }

    [Fact]
    public async Task InstallDllAsync_PluginDll_InstallsIntoVersionedFolder()
    {
        using var stream = new MemoryStream(PluginZipBuilder.PluginDllBytes);

        var result = await _installer.InstallDllAsync(
            stream,
            PluginZipBuilder.PluginDllName,
            stream.Length,
            confirmOlderVersion: false,
            TestContext.Current.CancellationToken);

        Assert.Equal("Installed", result.Action);
        Assert.Equal(PluginZipBuilder.PluginDllVersion, result.Version);
        Assert.EndsWith(
            $"{Path.GetFileNameWithoutExtension(PluginZipBuilder.PluginDllName)}_{PluginZipBuilder.PluginDllVersion}",
            result.Directory,
            StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(result.Directory, PluginZipBuilder.PluginDllName)));
        AssertStagingIsClean();
    }

    [Fact]
    public async Task InstallDllAsync_NonDllFileName_Rejected()
    {
        using var stream = new MemoryStream(PluginZipBuilder.PluginDllBytes);

        var exception = await Assert.ThrowsAsync<PluginArchiveException>(
            () => _installer.InstallDllAsync(
                stream,
                "notes.txt",
                stream.Length,
                confirmOlderVersion: false,
                TestContext.Current.CancellationToken));

        Assert.Contains(".dll", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallDllAsync_NonPluginAssembly_Rejected()
    {
        using var stream = new MemoryStream(PluginZipBuilder.NonPluginDllBytes);

        await Assert.ThrowsAsync<PluginArchiveException>(
            () => _installer.InstallDllAsync(
                stream,
                "MediaBrowser.Model.dll",
                stream.Length,
                confirmOlderVersion: false,
                TestContext.Current.CancellationToken));
        AssertStagingIsClean();
    }

    [Fact]
    public async Task InstallDllAsync_PeanutButterAssembly_Rejected()
    {
        using var stream = new MemoryStream(PluginZipBuilder.PeanutButterPluginDllBytes);

        var exception = await Assert.ThrowsAsync<PluginArchiveException>(
            () => _installer.InstallDllAsync(
                stream,
                PluginZipBuilder.PeanutButterPluginDllName,
                stream.Length,
                confirmOlderVersion: false,
                TestContext.Current.CancellationToken));

        Assert.Contains("cannot install or update itself", exception.Message, StringComparison.OrdinalIgnoreCase);
        AssertStagingIsClean();
    }

    private string SeedInstalledPlugin(string folderName, string version)
    {
        var directory = Path.Combine(_pluginsDirectory.Path, folderName);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(
            Path.Combine(directory, "meta.json"),
            PluginZipBuilder.MetaJson(_pluginGuid, "Test Plugin", version));
        File.WriteAllBytes(
            Path.Combine(directory, PluginZipBuilder.PluginDllName),
            PluginZipBuilder.PluginDllBytes);
        return directory;
    }

    private void AssertStagingIsClean()
    {
        var staging = Path.Combine(_stagingDirectory.Path, "staging");
        if (Directory.Exists(staging))
        {
            Assert.Empty(Directory.EnumerateFileSystemEntries(staging));
        }

        Assert.DoesNotContain(
            Directory.EnumerateDirectories(_pluginsDirectory.Path),
            directory => Path.GetFileName(directory).StartsWith('.'));
    }
}
