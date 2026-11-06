namespace WinBrew.Application.Ports;

/// <summary>
/// The self-update feed compiled into the build (A14): its URL and the
/// INITIAL release root (N=1). The feed's own root chain rotates from there,
/// so keys can change without shipping a new binary.
/// </summary>
public sealed record ReleaseFeedConfiguration(Uri Url, byte[] InitialRoot);

public interface IReleaseFeed
{
    /// <summary>Null when no feed is configured in this build ("self-update not configured for this build", exit 3).</summary>
    ReleaseFeedConfiguration? Configuration { get; }
}
