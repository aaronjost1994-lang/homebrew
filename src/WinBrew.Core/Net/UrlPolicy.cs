namespace WinBrew.Core.Net;

/// <summary>
/// Inputs of the A13 localhost exception. The exception applies only when
/// ALL three hold; in release builds <see cref="TestHooksBuild"/> is always
/// false (the exception is compiled out), so the other two have no effect.
/// </summary>
public sealed record InsecureLocalhostPolicy(bool TestHooksBuild, bool AllowInsecureLocalhostConfig, bool TestModeEnvironment)
{
    public static readonly InsecureLocalhostPolicy Release = new(false, false, false);

    public bool Allowed => TestHooksBuild && AllowInsecureLocalhostConfig && TestModeEnvironment;
}

/// <summary>What the client does with one redirect response (A13).</summary>
public sealed record RedirectDecision(bool Follow, Uri? Target, bool StripCredentials, string? Refusal);

/// <summary>
/// Pure URL rules (A13): HTTPS only (with the triple-gated localhost
/// exception), manual redirects (at most 5, never to non-HTTPS, credentials
/// removed when the origin changes) and log redaction.
/// </summary>
public static class UrlPolicy
{
    public const int MaxRedirects = 5;

    /// <summary>Validates a repository or download URL. Returns null when allowed, else the refusal reason.</summary>
    public static string? Check(Uri uri, InsecureLocalhostPolicy localhost)
    {
        if (!uri.IsAbsoluteUri)
        {
            return $"'{Redact(uri)}' is not an absolute URL";
        }

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return null;
        }

        if (uri.Scheme == Uri.UriSchemeHttp && IsLoopbackHost(uri))
        {
            return localhost.Allowed
                ? null
                : $"'{Redact(uri)}' uses http://; the localhost exception requires a test-hooks build, " +
                  "testing.allowInsecureLocalhost=true and WINBREW_TEST_MODE=1 (A13)";
        }

        return $"'{Redact(uri)}' is not HTTPS; WinBrew downloads only over HTTPS (A13)";
    }

    /// <summary>The literal hosts of the A13 exception: localhost, 127.0.0.1 and [::1].</summary>
    public static bool IsLoopbackHost(Uri uri) =>
        string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        uri.Host == "127.0.0.1" ||
        uri.Host is "[::1]" or "::1";

    /// <summary>Origin = scheme + host + port (A13).</summary>
    public static bool SameOrigin(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        a.Port == b.Port;

    /// <summary>"https://host:port" — the Credential Manager target suffix (WinBrew:&lt;origin&gt;, A13).</summary>
    public static string Origin(Uri uri) =>
        $"{uri.Scheme.ToLowerInvariant()}://{uri.IdnHost.ToLowerInvariant()}:{uri.Port}";

    /// <summary>
    /// Decides one redirect hop. <paramref name="redirectsSoFar"/> counts hops
    /// already followed. The new target must pass <see cref="Check"/> and be
    /// HTTPS (or the same gated localhost exception); credentials are
    /// stripped whenever the origin changes (and never re-added).
    /// </summary>
    public static RedirectDecision DecideRedirect(Uri current, Uri? location, int redirectsSoFar, InsecureLocalhostPolicy localhost)
    {
        if (location is null)
        {
            return new RedirectDecision(false, null, false, "redirect without a Location header");
        }

        if (redirectsSoFar >= MaxRedirects)
        {
            return new RedirectDecision(false, null, false, $"more than {MaxRedirects} redirects (A13)");
        }

        var target = location.IsAbsoluteUri ? location : new Uri(current, location);
        var refusal = Check(target, localhost);
        if (refusal is not null)
        {
            return new RedirectDecision(false, target, false, "redirect refused: " + refusal);
        }

        // Downgrade from HTTPS to the localhost http exception is still a
        // non-HTTPS redirect: refuse it unless the request already was http.
        if (current.Scheme == Uri.UriSchemeHttps && target.Scheme != Uri.UriSchemeHttps)
        {
            return new RedirectDecision(false, target, false, "redirect from HTTPS to a non-HTTPS URL refused (A13)");
        }

        return new RedirectDecision(true, target, StripCredentials: !SameOrigin(current, target), null);
    }

    /// <summary>
    /// Log redaction (A13): removes URL userinfo and the ENTIRE query string
    /// (signed URLs carry secrets there) and the fragment.
    /// </summary>
    public static string Redact(Uri uri)
    {
        if (!uri.IsAbsoluteUri)
        {
            var raw = uri.OriginalString;
            var cut = raw.IndexOfAny(['?', '#']);
            return cut >= 0 ? raw[..cut] + (raw[cut] == '?' ? "?<redacted>" : string.Empty) : raw;
        }

        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Fragment = string.Empty,
        };
        var hadQuery = !string.IsNullOrEmpty(uri.Query);
        builder.Query = string.Empty;
        var text = builder.Uri.GetLeftPart(UriPartial.Path);
        return hadQuery ? text + "?<redacted>" : text;
    }

    /// <summary>Redacts an arbitrary string that may be a URL.</summary>
    public static string Redact(string maybeUrl) =>
        Uri.TryCreate(maybeUrl, UriKind.Absolute, out var uri) ? Redact(uri) : maybeUrl;

    /// <summary>Header names whose values are never logged (A13).</summary>
    public static bool IsSensitiveHeader(string name) =>
        name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase);
}
