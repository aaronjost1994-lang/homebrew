using System.Security.Cryptography;
using WinBrew.Application.Ports;
using WinBrew.Core;
using WinBrew.Core.Net;
using WinBrew.Infrastructure.Http;
using WinBrew.TestFixtures;

namespace WinBrew.IntegrationTests;

/// <summary>
/// A13 HTTP behavior of the real <see cref="SecureHttpFetcher"/> against the
/// in-process server: HTTPS-only gate, manual redirects (max 5, no
/// downgrade), credentials only for their exact origin, size limits on
/// ACTUAL streamed bytes, truncation, retries with backoff, timeouts.
/// </summary>
public sealed class HttpFetcherTests : IDisposable
{
    private static readonly InsecureLocalhostPolicy Allowed = new(true, true, true);

    private readonly LocalHttpServer _server = new();
    private readonly LocalHttpServer _otherOrigin = new();
    private readonly RecordingCredentials _credentials = new();

    public void Dispose()
    {
        _server.Dispose();
        _otherOrigin.Dispose();
    }

    private sealed class RecordingCredentials : ICredentialProvider
    {
        public Dictionary<string, OriginCredential> Stored { get; } = new(StringComparer.Ordinal);

        public OriginCredential? GetForOrigin(string origin) => Stored.TryGetValue(origin, out var c) ? c : null;
    }

    private sealed class Factory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private SecureHttpFetcher Fetcher() => new(new Factory(), _credentials);

    private static FetchRequest Request(Uri url, long? expected = null, long max = 1 << 20, int retries = 0, double timeoutSeconds = 10,
        InsecureLocalhostPolicy? policy = null) =>
        new(url, expected, max, TimeSpan.FromSeconds(timeoutSeconds), retries, policy ?? Allowed);

    private Uri Url(string path) => new(_server.BaseUrl, path);

