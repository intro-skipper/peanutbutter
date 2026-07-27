using Jellyfin.Plugin.PeanutButter.Services;
using Xunit;

namespace Jellyfin.Plugin.PeanutButter.Tests.Installer;

public sealed class PluginZipInstallerHelperTests
{
    [Theory]
    [InlineData("folder\\file.dll", "folder/file.dll")]
    [InlineData("a//b.dll", "a/b.dll")]
    [InlineData("a/b.dll", "a/b.dll")]
    [InlineData("plugin.dll", "plugin.dll")]
    public void NormalizeArchivePath_AcceptsAndNormalizesSafePaths(string input, string expected)
    {
        Assert.Equal(expected, PluginZipInstaller.NormalizeArchivePath(input));
    }

    [Theory]
    [InlineData("/absolute.dll")]
    [InlineData("C:evil.dll")]
    [InlineData("a\0b.dll")]
    [InlineData("a/../b.dll")]
    [InlineData("./a.dll")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeArchivePath_RejectsUnsafePaths(string input)
    {
        Assert.Throws<PluginArchiveException>(() => PluginZipInstaller.NormalizeArchivePath(input));
    }

    [Fact]
    public void FindCommonRootPrefix_SingleCommonRoot_ReturnsPrefix()
    {
        var result = PluginZipInstaller.FindCommonRootPrefix(["root/a.dll", "root/sub/b.dll"]);
        Assert.Equal("root/", result);
    }

    [Fact]
    public void FindCommonRootPrefix_CaseInsensitiveRootMatch_ReturnsFirstSpelling()
    {
        var result = PluginZipInstaller.FindCommonRootPrefix(["Root/a.dll", "root/b.dll"]);
        Assert.Equal("Root/", result);
    }

    [Fact]
    public void FindCommonRootPrefix_MixedRoots_ReturnsEmpty()
    {
        var result = PluginZipInstaller.FindCommonRootPrefix(["root/a.dll", "other/b.dll"]);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void FindCommonRootPrefix_RootLevelFile_ReturnsEmpty()
    {
        var result = PluginZipInstaller.FindCommonRootPrefix(["a.dll", "root/b.dll"]);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void FindCommonRootPrefix_EmptyInput_ReturnsEmpty()
    {
        var result = PluginZipInstaller.FindCommonRootPrefix([]);
        Assert.Equal(string.Empty, result);
    }

    [Theory]
    [InlineData("My/Plugin", "My_Plugin")]
    [InlineData(" name ", "name")]
    [InlineData("name...", "name")]
    [InlineData(".", "")]
    [InlineData("..", "")]
    [InlineData("...", "")]
    public void SanitizeFolderName_SanitizesInput(string input, string expected)
    {
        Assert.Equal(expected, PluginZipInstaller.SanitizeFolderName(input));
    }

    [Fact]
    public void SanitizeFolderName_TruncatesTo120Characters()
    {
        var input = new string('a', 130);
        Assert.Equal(new string('a', 120), PluginZipInstaller.SanitizeFolderName(input));
    }

    [Fact]
    public void ParsePluginVersion_MetadataVersionWins()
    {
        var version = PluginZipInstaller.ParsePluginVersion("1.2.3.4", "Plugin_9.9.9.9");
        Assert.Equal(new Version(1, 2, 3, 4), version);
    }

    [Fact]
    public void ParsePluginVersion_FallsBackToDirectorySuffix()
    {
        var version = PluginZipInstaller.ParsePluginVersion("not-a-version", "Plugin_2.0.1.0");
        Assert.Equal(new Version(2, 0, 1, 0), version);
    }

    [Fact]
    public void ParsePluginVersion_NoParsableVersion_ReturnsZero()
    {
        var version = PluginZipInstaller.ParsePluginVersion(string.Empty, "PluginWithoutVersion");
        Assert.Equal(new Version(0, 0, 0, 0), version);
    }
}
