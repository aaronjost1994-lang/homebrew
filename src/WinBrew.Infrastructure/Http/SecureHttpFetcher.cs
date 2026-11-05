using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using WinBrew.Application.Ports;
using WinBrew.Core;
using WinBrew.Core.Net;

namespace WinBrew.Infrastructure.Http;

/// <summary>
/// The A13 HTTP client over <see cref="IHttpClientFactory"/> (A4). The named
/// client is registered with AllowAutoRedirect=false and no cookies; this
/// class follows redirects itself (max 5, never to non-HTTPS, credentials
/// stripped on origin change and never re-added), attaches Credential
/// Manager credentials only to their exact origin, enforces the timeout and
/// the size limits on ACTUAL bytes, hashes while streaming, and retries
/// transient failures with exponential backoff and jitter. Every URL that
/// reaches a message is redacted (userinfo and query removed).
/// </summary>
public sealed class SecureHttpFetcher(IHttpClientFactory factory, ICredentialProvider credentials) : IHttpFetcher
{
    public const string ClientName = "winbrew";

    private const int BufferSize = 81920;

    public async Task<FetchResult> DownloadToFileAsync(FetchRequest request, string destinationPath, CancellationToken ct)
    {
        return await WithRetriesAsync(request, async token =>
        {
            await using var file = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None,
                BufferSize, FileOptions.Asynchronous);
            return await FetchOnceAsync(request, file, token);
        }, ct);
    }

    public async Task<(byte[] Body, FetchResult Result)> GetBytesAsync(FetchRequest request, CancellationToken ct)
    {
        byte[] body = [];
        var result = await WithRetriesAsync(request, async token =>
        {
            using var buffer = new MemoryStream();
            var r = await FetchOnceAsync(request, buffer, token);
            body = buffer.ToArray();
            return r;
        }, ct);
        return (body, result);
    }

    // ---- retries ---------------------------------------------------------------------------

    private sealed class TransientFetchException(string message, Exception? inner = null) : Exception(message, inner);

    private static async Task<FetchResult> WithRetriesAsync(FetchRequest request, Func<CancellationToken, Task<FetchResult>> attempt, CancellationToken ct)
    {
        var refusal = UrlPolicy.Check(request.Url, request.Localhost);
        if (refusal is not null)
        {
            throw new WinBrewException(WinBrewErrorCode.VerificationFailure, $"Refusing to download: {refusal}.");
        }

        var failures = new List<string>();
        for (var tryNumber = 0; ; tryNumber++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(request.Timeout);
            try
            {
                return await attempt(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                failures.Add($"timed out after {request.Timeout.TotalSeconds:0}s");
            }
            catch (TransientFetchException ex)
            {
                failures.Add(ex.Message);
            }
            catch (HttpRequestException ex)
            {
                failures.Add(ex.HttpRequestError.ToString());
            }
            catch (IOException ex) when (!DiskFull.Is(ex))
            {
                failures.Add("connection interrupted: " + ex.GetType().Name);
            }
            catch (IOException ex)
            {
                throw new WinBrewException(WinBrewErrorCode.InsufficientSpace,
                    $"Disk full while downloading {UrlPolicy.Redact(request.Url)}: {ex.Message}");
            }

            if (tryNumber >= request.Retries)
            {
                throw new WinBrewException(WinBrewErrorCode.NetworkFailure,
                    $"Download of {UrlPolicy.Redact(request.Url)} failed after {tryNumber + 1} attempt(s): {string.Join("; ", failures)}.",
                    new Dictionary<string, object?> { ["url"] = UrlPolicy.Redact(request.Url), ["attempts"] = tryNumber + 1 });
            }

            await Task.Delay(Backoff(tryNumber), ct);
        }
    }

    /// <summary>Exponential backoff with full jitter: ~0.5 s, 1 s, 2 s … capped at 8 s (D10 download.retries).</summary>
    private static TimeSpan Backoff(int attempt)
    {
        var ceilingMs = Math.Min(8000, 500 * (1 << Math.Min(attempt, 4)));
        return TimeSpan.FromMilliseconds(ceilingMs / 2 + RandomNumberGenerator.GetInt32(ceilingMs / 2 + 1));
    }

    // ---- one attempt -------------------------------------------------------------------------

    private async Task<FetchResult> FetchOnceAsync(FetchRequest request, Stream destination, CancellationToken ct)
    {
        var client = factory.CreateClient(ClientName);
        var origin = request.Url;
        var current = request.Url;
        var credentialsAllowed = true;

        for (var redirects = 0; ; redirects++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, current);
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

            if (credentialsAllowed && UrlPolicy.SameOrigin(origin, current))
            {
                var credential = credentials.GetForOrigin(UrlPolicy.Origin(current));
                if (credential is not null)
                {
                    message.Headers.Authorization = new AuthenticationHeaderValue(credential.Scheme, credential.Parameter);
                }
            }

            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);

            if (IsRedirect(response.StatusCode))
            {
                var decision = UrlPolicy.DecideRedirect(current, response.Headers.Location, redirects, request.Localhost);
                if (!decision.Follow)
                {
                    throw new WinBrewException(WinBrewErrorCode.VerificationFailure,
                        $"Download of {UrlPolicy.Redact(request.Url)} stopped: {decision.Refusal}.");
                }

                if (decision.StripCredentials)
                {
                    credentialsAllowed = false; // removed on origin change, never re-added (A13)
                }

                current = decision.Target!;
                continue;
            }

            if (response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
                (int)response.StatusCode >= 500)
            {
                throw new TransientFetchException($"HTTP {(int)response.StatusCode} from {UrlPolicy.Redact(current)}");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new WinBrewException(WinBrewErrorCode.NetworkFailure,
                    $"HTTP {(int)response.StatusCode} from {UrlPolicy.Redact(current)}.",
                    new Dictionary<string, object?> { ["status"] = (int)response.StatusCode, ["url"] = UrlPolicy.Redact(current) });
            }

            return await ReadBodyAsync(request, current, response, destination, ct);
        }
    }

    private static async Task<FetchResult> ReadBodyAsync(FetchRequest request, Uri url, HttpResponseMessage response, Stream destination, CancellationToken ct)
    {
        var limit = request.ExpectedSize is long expected ? Math.Min(expected, request.MaxBytes) : request.MaxBytes;
        var declared = response.Content.Headers.ContentLength;
        if (declared is long length)
        {
            if (length > request.MaxBytes)
            {
                throw SizeError(url, $"declares {length} bytes, above the limit of {request.MaxBytes}");
            }

            if (request.ExpectedSize is long size && length != size)
            {
                throw SizeError(url, $"declares {length} bytes but the metadata says {size}");
            }
        }

        if (request.ExpectedSize is long tooBig && tooBig > request.MaxBytes)
        {
            throw SizeError(url, $"metadata size {tooBig} exceeds the limit of {request.MaxBytes}");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[BufferSize];
        long total = 0;

        while (true)
        {
            var read = await body.ReadAsync(buffer, ct);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > limit)
            {
                // Enforced on ACTUAL bytes, never on headers (A11 step 3).
                throw SizeError(url, $"sent more than {limit} bytes");
            }

            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        if (request.ExpectedSize is long expectedSize && total != expectedSize)
        {
            throw SizeError(url, $"is truncated: received {total} of {expectedSize} bytes");
        }

        await destination.FlushAsync(ct);
        return new FetchResult(Convert.ToHexStringLower(hash.GetHashAndReset()), total);
    }

    private static WinBrewException SizeError(Uri url, string problem) =>
        new(WinBrewErrorCode.VerificationFailure, $"Download {UrlPolicy.Redact(url)} {problem} (A11 step 3).");

    private static bool IsRedirect(HttpStatusCode code) => code is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
        or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
}

/// <summary>Disk-full detection (A13/D9 exit 12): ERROR_DISK_FULL (112) and ERROR_HANDLE_DISK_FULL (39).</summary>
public static class DiskFull
{
    public const int ErrorDiskFullHResult = unchecked((int)0x80070070);
    public const int ErrorHandleDiskFullHResult = unchecked((int)0x80070027);

    public static bool Is(IOException ex) =>
        ex.HResult is ErrorDiskFullHResult or ErrorHandleDiskFullHResult ||
        // POSIX ENOSPC surfaces as HResult 28 on Linux/macOS hosts.
        ex.HResult == 28;
}
