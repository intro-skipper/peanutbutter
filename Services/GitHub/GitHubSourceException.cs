namespace Jellyfin.Plugin.PeanutButter.Services.GitHub;

/// <summary>
/// Categorizes why a GitHub release operation failed, so the controller can map it to an HTTP status.
/// </summary>
public enum GitHubSourceFailureReason
{
    /// <summary>The supplied owner, repository, tag, or asset identifier is not acceptable.</summary>
    InvalidRequest,

    /// <summary>The repository, release, or asset does not exist on GitHub.</summary>
    NotFound,

    /// <summary>GitHub's API rate limit is exhausted for this server's IP address.</summary>
    RateLimited,

    /// <summary>GitHub returned a server error or was unreachable.</summary>
    Upstream,

    /// <summary>GitHub returned a response that could not be parsed or exceeded safety limits.</summary>
    InvalidResponse,

    /// <summary>A download redirect pointed outside the allowed GitHub hosts.</summary>
    RedirectRejected,

    /// <summary>The asset exceeds the maximum accepted download size.</summary>
    TooLarge,

    /// <summary>The downloaded bytes did not match the digest GitHub published for the asset.</summary>
    DigestMismatch,

    /// <summary>The operation exceeded this plugin's own deadline while GitHub was still responding.</summary>
    TimedOut
}

/// <summary>
/// Indicates that resolving or downloading a GitHub release failed. The <see cref="Reason"/>
/// distinguishes admin input errors from upstream failures; the message is safe to show to
/// the administrator.
/// </summary>
public sealed class GitHubSourceException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="GitHubSourceException"/> class.
    /// </summary>
    /// <param name="reason">The failure category.</param>
    /// <param name="message">The administrator-facing message.</param>
    public GitHubSourceException(GitHubSourceFailureReason reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="GitHubSourceException"/> class.
    /// </summary>
    /// <param name="reason">The failure category.</param>
    /// <param name="message">The administrator-facing message.</param>
    /// <param name="innerException">The underlying failure.</param>
    public GitHubSourceException(GitHubSourceFailureReason reason, string message, Exception innerException)
        : base(message, innerException)
    {
        Reason = reason;
    }

    /// <summary>Gets the failure category.</summary>
    public GitHubSourceFailureReason Reason { get; }

    /// <summary>Gets the time at which GitHub's rate limit resets, when <see cref="Reason"/> is <see cref="GitHubSourceFailureReason.RateLimited"/>.</summary>
    public DateTimeOffset? RateLimitResetsAt { get; init; }
}
