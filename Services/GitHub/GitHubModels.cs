using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.PeanutButter.Services.GitHub;

/// <summary>
/// The subset of GitHub's release API response consumed by this plugin.
/// Unknown members are ignored; missing members fall back to safe defaults.
/// </summary>
public sealed class GitHubRelease
{
    /// <summary>Gets the git tag the release was created from.</summary>
    [JsonPropertyName("tag_name")]
    public string TagName { get; init; } = string.Empty;

    /// <summary>Gets the human-readable release title, when set.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Gets a value indicating whether the release is marked as a prerelease.</summary>
    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; init; }

    /// <summary>Gets the publication timestamp, when available.</summary>
    [JsonPropertyName("published_at")]
    public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>Gets the downloadable assets attached to the release.</summary>
    [JsonPropertyName("assets")]
    public IReadOnlyList<GitHubReleaseAsset> Assets { get; init; } = [];
}

/// <summary>
/// A single release asset as reported by GitHub's release API.
/// </summary>
public sealed class GitHubReleaseAsset
{
    /// <summary>Gets GitHub's stable identifier for the asset.</summary>
    [JsonPropertyName("id")]
    public long Id { get; init; }

    /// <summary>Gets the asset file name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets the asset size in bytes as reported by GitHub.</summary>
    [JsonPropertyName("size")]
    public long Size { get; init; }

    /// <summary>Gets the media type GitHub recorded for the asset, when available.</summary>
    [JsonPropertyName("content_type")]
    public string? ContentType { get; init; }

    /// <summary>
    /// Gets the content digest in <c>sha256:&lt;hex&gt;</c> form. Only populated for assets
    /// uploaded after GitHub introduced the field in 2025; absent digests degrade the
    /// download to an unverified (but still fully validated) install.
    /// </summary>
    [JsonPropertyName("digest")]
    public string? Digest { get; init; }
}

/// <summary>
/// A release asset downloaded to a local temporary file. Disposing the handle deletes the
/// file, so callers must keep it alive until installation has consumed the content.
/// </summary>
public sealed class GitHubDownloadedAsset : IAsyncDisposable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="GitHubDownloadedAsset"/> class.
    /// </summary>
    /// <param name="filePath">The temporary file holding the downloaded bytes.</param>
    /// <param name="name">The asset file name.</param>
    /// <param name="length">The number of bytes actually downloaded.</param>
    /// <param name="sha256Hex">The lowercase hex SHA-256 of the downloaded bytes.</param>
    /// <param name="digestVerified">Whether the download matched a digest GitHub published.</param>
    public GitHubDownloadedAsset(string filePath, string name, long length, string sha256Hex, bool digestVerified)
    {
        FilePath = filePath;
        Name = name;
        Length = length;
        Sha256Hex = sha256Hex;
        DigestVerified = digestVerified;
    }

    /// <summary>Gets the temporary file holding the downloaded bytes.</summary>
    public string FilePath { get; }

    /// <summary>Gets the asset file name.</summary>
    public string Name { get; }

    /// <summary>Gets the number of bytes actually downloaded.</summary>
    public long Length { get; }

    /// <summary>Gets the lowercase hex SHA-256 of the downloaded bytes.</summary>
    public string Sha256Hex { get; }

    /// <summary>Gets a value indicating whether the download matched a digest GitHub published for the asset.</summary>
    public bool DigestVerified { get; }

    /// <summary>Opens the downloaded file for sequential shared reading.</summary>
    /// <returns>A read-only stream over the downloaded bytes.</returns>
    public FileStream OpenRead()
        => new(
            FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; an undeletable temp file must not fail the operation.
        }
        catch (UnauthorizedAccessException)
        {
        }

        return ValueTask.CompletedTask;
    }
}
