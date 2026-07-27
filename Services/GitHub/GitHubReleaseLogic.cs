using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.PeanutButter.Services.GitHub;

/// <summary>
/// How a release asset is classified for installation.
/// </summary>
public enum GitHubAssetKind
{
    /// <summary>A plugin ZIP archive.</summary>
    Zip,

    /// <summary>A standalone plugin DLL.</summary>
    Dll,

    /// <summary>Anything else (checksums, signatures, tarballs, ...); never installable.</summary>
    Other
}

/// <summary>
/// Pure validation, parsing, and selection logic for the GitHub install feature. Everything
/// here is deterministic and free of I/O so it can be unit tested exhaustively. The
/// validation functions are the security boundary for values that end up in request URIs:
/// the controller's model validation produces friendly 400s, but these are re-checked here.
/// </summary>
internal static partial class GitHubReleaseLogic
{
    private const int MaximumTagLength = 128;

    /// <summary>
    /// Validates a GitHub account name (user or organization) against GitHub's own rules:
    /// alphanumeric with single interior hyphens, at most 39 characters.
    /// </summary>
    internal static bool IsValidOwner(string? owner)
        => !string.IsNullOrEmpty(owner) && OwnerRegex().IsMatch(owner);

    /// <summary>
    /// Validates a repository name: GitHub's charset, at most 100 characters, and never a
    /// path-navigation literal (<c>.</c> / <c>..</c>) that would survive URL normalization.
    /// </summary>
    internal static bool IsValidRepo(string? repo)
        => !string.IsNullOrEmpty(repo)
            && repo is not ("." or "..")
            && RepoRegex().IsMatch(repo);

    /// <summary>
    /// Validates a git tag name conservatively. Slash-separated segments are allowed
    /// (branch-prefixed tags such as <c>12.0/v1.12.0.1</c> exist in the wild); each segment
    /// must start alphanumeric and stay within a safe charset, and the tag may not contain
    /// <c>..</c> or end in <c>.lock</c> (both forbidden by git and dangerous in URLs).
    /// </summary>
    internal static bool IsValidTag(string? tag)
    {
        if (string.IsNullOrEmpty(tag)
            || tag.Length > MaximumTagLength
            || tag.Contains("..", StringComparison.Ordinal)
            || tag.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = tag.Split('/');
        return segments.Length > 0
            && segments.All(static segment => TagSegmentRegex().IsMatch(segment));
    }

    /// <summary>
    /// Parses an administrator-supplied repository reference: either <c>owner/repo</c> or a
    /// pasted <c>github.com</c> URL (optionally with a <c>.git</c> suffix or extra path such
    /// as <c>/releases/tag/x</c>). Only the owner and repository are ever taken from the
    /// input; no URL from the client is fetched.
    /// </summary>
    internal static bool TryParseRepository(string? input, out string owner, out string repo)
    {
        owner = string.Empty;
        repo = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var trimmed = input.Trim();
        string[] pathSegments;
        if (trimmed.Contains("github.com", StringComparison.OrdinalIgnoreCase))
        {
            if (!trimmed.Contains("://", StringComparison.Ordinal))
            {
                trimmed = "https://" + trimmed;
            }

            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
                || uri.Host is not ("github.com" or "www.github.com")
                || !string.IsNullOrEmpty(uri.UserInfo))
            {
                return false;
            }

            pathSegments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        }
        else
        {
            pathSegments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (pathSegments.Length != 2)
            {
                return false;
            }
        }

        if (pathSegments.Length < 2)
        {
            return false;
        }

        var candidateOwner = pathSegments[0];
        var candidateRepo = pathSegments[1];
        if (candidateRepo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            candidateRepo = candidateRepo[..^4];
        }

        if (!IsValidOwner(candidateOwner) || !IsValidRepo(candidateRepo))
        {
            return false;
        }

        owner = candidateOwner;
        repo = candidateRepo;
        return true;
    }

