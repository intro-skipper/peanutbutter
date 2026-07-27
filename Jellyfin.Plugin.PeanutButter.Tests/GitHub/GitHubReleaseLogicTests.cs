using Jellyfin.Plugin.PeanutButter.Services.GitHub;
using Xunit;

namespace Jellyfin.Plugin.PeanutButter.Tests.GitHub;

public sealed class GitHubReleaseLogicTests
{
    [Theory]
    [InlineData("intro-skipper")]
    [InlineData("a")]
    [InlineData("user123")]
    [InlineData("a-b-c")]
    public void IsValidOwner_AcceptsGitHubLogins(string owner)
    {
        Assert.True(GitHubReleaseLogic.IsValidOwner(owner));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("double--hyphen")]
    [InlineData("has space")]
    [InlineData("owner/extra")]
    [InlineData("ThisNameIsWayTooLongForGitHubBecauseItExceedsThirtyNine")]
    public void IsValidOwner_RejectsInvalidLogins(string owner)
    {
        Assert.False(GitHubReleaseLogic.IsValidOwner(owner));
    }

    [Theory]
    [InlineData("intro-skipper")]
    [InlineData("jellyfin_plugin.repo")]
    [InlineData("a")]
    public void IsValidRepo_AcceptsRepositoryNames(string repo)
    {
        Assert.True(GitHubReleaseLogic.IsValidRepo(repo));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("has space")]
    [InlineData("path/segment")]
    public void IsValidRepo_RejectsInvalidNames(string repo)
    {
        Assert.False(GitHubReleaseLogic.IsValidRepo(repo));
    }

