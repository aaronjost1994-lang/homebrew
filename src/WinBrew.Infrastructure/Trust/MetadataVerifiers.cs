using WinBrew.Application.Ports;
using WinBrew.Core;

namespace WinBrew.Infrastructure.Trust;

/// <summary>
/// Release builds in Phase B: real D6 signature verification (ECDSA P-256
/// envelopes, root → timestamp → index) arrives in Phase D, so a release
/// build accepts NO repository metadata yet. This is an honest refusal, not
/// a stub that pretends to verify (A2).
/// </summary>
public sealed class UnavailableMetadataVerifier : IMetadataVerifier
{
    public Task<byte[]> VerifyIndexAsync(string repo, byte[] document, CancellationToken ct) =>
        throw new WinBrewException(
            WinBrewErrorCode.VerificationFailure,
            $"Cannot accept metadata for repository '{repo}': signature verification (D6) is not available in " +
            "this build (NotAvailableInThisBuild; planned for Phase D). Unsigned metadata is never accepted by release builds.",
            new Dictionary<string, object?> { ["reason"] = "NotAvailableInThisBuild", ["phase"] = "Phase D (trust)" });
}

#if WINBREW_TEST_HOOKS
/// <summary>
/// TEST-HOOKS BUILDS ONLY (-p:WinBrewTestHooks=true; compiled out of release
/// builds). Accepts an UNSIGNED index.json as its own payload so Phase B can
/// exercise the final catalog ingest path before Phase D brings signing.
/// It still requires WINBREW_TEST_MODE=1 at run time, so a test-hooks binary
/// used outside a test never ingests unsigned metadata by accident.
/// </summary>
public sealed class TestHooksUnsignedVerifier(bool testModeEnvironment) : IMetadataVerifier
{
    public Task<byte[]> VerifyIndexAsync(string repo, byte[] document, CancellationToken ct)
    {
        if (!testModeEnvironment)
        {
            throw new WinBrewException(WinBrewErrorCode.VerificationFailure,
                "TestHooksUnsignedVerifier refuses unsigned metadata unless WINBREW_TEST_MODE=1 (test-hooks build).");
        }

        return Task.FromResult(document);
    }
}
#endif

/// <summary>Chooses the verifier for the build (A11: one property gates all test-only behavior).</summary>
public static class MetadataVerifierFactory
{
    public static IMetadataVerifier Create(bool testHooksBuild, bool testModeEnvironment)
    {
#if WINBREW_TEST_HOOKS
        if (testHooksBuild)
        {
            return new TestHooksUnsignedVerifier(testModeEnvironment);
        }
#else
        _ = testHooksBuild;
        _ = testModeEnvironment;
#endif
        return new UnavailableMetadataVerifier();
    }
}
