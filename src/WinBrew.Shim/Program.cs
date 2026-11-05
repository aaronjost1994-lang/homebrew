namespace WinBrew.Shim;

/// <summary>
/// wbrew-shim.exe — the shim executable template (D4).
///
/// Phase A ships the project, its Native AOT build configuration
/// (csproj) and this entry point only. The full D4 contract — the MSVC
/// C-runtime argv[0] scan, sidecar containment with the wbrew self-shim
/// exception, Job Object handling, Ctrl+C forwarding, GUI targets, the
/// 32,767-character command-line limit and the .old.&lt;guid&gt; rename
/// protocol — is implemented in Phase B, per the phase plan.
///
/// This entry point fails loudly and honestly: it is never on an
/// execution path in a Phase A build (no self install exists yet), and it
/// never pretends to shim anything.
/// </summary>
internal static class Program
{
    private const int ShimTargetMissing = 9009; // D4: matches cmd.exe "not recognized"

    [STAThread]
    private static int Main()
    {
        Console.Error.WriteLine(
            "wbrew-shim: the shim implementation (D4) arrives in Phase B; " +
            "this Native AOT template does nothing yet.");
        return ShimTargetMissing;
    }
}
