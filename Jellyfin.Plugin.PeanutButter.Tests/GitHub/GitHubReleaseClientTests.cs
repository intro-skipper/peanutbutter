using System.Net;
using System.Text;
using Jellyfin.Plugin.PeanutButter.Services.GitHub;
using Jellyfin.Plugin.PeanutButter.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.PeanutButter.Tests.GitHub;

public sealed class GitHubReleaseClientTests
{
    private const string ReleaseJson = /*lang=json,strict*/ """
        {
          "tag_name": "v1.2.3",
          "name": "Release 1.2.3",
          "prerelease": false,
          "published_at": "2026-01-01T00:00:00Z",
          "assets": [
            {
              "id": 42,
              "name": "plugin.zip",
              "size": 3,
              "content_type": "application/zip",
              "digest": "sha256:ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
            }
          ]
        }
        """;

    [Fact]
    public async Task GetReleaseAsync_ParsesReleaseDocument()
    {
        var handler = new FakeHttpMessageHandler(static _ => Json(ReleaseJson));
        var client = CreateClient(handler);

        var release = await client.GetReleaseAsync("owner", "repo", null, TestContext.Current.CancellationToken);

        Assert.Equal("v1.2.3", release.TagName);
        var asset = Assert.Single(release.Assets);
        Assert.Equal(42, asset.Id);
        Assert.Equal("plugin.zip", asset.Name);
        Assert.StartsWith("sha256:", asset.Digest, StringComparison.Ordinal);
        Assert.Equal(
            new Uri("https://api.github.com/repos/owner/repo/releases/latest"),
            Assert.Single(handler.RequestedUris));
    }

    [Fact]
    public async Task GetReleaseAsync_SendsRequiredHeaders()
    {
        var handler = new FakeHttpMessageHandler(static _ => Json(ReleaseJson));
        var client = CreateClient(handler);

        await client.GetReleaseAsync("owner", "repo", "v1.2.3", TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.NotEmpty(request.Headers.UserAgent);
        Assert.Contains(request.Headers.Accept, static accept => accept.MediaType == "application/vnd.github+json");
        Assert.Equal("2022-11-28", Assert.Single(request.Headers.GetValues("X-GitHub-Api-Version")));
        Assert.Equal(
            new Uri("https://api.github.com/repos/owner/repo/releases/tags/v1.2.3"),
            request.RequestUri);
    }

    [Fact]
    public async Task GetReleaseAsync_InvalidOwner_ThrowsWithoutRequest()
    {
        var handler = new FakeHttpMessageHandler(static _ => Json(ReleaseJson));
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<GitHubSourceException>(
            () => client.GetReleaseAsync("-bad-", "repo", null, TestContext.Current.CancellationToken));

        Assert.Equal(GitHubSourceFailureReason.InvalidRequest, exception.Reason);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GetReleaseAsync_NotFound_MapsToNotFound()
    {
        var handler = new FakeHttpMessageHandler(static _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<GitHubSourceException>(
            () => client.GetReleaseAsync("owner", "repo", null, TestContext.Current.CancellationToken));

        Assert.Equal(GitHubSourceFailureReason.NotFound, exception.Reason);
    }

    [Fact]
    public async Task GetReleaseAsync_RateLimited_MapsWithResetTime()
    {
        var handler = new FakeHttpMessageHandler(static _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.Add("x-ratelimit-remaining", "0");
            response.Headers.Add("x-ratelimit-reset", "1785200000");
            return response;
        });
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<GitHubSourceException>(
            () => client.GetReleaseAsync("owner", "repo", null, TestContext.Current.CancellationToken));

        Assert.Equal(GitHubSourceFailureReason.RateLimited, exception.Reason);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1785200000), exception.RateLimitResetsAt);
    }

    [Fact]
    public async Task GetReleaseAsync_MalformedJson_MapsToInvalidResponse()
    {
        var handler = new FakeHttpMessageHandler(static _ => Json("{ not json"));
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<GitHubSourceException>(
            () => client.GetReleaseAsync("owner", "repo", null, TestContext.Current.CancellationToken));

        Assert.Equal(GitHubSourceFailureReason.InvalidResponse, exception.Reason);
    }

