using WinBrew.Core.Net;

namespace WinBrew.Application.Ports;

/// <summary>
/// One bounded HTTP fetch (A11 step 3, A13). <see cref="ExpectedSize"/>
/// (the manifest's declared size, or index.json's manifest size) must match
/// exactly — longer is rejected while streaming, shorter (truncated) after.
/// <see cref="MaxBytes"/> is the client-side hard limit
/// (download.maxBytes / trust.maxMetadataBytes) applied on top.
/// </summary>
public sealed record FetchRequest(
    Uri Url,
    long? ExpectedSize,
    long MaxBytes,
    TimeSpan Timeout,
    int Retries,
    InsecureLocalhostPolicy Localhost);

/// <summary>Streaming result: lowercase hex SHA-256 and the byte count actually received.</summary>
public sealed record FetchResult(string Sha256, long Length);

/// <summary>
/// HTTP client port (A13): HTTPS only (triple-gated localhost exception),
/// manual redirects (max 5, never to non-HTTPS, credentials stripped on
/// origin change), Credential Manager credentials sent only to their exact
/// origin, bounded retries with exponential backoff and jitter, a per-fetch
/// timeout, size limits enforced on actual bytes, streaming SHA-256, and
/// redacted logging. Failures after retries are NetworkFailure (exit 4);
/// size violations are VerificationFailure (exit 5).
/// </summary>
public interface IHttpFetcher
{
    /// <summary>Streams the body into <paramref name="destinationPath"/> (created/truncated), hashing as it goes.</summary>
    Task<FetchResult> DownloadToFileAsync(FetchRequest request, string destinationPath, CancellationToken ct);

    /// <summary>Fetches a small document (metadata, manifests) into memory under the same rules.</summary>
    Task<(byte[] Body, FetchResult Result)> GetBytesAsync(FetchRequest request, CancellationToken ct);
}

/// <summary>A credential for exactly one origin (A13).</summary>
public sealed record OriginCredential(string Scheme, string Parameter);

/// <summary>
/// Windows Credential Manager lookup (A13): generic credential
/// <c>WinBrew:&lt;origin&gt;</c>. Returns null when none is stored.
/// Credentials are never stored in config.json.
/// </summary>
public interface ICredentialProvider
{
    OriginCredential? GetForOrigin(string origin);
}

/// <summary>No credentials (non-Windows hosts, tests).</summary>
public sealed class NoCredentialProvider : ICredentialProvider
{
    public static readonly NoCredentialProvider Instance = new();

    public OriginCredential? GetForOrigin(string origin) => null;
}
