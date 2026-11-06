namespace WinBrew.Core.Installers;

/// <summary>What an installer exit code means (A12).</summary>
public enum InstallerOutcome
{
    Success,
    RebootRequired,
    RebootStarted,
    Cancelled,
    Busy,
    Failed,
}

/// <summary>
/// msiexec invocation and exit-code rules (A12). Arguments are always a
/// LIST for <c>ProcessStartInfo.ArgumentList</c> — no shell, no string
/// concatenated into a command line.
/// </summary>
public static class MsiRules
{
    public const int ErrorSuccess = 0;
    public const int ErrorInstallUserExit = 1602;
    public const int ErrorInstallFailure = 1603;
    public const int ErrorInstallAlreadyRunning = 1618;
    public const int ErrorSuccessRebootInitiated = 1641;
    public const int ErrorSuccessRebootRequired = 3010;

    /// <summary>Retries of 1618 before exit 13 InstallerBusy (A12, D9).</summary>
    public const int BusyRetries = 4;

    /// <summary>Backoff before retry <paramref name="attempt"/> (1-based): 5 s, 10 s, 20 s, 40 s.</summary>
    public static TimeSpan BusyBackoff(int attempt) => TimeSpan.FromSeconds(5 * Math.Pow(2, attempt - 1));

    /// <summary>
    /// Runs msiexec, retrying 1618 (another installation in progress) with
    /// backoff up to <see cref="BusyRetries"/> times; returns the final exit code
    /// (still 1618 when retries are exhausted → exit 13).
    /// </summary>
    public static async Task<int> RunWithBusyRetriesAsync(
        Func<CancellationToken, Task<int>> run, Func<TimeSpan, CancellationToken, Task> delay, CancellationToken ct)
    {
        var code = await run(ct).ConfigureAwait(false);
        for (var attempt = 1; code == ErrorInstallAlreadyRunning && attempt <= BusyRetries; attempt++)
        {
            await delay(BusyBackoff(attempt), ct).ConfigureAwait(false);
            code = await run(ct).ConfigureAwait(false);
        }

        return code;
    }

    /// <summary>msiexec exit codes: 0, 3010, 1641, 1602, 1618; anything else (e.g. 1603) is a failure.</summary>
    public static InstallerOutcome Map(int exitCode) => exitCode switch
    {
        ErrorSuccess => InstallerOutcome.Success,
        ErrorSuccessRebootRequired => InstallerOutcome.RebootRequired,
        ErrorSuccessRebootInitiated => InstallerOutcome.RebootStarted,
        ErrorInstallUserExit => InstallerOutcome.Cancelled,
        ErrorInstallAlreadyRunning => InstallerOutcome.Busy,
        _ => InstallerOutcome.Failed,
    };

    /// <summary>EXE installers: 0 success; 3010/1641 reboot; anything else is a failure (1618 is MSI-specific).</summary>
    public static InstallerOutcome MapExe(int exitCode) => exitCode switch
    {
        ErrorSuccess => InstallerOutcome.Success,
        ErrorSuccessRebootRequired => InstallerOutcome.RebootRequired,
        ErrorSuccessRebootInitiated => InstallerOutcome.RebootStarted,
        _ => InstallerOutcome.Failed,
    };

    /// <summary>D9 exit code for an outcome (Success → 0 = no error).</summary>
    public static WinBrewErrorCode ToErrorCode(InstallerOutcome outcome) => outcome switch
    {
        InstallerOutcome.Success => WinBrewErrorCode.Success,
        InstallerOutcome.RebootRequired or InstallerOutcome.RebootStarted => WinBrewErrorCode.RebootRequired,
        InstallerOutcome.Cancelled => WinBrewErrorCode.Cancelled,
        InstallerOutcome.Busy => WinBrewErrorCode.InstallerBusy,
        _ => WinBrewErrorCode.GeneralFailure,
    };

    /// <summary><c>/i &lt;path&gt; /qn /norestart /l*v &lt;log&gt;</c> plus only the declared properties, in ordinal key order.</summary>
    public static IReadOnlyList<string> InstallArguments(string msiPath, string logPath, IReadOnlyDictionary<string, string> properties)
    {
        RequireAbsolute(msiPath, "MSI path");
        RequireAbsolute(logPath, "log path");
        var args = new List<string> { "/i", msiPath, "/qn", "/norestart", "/l*v", logPath };
        foreach (var (name, value) in properties.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!InstallerArguments.PropertyNameRegex().IsMatch(name) || !InstallerArguments.PropertyValueRegex().IsMatch(value))
            {
                throw new WinBrewException(WinBrewErrorCode.VerificationFailure, $"MSI property '{name}' is not a safe public property assignment (A12).");
            }

            args.Add($"{name}={value}");
        }

        return args;
    }

    /// <summary><c>/x &lt;ProductCode&gt; /qn /norestart /l*v &lt;log&gt;</c>.</summary>
    public static IReadOnlyList<string> UninstallArguments(string productCode, string logPath)
    {
        if (!InstallerArguments.GuidRegex().IsMatch(productCode))
        {
            throw new WinBrewException(WinBrewErrorCode.VerificationFailure, $"'{productCode}' is not a ProductCode GUID (A12).");
        }

        RequireAbsolute(logPath, "log path");
        return ["/x", productCode, "/qn", "/norestart", "/l*v", logPath];
    }

    /// <summary>The properties for an install in <paramref name="scope"/>: declared properties, plus the authored per-user set for 'user'.</summary>
    public static IReadOnlyDictionary<string, string> PropertiesFor(MsiInstaller msi, string scope)
    {
        var result = new Dictionary<string, string>(msi.Properties ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        if (scope == "user")
        {
            foreach (var (k, v) in msi.UserProperties ?? new Dictionary<string, string>())
            {
                result[k] = v;
            }
        }

        return result;
    }

    private static void RequireAbsolute(string path, string what)
    {
        if (!Path.IsPathFullyQualified(path) || path.Contains('"', StringComparison.Ordinal))
        {
            throw new WinBrewException(WinBrewErrorCode.GeneralFailure, $"The {what} '{path}' must be an absolute path without quotes.");
        }
    }
}
