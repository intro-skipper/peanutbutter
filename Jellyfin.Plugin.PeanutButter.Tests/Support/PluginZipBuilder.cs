using System.IO.Compression;
using System.Text;

namespace Jellyfin.Plugin.PeanutButter.Tests.Support;

/// <summary>
/// Builds plugin ZIP archives in memory for installer tests. The compiled plugin assembly
/// from the test output is a genuine Jellyfin plugin (it contains a public <c>IPlugin</c>
/// implementor), so it doubles as the standard valid payload; <c>MediaBrowser.Model.dll</c>
/// is a valid managed assembly without a plugin type and serves as the rejection payload.
/// </summary>
public static class PluginZipBuilder
{
    /// <summary>The file name of the valid plugin payload assembly.</summary>
    public const string PluginDllName = "Jellyfin.Plugin.PeanutButter.Tests.dll";

    /// <summary>The file name of the Peanut Butter assembly used by self-install tests.</summary>
    public const string PeanutButterPluginDllName = "Jellyfin.Plugin.PeanutButter.dll";

    /// <summary>Gets the bytes of the compiled plugin assembly from the test output.</summary>
    public static byte[] PluginDllBytes { get; } = File.ReadAllBytes(
        Path.Combine(AppContext.BaseDirectory, PluginDllName));

    /// <summary>Gets the actual Peanut Butter assembly for self-install rejection tests.</summary>
    public static byte[] PeanutButterPluginDllBytes { get; } = File.ReadAllBytes(
        Path.Combine(AppContext.BaseDirectory, PeanutButterPluginDllName));

    /// <summary>Gets the bytes of a managed assembly that is not a Jellyfin plugin.</summary>
    public static byte[] NonPluginDllBytes { get; } = File.ReadAllBytes(
        Path.Combine(AppContext.BaseDirectory, "MediaBrowser.Model.dll"));

    /// <summary>Gets the version of the compiled plugin assembly, as the installer reports it.</summary>
    public static string PluginDllVersion { get; } =
        System.Reflection.AssemblyName.GetAssemblyName(
            Path.Combine(AppContext.BaseDirectory, PluginDllName)).Version!.ToString();

    /// <summary>Builds a ZIP archive from (path, content) entries.</summary>
    public static MemoryStream Build(params (string Path, byte[] Content)[] entries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                var entry = archive.CreateEntry(path);
                using var entryStream = entry.Open();
                entryStream.Write(content, 0, content.Length);
            }
        }

        stream.Position = 0;
        return stream;
    }

    /// <summary>Builds a standard plugin ZIP: a <c>meta.json</c> plus the valid plugin DLL.</summary>
    public static MemoryStream BuildPluginZip(Guid guid, string name, string version)
        => Build(
            ("meta.json", MetaJson(guid, name, version)),
            (PluginDllName, PluginDllBytes));

    /// <summary>Builds a manifest <c>meta.json</c> body.</summary>
    public static byte[] MetaJson(Guid guid, string name, string version)
        => Encoding.UTF8.GetBytes(
            $$"""
            {
              "guid": "{{guid}}",
              "name": "{{name}}",
              "version": "{{version}}",
              "assemblies": ["{{PluginDllName}}"]
            }
            """);
}