    [Theory]
    [InlineData("v1.2.3")]
    [InlineData("2.1.0.0")]
    [InlineData("12.0/v1.12.0.1")]
    [InlineData("release-2024_final+build")]
    public void IsValidTag_AcceptsRealWorldTags(string tag)
    {
        Assert.True(GitHubReleaseLogic.IsValidTag(tag));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a..b")]
    [InlineData("v1.0.lock")]
    [InlineData("-leading")]
    [InlineData(".hidden")]
    [InlineData("has space")]
    [InlineData("tag~name")]
    [InlineData("tag:name")]
    public void IsValidTag_RejectsUnsafeTags(string tag)
    {
        Assert.False(GitHubReleaseLogic.IsValidTag(tag));
    }

    [Fact]
    public void IsValidTag_RejectsOverlongTags()
    {
        Assert.False(GitHubReleaseLogic.IsValidTag(new string('a', 129)));
    }

    [Theory]
    [InlineData("owner/repo", "owner", "repo")]
    [InlineData("  owner/repo  ", "owner", "repo")]
    [InlineData("https://github.com/owner/repo", "owner", "repo")]
    [InlineData("https://github.com/owner/repo.git", "owner", "repo")]
    [InlineData("https://github.com/owner/repo/releases/tag/v1.0.0", "owner", "repo")]
    [InlineData("https://www.github.com/owner/repo", "owner", "repo")]
    [InlineData("github.com/owner/repo", "owner", "repo")]
    public void TryParseRepository_ParsesSupportedForms(string input, string expectedOwner, string expectedRepo)
    {
        Assert.True(GitHubReleaseLogic.TryParseRepository(input, out var owner, out var repo));
        Assert.Equal(expectedOwner, owner);
        Assert.Equal(expectedRepo, repo);
    }

    [Theory]
    [InlineData("")]
    [InlineData("owner")]
    [InlineData("owner/repo/extra")]
    [InlineData("https://gitlab.com/owner/repo")]
    [InlineData("https://github.com.evil.com/owner/repo")]
    [InlineData("https://user@github.com/owner/repo")]
    [InlineData("https://github.com/-bad/repo")]
    [InlineData("https://github.com/owner/..")]
    public void TryParseRepository_RejectsUnsupportedForms(string input)
    {
        Assert.False(GitHubReleaseLogic.TryParseRepository(input, out _, out _));
    }

    [Fact]
    public void BuildReleaseUri_EncodesSlashTags()
    {
        var uri = GitHubReleaseLogic.BuildReleaseUri("owner", "repo", "12.0/v1.12.0.1");
        Assert.Equal("https://api.github.com/repos/owner/repo/releases/tags/12.0%2Fv1.12.0.1", uri.AbsoluteUri);
    }

    [Fact]
    public void BuildReleaseUri_NoTag_TargetsLatest()
    {
        var uri = GitHubReleaseLogic.BuildReleaseUri("owner", "repo", null);
        Assert.Equal("https://api.github.com/repos/owner/repo/releases/latest", uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("https://api.github.com/repos/o/r/releases/assets/1")]
    [InlineData("https://objects.githubusercontent.com/some/path?sig=abc")]
    [InlineData("https://release-assets.githubusercontent.com/some/path")]
    public void IsAllowedDownloadUri_AcceptsGitHubHosts(string uri)
    {
        Assert.True(GitHubReleaseLogic.IsAllowedDownloadUri(new Uri(uri)));
    }

    [Theory]
    [InlineData("http://api.github.com/insecure")]
    [InlineData("https://evil.com/payload")]
    [InlineData("https://api.github.com.evil.com/path")]
    [InlineData("https://evilgithubusercontent.com/path")]
    [InlineData("https://githubusercontent.com/path")]
    [InlineData("https://api.github.com@evil.com/path")]
    [InlineData("https://api.github.com:8443/path")]
    [InlineData("https://192.0.2.1/path")]
    [InlineData("https://[2001:db8::1]/path")]
    public void IsAllowedDownloadUri_RejectsEverythingElse(string uri)
    {
        Assert.False(GitHubReleaseLogic.IsAllowedDownloadUri(new Uri(uri)));
    }

    [Fact]
    public void IsAllowedDownloadUri_RejectsNull()
    {
        Assert.False(GitHubReleaseLogic.IsAllowedDownloadUri(null));
    }

    [Theory]
    [InlineData("v4.0.0.3", "4.0.0.3")]
    [InlineData("2.1.0.0", "2.1.0.0")]
    [InlineData("12.0/v1.12.0.1", "1.12.0.1")]
    [InlineData("V1.2", "1.2.0.0")]
    [InlineData("1.2.3", "1.2.3.0")]
    public void TryExtractVersionFromTag_ExtractsComparableVersions(string tag, string expected)
    {
        Assert.True(GitHubReleaseLogic.TryExtractVersionFromTag(tag, out var version));
        Assert.Equal(Version.Parse(expected), version);
    }

    [Theory]
    [InlineData("release-2024")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("v")]
    [InlineData("vNext")]
    public void TryExtractVersionFromTag_RejectsVersionlessTags(string tag)
    {
        Assert.False(GitHubReleaseLogic.TryExtractVersionFromTag(tag, out _));
    }

    [Fact]
    public void TryParseSha256Digest_ParsesAndLowercases()
    {
        var hex = new string('A', 64);
        Assert.True(GitHubReleaseLogic.TryParseSha256Digest("sha256:" + hex, out var parsed));
        Assert.Equal(new string('a', 64), parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha1:abcdef")]
    [InlineData("sha256:tooshort")]
    [InlineData("sha256:")]
    public void TryParseSha256Digest_RejectsOtherForms(string? digest)
    {
        Assert.False(GitHubReleaseLogic.TryParseSha256Digest(digest, out _));
    }

    [Fact]
    public void TryParseSha256Digest_RejectsNonHexCharacters()
    {
        Assert.False(GitHubReleaseLogic.TryParseSha256Digest("sha256:" + new string('g', 64), out _));
    }

    [Theory]
    [InlineData("1.0.0.0", "v1.0.1", true)]
    [InlineData("1.0.1.0", "v1.0.1", false)]
    [InlineData("2.0.0.0", "v1.9.9", false)]
    [InlineData("1.2.3.0", "v1.2.3", false)]
    public void DetermineUpdate_ComparesParsedVersions(string recordedVersion, string latestTag, bool expected)
    {
        var decision = GitHubReleaseLogic.DetermineUpdate(recordedVersion, "old-tag", string.Empty, latestTag, null);
        Assert.Equal("version", decision.ComparisonMethod);
        Assert.Equal(expected, decision.UpdateAvailable);
    }

    [Fact]
    public void DetermineUpdate_UnparseableVersions_FallsBackToTagInequality()
    {
        var decision = GitHubReleaseLogic.DetermineUpdate("garbage", "build-a", string.Empty, "build-b", null);
        Assert.Equal("tag", decision.ComparisonMethod);
        Assert.True(decision.UpdateAvailable);
    }

    [Fact]
    public void DetermineUpdate_SameTag_NoUpdate()
    {
        var decision = GitHubReleaseLogic.DetermineUpdate("garbage", "build-a", string.Empty, "build-a", null);
        Assert.False(decision.UpdateAvailable);
    }

    [Fact]
    public void DetermineUpdate_EqualDigests_VetoTagRename()
    {
        var hex = new string('a', 64);
        var decision = GitHubReleaseLogic.DetermineUpdate("garbage", "build-a", hex, "renamed", "sha256:" + hex);
        Assert.Equal("tag", decision.ComparisonMethod);
        Assert.False(decision.UpdateAvailable);
    }

    [Fact]
    public void DetermineUpdate_DifferentDigests_TagChangeIsUpdate()
    {
        var decision = GitHubReleaseLogic.DetermineUpdate(
            "garbage",
            "build-a",
            new string('a', 64),
            "build-b",
            "sha256:" + new string('b', 64));
        Assert.True(decision.UpdateAvailable);
    }

    [Theory]
    [InlineData("plugin.zip", GitHubAssetKind.Zip)]
    [InlineData("Plugin.DLL", GitHubAssetKind.Dll)]
    [InlineData("plugin.zip.md5", GitHubAssetKind.Other)]
    [InlineData("plugin.sha256", GitHubAssetKind.Other)]
    [InlineData("notes.txt", GitHubAssetKind.Other)]
    [InlineData("source.tar.gz", GitHubAssetKind.Other)]
    public void ClassifyAsset_ClassifiesByExtension(string name, GitHubAssetKind expected)
    {
        Assert.Equal(expected, GitHubReleaseLogic.ClassifyAsset(name));
    }

    [Fact]
    public void IsInstallable_RejectsOversizeAndEmptyAssets()
    {
        Assert.False(GitHubReleaseLogic.IsInstallable(MakeAsset(1, "plugin.zip", 0)));
        Assert.False(GitHubReleaseLogic.IsInstallable(MakeAsset(1, "plugin.zip", Services.PluginZipInstaller.MaximumUploadBytes + 1)));
        Assert.True(GitHubReleaseLogic.IsInstallable(MakeAsset(1, "plugin.zip", 1024)));
    }

    [Fact]
    public void SelectRecommendedAsset_SoleInstallable_Selected()
    {
        var assets = new[]
        {
            MakeAsset(1, "plugin.zip", 1024),
            MakeAsset(2, "plugin.zip.md5", 32),
        };
        Assert.Equal(1L, GitHubReleaseLogic.SelectRecommendedAsset(assets, "unrelated"));
    }

    [Fact]
    public void SelectRecommendedAsset_SoleZipAmongMany_Selected()
    {
        var assets = new[]
        {
            MakeAsset(1, "plugin.zip", 1024),
            MakeAsset(2, "standalone.dll", 512),
        };
        Assert.Equal(1L, GitHubReleaseLogic.SelectRecommendedAsset(assets, "unrelated"));
    }

    [Fact]
    public void SelectRecommendedAsset_RepoNamedZip_PreferredShortestFirst()
    {
        var assets = new[]
        {
            MakeAsset(1, "intro-skipper-v1.12.0.1-debug.zip", 1024),
            MakeAsset(2, "intro-skipper-v1.12.0.1.zip", 1024),
            MakeAsset(3, "other-tool.zip", 1024),
            MakeAsset(4, "helper.dll", 128),
        };
        Assert.Equal(2L, GitHubReleaseLogic.SelectRecommendedAsset(assets, "intro-skipper"));
    }

    [Fact]
    public void SelectRecommendedAsset_Ambiguous_ReturnsNull()
    {
        var assets = new[]
        {
            MakeAsset(1, "variant-a.zip", 1024),
            MakeAsset(2, "variant-b.zip", 1024),
        };
        Assert.Null(GitHubReleaseLogic.SelectRecommendedAsset(assets, "unrelated"));
    }

    [Fact]
    public void SelectRecommendedAsset_NoAssets_ReturnsNull()
    {
        Assert.Null(GitHubReleaseLogic.SelectRecommendedAsset([], "repo"));
    }

    [Theory]
    [InlineData("MyPlugin_1.2.3.4", "MyPlugin")]
    [InlineData("MyPlugin_1.2", "MyPlugin")]
    [InlineData("My_Plugin_2.0.0.0", "My_Plugin")]
    [InlineData("MyPlugin", "MyPlugin")]
    [InlineData("MyPlugin_notaversion", "MyPlugin_notaversion")]
    [InlineData("_1.2.3.4", "_1.2.3.4")]
    public void StripVersionFolderSuffix_RemovesOnlyVersionSuffixes(string folderName, string expected)
    {
        Assert.Equal(expected, GitHubReleaseLogic.StripVersionFolderSuffix(folderName));
    }

    private static GitHubReleaseAsset MakeAsset(long id, string name, long size)
        => new()
        {
            Id = id,
            Name = name,
            Size = size,
        };
}