    [Fact]
    public async Task Download_StreamsAndHashes()
    {
        var body = RandomNumberGenerator.GetBytes(50_000);
        _server.Handle("/a.bin", ctx => LocalHttpServer.WriteAsync(ctx, body));
        var file = Path.GetTempFileName();
        try
        {
            var result = await Fetcher().DownloadToFileAsync(Request(Url("a.bin"), body.Length), file, CancellationToken.None);
            Assert.Equal(body.Length, result.Length);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(body)), result.Sha256);
            Assert.Equal(body, await File.ReadAllBytesAsync(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData(false, false, true)]  // release build: no exception at all
    [InlineData(true, false, true)]   // test-hooks but config off
    [InlineData(true, true, false)]   // test-hooks + config, but WINBREW_TEST_MODE unset
    public async Task LoopbackHttp_NeedsAllThreeGates(bool testHooks, bool config, bool env)
    {
        var policy = new InsecureLocalhostPolicy(testHooks, config, env);
        var ex = await Assert.ThrowsAsync<WinBrewException>(() => Fetcher().GetBytesAsync(Request(Url("x"), policy: policy), CancellationToken.None));
        Assert.Equal(WinBrewErrorCode.VerificationFailure, ex.Code);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task NonLoopbackHttp_IsAlwaysRefused()
    {
        var ex = await Assert.ThrowsAsync<WinBrewException>(() =>
            Fetcher().GetBytesAsync(Request(new Uri("http://example.org/x")), CancellationToken.None));
        Assert.Contains("not HTTPS", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedChunkedBody_IsAbortedOnActualBytes()
    {
        _server.Handle("/big", ctx => LocalHttpServer.WriteChunkedAsync(ctx, new byte[5000]));
        var ex = await Assert.ThrowsAsync<WinBrewException>(() => Fetcher().GetBytesAsync(Request(Url("big"), max: 4096), CancellationToken.None));
        Assert.Equal(WinBrewErrorCode.VerificationFailure, ex.Code);
        Assert.Contains("more than 4096", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LongerThanTheMetadataSize_IsRejected_EvenWithoutContentLength()
    {
        _server.Handle("/m", ctx => LocalHttpServer.WriteChunkedAsync(ctx, new byte[300]));
        var ex = await Assert.ThrowsAsync<WinBrewException>(() => Fetcher().GetBytesAsync(Request(Url("m"), expected: 200), CancellationToken.None));
        Assert.Equal(WinBrewErrorCode.VerificationFailure, ex.Code);
    }

    [Fact]
    public async Task Truncated_CleanEof_IsRejected_NotRetried()
    {
        _server.Handle("/t", ctx => LocalHttpServer.WriteChunkedAsync(ctx, new byte[100]));
        var ex = await Assert.ThrowsAsync<WinBrewException>(() => Fetcher().GetBytesAsync(Request(Url("t"), expected: 200, retries: 3), CancellationToken.None));
        Assert.Contains("truncated", ex.Message, StringComparison.Ordinal);
        Assert.Single(_server.Requests);
    }

    [Fact]
    public async Task ContentLengthMismatch_IsRejected_BeforeTheBody()
    {
        _server.Handle("/c", ctx => LocalHttpServer.WriteAsync(ctx, new byte[10]));
        var ex = await Assert.ThrowsAsync<WinBrewException>(() => Fetcher().GetBytesAsync(Request(Url("c"), expected: 11), CancellationToken.None));
        Assert.Equal(WinBrewErrorCode.VerificationFailure, ex.Code);
    }

    [Fact]
    public async Task InterruptedConnection_IsRetried_ThenSucceeds()
    {
        var body = RandomNumberGenerator.GetBytes(20_000);
        var attempts = 0;
        _server.Handle("/flaky", ctx => Interlocked.Increment(ref attempts) == 1
            ? LocalHttpServer.DropAfterAsync(ctx, body, 1000)
            : LocalHttpServer.WriteAsync(ctx, body));
        var (bytes, _) = await Fetcher().GetBytesAsync(Request(Url("flaky"), body.Length, retries: 2), CancellationToken.None);
        Assert.Equal(body, bytes);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task ServerErrors_AreRetried_ThenNetworkFailure()
    {
        _server.Handle("/500", ctx => LocalHttpServer.StatusAsync(ctx, 503));
        var ex = await Assert.ThrowsAsync<WinBrewException>(() => Fetcher().GetBytesAsync(Request(Url("500"), retries: 2), CancellationToken.None));
        Assert.Equal(WinBrewErrorCode.NetworkFailure, ex.Code);
        Assert.Equal(3, _server.Requests.Count);
    }

    [Fact]
    public async Task NotFound_IsNotRetried()
    {
        var ex = await Assert.ThrowsAsync<WinBrewException>(() => Fetcher().GetBytesAsync(Request(Url("missing"), retries: 3), CancellationToken.None));
        Assert.Equal(WinBrewErrorCode.NetworkFailure, ex.Code);
        Assert.Single(_server.Requests);
    }

    [Fact]
    public async Task Timeout_IsANetworkFailure()
    {
        _server.Handle("/slow", async ctx =>
        {
            await Task.Delay(3000);
            await LocalHttpServer.WriteAsync(ctx, [1]);
        });
        var ex = await Assert.ThrowsAsync<WinBrewException>(() => Fetcher().GetBytesAsync(Request(Url("slow"), timeoutSeconds: 0.5), CancellationToken.None));
        Assert.Equal(WinBrewErrorCode.NetworkFailure, ex.Code);
        Assert.Contains("timed out", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Redirects_AreFollowedManually_UpToFive()
    {
        for (var i = 0; i < 5; i++)
        {
            var next = $"/r{i + 1}";
            _server.Handle($"/r{i}", ctx => LocalHttpServer.RedirectAsync(ctx, next));
        }

        _server.Handle("/r5", ctx => LocalHttpServer.WriteAsync(ctx, "ok"u8.ToArray()));
        var (bytes, _) = await Fetcher().GetBytesAsync(Request(Url("r0")), CancellationToken.None);
        Assert.Equal("ok"u8.ToArray(), bytes);
    }

    [Fact]
    public async Task SixRedirects_AreRefused()
    {
        for (var i = 0; i < 6; i++)
        {
            var next = $"/s{i + 1}";
            _server.Handle($"/s{i}", ctx => LocalHttpServer.RedirectAsync(ctx, next));
        }

        _server.Handle("/s6", ctx => LocalHttpServer.WriteAsync(ctx, "late"u8.ToArray()));
        var ex = await Assert.ThrowsAsync<WinBrewException>(() => Fetcher().GetBytesAsync(Request(Url("s0")), CancellationToken.None));
        Assert.Contains("more than 5 redirects", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("/s6", _server.RequestedPaths);
    }

    [Fact]
    public async Task RedirectToNonHttps_IsRefused()
    {
        _server.Handle("/down", ctx => LocalHttpServer.RedirectAsync(ctx, "http://example.org/payload"));
        var ex = await Assert.ThrowsAsync<WinBrewException>(() => Fetcher().GetBytesAsync(Request(Url("down")), CancellationToken.None));
        Assert.Contains("redirect refused", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Credentials_GoOnlyToTheirOrigin_AndAreNeverReAdded()
    {
        _credentials.Stored[UrlPolicy.Origin(_server.BaseUrl)] = new OriginCredential("Bearer", "s3cret");
        // origin A → origin B → back to origin A
        _server.Handle("/start", ctx => LocalHttpServer.RedirectAsync(ctx, new Uri(_otherOrigin.BaseUrl, "hop").ToString()));
        _otherOrigin.Handle("/hop", ctx => LocalHttpServer.RedirectAsync(ctx, new Uri(_server.BaseUrl, "final").ToString()));
        _server.Handle("/final", ctx => LocalHttpServer.WriteAsync(ctx, "done"u8.ToArray()));

        await Fetcher().GetBytesAsync(Request(Url("start")), CancellationToken.None);

        var first = _server.Requests.First(r => r.Path == "/start");
        Assert.Equal("Bearer s3cret", first.Authorization);
        Assert.Null(_otherOrigin.Requests.Single().Authorization);              // stripped on origin change
        Assert.Null(_server.Requests.First(r => r.Path == "/final").Authorization); // never re-added
    }

    [Fact]
    public void Redaction_RemovesUserinfoAndQuery()
    {
        var redacted = UrlPolicy.Redact(new Uri("https://user:pw@cdn.example.org/a/b.zip?sig=SECRET&x=1#frag"));
        Assert.Equal("https://cdn.example.org/a/b.zip?<redacted>", redacted);
        Assert.DoesNotContain("SECRET", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("pw", redacted, StringComparison.Ordinal);
    }
}
