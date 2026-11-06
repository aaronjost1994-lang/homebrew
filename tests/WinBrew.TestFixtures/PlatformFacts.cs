using Xunit;

namespace WinBrew.TestFixtures;

/// <summary>
/// A fact that runs only on Windows. Elsewhere it is reported as SKIPPED with
/// the reason — never silently passed (A2/A16 honesty rules).
/// </summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute(string reason = "Needs Windows")
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = $"{reason} (not run on {Environment.OSVersion.Platform}).";
        }
    }
}

/// <summary>
/// A fact that needs the test-hooks build (-p:WinBrewTestHooks=true): fault
/// injection, the localhost HTTP exception and unsigned ingest (A11/A13).
/// Reported as SKIPPED in a release build.
/// </summary>
public sealed class TestHooksFactAttribute : FactAttribute
{
    public TestHooksFactAttribute()
    {
        if (!TestBuild.TestHooks)
        {
            Skip = "Needs the test-hooks build: dotnet test -p:WinBrewTestHooks=true (A11/A16).";
        }
    }
}

/// <summary>
/// A fact that needs BOTH Windows and the test-hooks build (e.g. HKCU PATH
/// tests that also use the localhost or fault-injection seams). Skipped, with
/// the missing prerequisite named, otherwise.
/// </summary>
public sealed class WindowsTestHooksFactAttribute : FactAttribute
{
    public WindowsTestHooksFactAttribute(string reason = "Needs Windows")
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = $"{reason} (not run on {Environment.OSVersion.Platform}).";
        }
        else if (!TestBuild.TestHooks)
        {
            Skip = "Needs the test-hooks build: dotnet test -p:WinBrewTestHooks=true (A11/A16).";
        }
    }
}

/// <summary>Theory variant of <see cref="TestHooksFactAttribute"/>.</summary>
public sealed class TestHooksTheoryAttribute : TheoryAttribute
{
    public TestHooksTheoryAttribute()
    {
        if (!TestBuild.TestHooks)
        {
            Skip = "Needs the test-hooks build: dotnet test -p:WinBrewTestHooks=true (A11/A16).";
        }
    }
}

public static class TestBuild
{
    public static bool TestHooks
    {
        get
        {
#if WINBREW_TEST_HOOKS
            return true;
#else
            return false;
#endif
        }
    }
}

/// <summary>
/// A Windows fact that also needs a published artifact named by an
/// environment variable (Windows CI sets it after publishing; A16/Phase F).
/// Skipped — with the reason — when either is missing.
/// </summary>
public sealed class WindowsArtifactFactAttribute : FactAttribute
{
    public WindowsArtifactFactAttribute(string environmentVariable, string what)
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = $"Needs Windows and {what} ({environmentVariable}).";
        }
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(environmentVariable)))
        {
            Skip = $"Set {environmentVariable} to {what} (Windows CI publishes it first).";
        }
    }
}

/// <summary>A fact that needs a release artifact named by an environment variable (any OS). Skipped — with the reason — when it is missing.</summary>
public sealed class ArtifactFactAttribute : FactAttribute
{
    public ArtifactFactAttribute(string environmentVariable, string what)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(environmentVariable)))
        {
            Skip = $"Set {environmentVariable} to {what} (the release workflow sets it after packaging).";
        }
    }
}