    /// <summary>
    /// Builds the release-metadata URI for validated coordinates: the latest release when
    /// <paramref name="tag"/> is null, otherwise the release for that exact tag.
    /// </summary>
    internal static Uri BuildReleaseUri(string owner, string repo, string? tag)
    {
        var basePath = $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/releases/";
        return tag is null
            ? new Uri(basePath + "latest")
            : new Uri(basePath + "tags/" + Uri.EscapeDataString(tag));
    }

    /// <summary>Builds the asset-download URI for validated coordinates.</summary>
    internal static Uri BuildAssetUri(string owner, string repo, long assetId)
        => new($"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/releases/assets/{assetId}");

    /// <summary>
    /// Decides whether a URI may be requested during the download flow. GitHub asset
    /// downloads redirect from <c>api.github.com</c> to a <c>*.githubusercontent.com</c>
    /// CDN host (currently <c>release-assets.githubusercontent.com</c>; formerly
    /// <c>objects.githubusercontent.com</c>) — the suffix rule keeps future GitHub CDN
    /// moves working while still rejecting every non-GitHub host.
    /// </summary>
    internal static bool IsAllowedDownloadUri(Uri? uri)
        => uri is not null
            && uri.IsAbsoluteUri
            && uri.Scheme == Uri.UriSchemeHttps
            && uri.HostNameType == UriHostNameType.Dns
            && string.IsNullOrEmpty(uri.UserInfo)
            && uri.IsDefaultPort
            && (string.Equals(uri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Extracts a comparable <see cref="Version"/> from a release tag: the segment after the
    /// last slash, minus a leading <c>v</c>, padded to four components so <c>1.2.3</c>
    /// compares equal to <c>1.2.3.0</c>. Returns false for tags without a parseable version.
    /// </summary>
    internal static bool TryExtractVersionFromTag(string? tag, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        var candidate = tag.Trim();
        var lastSlash = candidate.LastIndexOf('/');
        if (lastSlash >= 0)
        {
            candidate = candidate[(lastSlash + 1)..];
        }

        if (candidate.Length > 1
            && (candidate[0] is 'v' or 'V')
            && char.IsAsciiDigit(candidate[1]))
        {
            candidate = candidate[1..];
        }

        if (!Version.TryParse(candidate, out var parsed))
        {
            return false;
        }

        version = Normalize(parsed);
        return true;
    }

    /// <summary>
    /// Parses GitHub's asset digest field. Only <c>sha256:</c> followed by exactly 64 hex
    /// characters is accepted; anything else is treated as "no digest available".
    /// </summary>
    internal static bool TryParseSha256Digest(string? digest, out string sha256Hex)
    {
        sha256Hex = string.Empty;
        const string Prefix = "sha256:";
        if (digest is null
            || !digest.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            || digest.Length != Prefix.Length + 64)
        {
            return false;
        }

        var hex = digest[Prefix.Length..];
        if (!hex.All(char.IsAsciiHexDigit))
        {
            return false;
        }

        sha256Hex = hex.ToLowerInvariant();
        return true;
    }

    /// <summary>
    /// Decides whether a tracked source has an update. When both sides yield comparable
    /// versions the decision is a version comparison; otherwise it degrades to tag
    /// inequality, with equal known digests vetoing an apparent change (a re-tag of
    /// identical content is not an update).
    /// </summary>
    internal static UpdateDecision DetermineUpdate(
        string recordedVersion,
        string recordedTag,
        string recordedSha256Hex,
        string latestTag,
        string? latestDigest)
    {
        Version? recorded = null;
        if (Version.TryParse(recordedVersion, out var parsedRecorded))
        {
            recorded = Normalize(parsedRecorded);
        }
        else if (TryExtractVersionFromTag(recordedTag, out var recordedFromTag))
        {
            recorded = recordedFromTag;
        }

        if (recorded is not null && TryExtractVersionFromTag(latestTag, out var latest))
        {
            return new UpdateDecision(latest.CompareTo(recorded) > 0, "version");
        }

        var tagsEqual = string.Equals(recordedTag, latestTag, StringComparison.Ordinal);
        if (TryParseSha256Digest(latestDigest, out var latestHex)
            && !string.IsNullOrEmpty(recordedSha256Hex)
            && string.Equals(latestHex, recordedSha256Hex, StringComparison.OrdinalIgnoreCase))
        {
            return new UpdateDecision(false, "tag");
        }

        return new UpdateDecision(!tagsEqual, "tag");
    }

    /// <summary>Classifies an asset by file extension.</summary>
    internal static GitHubAssetKind ClassifyAsset(string assetName)
    {
        if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return GitHubAssetKind.Zip;
        }

        return assetName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? GitHubAssetKind.Dll
            : GitHubAssetKind.Other;
    }

    /// <summary>
    /// An asset is installable when it is a ZIP or DLL within the installer's upload cap.
    /// Checksum companions (.md5/.sha256/.txt/...) fall out via the extension allowlist.
    /// </summary>
    internal static bool IsInstallable(GitHubReleaseAsset asset)
        => ClassifyAsset(asset.Name) != GitHubAssetKind.Other
            && asset.Size > 0
            && asset.Size <= PluginZipInstaller.MaximumUploadBytes;

    /// <summary>
    /// Picks the asset to preselect in the UI: a sole installable asset, else a sole ZIP,
    /// else the shortest-named ZIP mentioning the repository; null means the administrator
    /// must choose explicitly.
    /// </summary>
    internal static long? SelectRecommendedAsset(IReadOnlyList<GitHubReleaseAsset> assets, string repo)
    {
        var installable = assets.Where(IsInstallable).ToArray();
        if (installable.Length == 1)
        {
            return installable[0].Id;
        }

        var zips = installable
            .Where(static asset => ClassifyAsset(asset.Name) == GitHubAssetKind.Zip)
            .ToArray();
        if (zips.Length == 1)
        {
            return zips[0].Id;
        }

        var normalizedRepo = NormalizeForComparison(repo);
        if (normalizedRepo.Length == 0)
        {
            return null;
        }

        var named = zips
            .Where(asset => NormalizeForComparison(asset.Name).Contains(normalizedRepo, StringComparison.Ordinal))
            .OrderBy(static asset => asset.Name.Length)
            .ThenBy(static asset => asset.Name, StringComparer.Ordinal)
            .ToArray();
        return named.Length > 0 ? named[0].Id : null;
    }

    /// <summary>
    /// Strips the trailing <c>_x.y.z.w</c> version suffix from an installed plugin folder
    /// name (installs land in versioned folders), yielding the stable base name used for
    /// source-tracking identity.
    /// </summary>
    internal static string StripVersionFolderSuffix(string folderName)
    {
        var separator = folderName.LastIndexOf('_');
        return separator > 0 && Version.TryParse(folderName[(separator + 1)..], out _)
            ? folderName[..separator]
            : folderName;
    }

    private static Version Normalize(Version version)
        => new(
            version.Major,
            version.Minor,
            Math.Max(version.Build, 0),
            Math.Max(version.Revision, 0));

    private static string NormalizeForComparison(string value)
        => new([.. value.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit)]);

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9]|-(?=[A-Za-z0-9])){0,38}$")]
    private static partial Regex OwnerRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]{1,100}$")]
    private static partial Regex RepoRegex();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._+\-]*$")]
    private static partial Regex TagSegmentRegex();

    /// <summary>The outcome of an update check comparison.</summary>
    /// <param name="UpdateAvailable">Whether the latest release should be offered as an update.</param>
    /// <param name="ComparisonMethod">How the decision was made: "version" or "tag".</param>
    internal sealed record UpdateDecision(bool UpdateAvailable, string ComparisonMethod);
}