    [Fact]
    public async Task DownloadAssetAsync_FollowsRedirectToGitHubCdn()
    {
        using var temp = new TempDirectory();
        var handler = new FakeHttpMessageHandler(static request =>
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://release-assets.githubusercontent.com/asset?sig=secret");
                return redirect;
            }

            return Bytes("abc"u8.ToArray());
        });
        var client = CreateClient(handler);

        await using var downloaded = await client.DownloadAssetAsync(
            "owner",
            "repo",
            DigestedAsset(),
            temp.Path,
            TestContext.Current.CancellationToken);

        Assert.Equal(3, downloaded.Length);
        Assert.True(downloaded.DigestVerified);
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", downloaded.Sha256Hex);
        Assert.Equal("abc", await File.ReadAllTextAsync(downloaded.FilePath, TestContext.Current.CancellationToken));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("release-assets.githubusercontent.com", handler.RequestedUris[1]!.Host);
    }

    [Fact]
    public async Task DownloadAssetAsync_RedirectToForeignHost_Rejected()
    {
        using var temp = new TempDirectory();
        var handler = new FakeHttpMessageHandler(static _ =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("https://evil.example.com/payload");
            return redirect;
        });
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<GitHubSourceException>(
            () => client.DownloadAssetAsync("owner", "repo", DigestedAsset(), temp.Path, TestContext.Current.CancellationToken));

        Assert.Equal(GitHubSourceFailureReason.RedirectRejected, exception.Reason);
        Assert.Empty(Directory.EnumerateFiles(temp.Path));
    }

    [Fact]
    public async Task DownloadAssetAsync_TooManyRedirects_Rejected()
    {
        using var temp = new TempDirectory();
        var handler = new FakeHttpMessageHandler(static _ =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("https://objects.githubusercontent.com/loop");
            return redirect;
        });
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<GitHubSourceException>(
            () => client.DownloadAssetAsync("owner", "repo", DigestedAsset(), temp.Path, TestContext.Current.CancellationToken));

        Assert.Equal(GitHubSourceFailureReason.RedirectRejected, exception.Reason);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task DownloadAssetAsync_StreamLargerThanCap_AbortedByCountedBytes()
    {
        using var temp = new TempDirectory();
        var handler = new FakeHttpMessageHandler(static _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            // No Content-Length: the stream just keeps producing bytes past the cap.
            Content = new StreamContent(new ZeroStream(Services.PluginZipInstaller.MaximumUploadBytes + 1024))
        });
        var client = CreateClient(handler);
        var asset = new GitHubReleaseAsset { Id = 42, Name = "plugin.zip", Size = 1024 };

        var exception = await Assert.ThrowsAsync<GitHubSourceException>(
            () => client.DownloadAssetAsync("owner", "repo", asset, temp.Path, TestContext.Current.CancellationToken));

        Assert.Equal(GitHubSourceFailureReason.TooLarge, exception.Reason);
        Assert.Empty(Directory.EnumerateFiles(temp.Path));
    }

    [Fact]
    public async Task DownloadAssetAsync_OversizeAssetMetadata_RejectedBeforeRequest()
    {
        using var temp = new TempDirectory();
        var handler = new FakeHttpMessageHandler(static _ => Bytes([1]));
        var client = CreateClient(handler);
        var asset = new GitHubReleaseAsset
        {
            Id = 42,
            Name = "plugin.zip",
            Size = Services.PluginZipInstaller.MaximumUploadBytes + 1
        };

        var exception = await Assert.ThrowsAsync<GitHubSourceException>(
            () => client.DownloadAssetAsync("owner", "repo", asset, temp.Path, TestContext.Current.CancellationToken));

        Assert.Equal(GitHubSourceFailureReason.TooLarge, exception.Reason);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task DownloadAssetAsync_DigestMismatch_ThrowsAndDeletesFile()
    {
        using var temp = new TempDirectory();
        var handler = new FakeHttpMessageHandler(static _ => Bytes("tampered"u8.ToArray()));
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<GitHubSourceException>(
            () => client.DownloadAssetAsync("owner", "repo", DigestedAsset(), temp.Path, TestContext.Current.CancellationToken));

        Assert.Equal(GitHubSourceFailureReason.DigestMismatch, exception.Reason);
        Assert.Empty(Directory.EnumerateFiles(temp.Path));
    }

    [Fact]
    public async Task DownloadAssetAsync_NoDigest_SucceedsUnverified()
    {
        using var temp = new TempDirectory();
        var handler = new FakeHttpMessageHandler(static _ => Bytes("abc"u8.ToArray()));
        var client = CreateClient(handler);
        var asset = new GitHubReleaseAsset { Id = 42, Name = "plugin.zip", Size = 3 };

        await using var downloaded = await client.DownloadAssetAsync(
            "owner",
            "repo",
            asset,
            temp.Path,
            TestContext.Current.CancellationToken);

        Assert.False(downloaded.DigestVerified);
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", downloaded.Sha256Hex);
    }

    [Fact]
    public async Task DownloadedAsset_DisposeDeletesFile()
    {
        using var temp = new TempDirectory();
        var handler = new FakeHttpMessageHandler(static _ => Bytes("abc"u8.ToArray()));
        var client = CreateClient(handler);

        var downloaded = await client.DownloadAssetAsync(
            "owner",
            "repo",
            DigestedAsset(),
            temp.Path,
            TestContext.Current.CancellationToken);
        Assert.True(File.Exists(downloaded.FilePath));

        await downloaded.DisposeAsync();

        Assert.False(File.Exists(downloaded.FilePath));
    }

    private static GitHubReleaseClient CreateClient(FakeHttpMessageHandler handler)
        => new(new HttpClient(handler, disposeHandler: false), NullLogger<GitHubReleaseClient>.Instance);

    private static GitHubReleaseAsset DigestedAsset()
        => new()
        {
            Id = 42,
            Name = "plugin.zip",
            Size = 3,
            Digest = "sha256:ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        };

    private static HttpResponseMessage Json(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage Bytes(byte[] content)
        => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content)
        };

    /// <summary>A read-only stream of zeros with a fixed length and no backing allocation.</summary>
    private sealed class ZeroStream : Stream
    {
        private readonly long _length;
        private long _position;

        public ZeroStream(long length)
        {
            _length = length;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = _length - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            var toRead = (int)Math.Min(count, remaining);
            Array.Clear(buffer, offset, toRead);
            _position += toRead;
            return toRead;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
