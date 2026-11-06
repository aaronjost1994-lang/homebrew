using System.Text.RegularExpressions;

namespace WinBrew.Core.Manifests;

/// <summary>One validation finding: the JSON field it concerns and the problem.</summary>
public sealed record ManifestError(string Field, string Message)
{
    public override string ToString() => $"{Field}: {Message}";
}

/// <summary>
/// Manifest validation beyond the JSON Schema (A9). Collects all findings
/// (author-friendly) instead of throwing on the first. The v1.0 client
/// rejects installer-bearing manifests; the v1.1 client (Phase E) validates them.
/// </summary>
public static partial class ManifestValidator
{
    private static readonly Regex Sha256Regex = new("^[0-9a-f]{64}$", RegexOptions.Compiled);

    // Shim-name shape (documented choice): no separators, no '.exe' suffix
    // (the shim binary is <name>.exe — a suffix would double it), not a
    // reserved device name.
    private static readonly Regex ShimNameRegex = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.Compiled);

    private static readonly string[] AllowedArchitectures = ["x64", "arm64", "x86"];
    private static readonly string[] AllowedFormats = ["zip", "msi", "exe"];
    private static readonly string[] AllowedInstallTypes = ["portable", "msi", "exe"];

    private const int MaxDisplayNameLength = 256;
    private const int MaxDescriptionLength = 2048;
    private const int MaxVendorVersionLength = 64;

    /// <summary>
    /// Validates a manifest against every A9 rule. <paramref name="testHooksBuild"/>
    /// permits http://localhost/127.0.0.1/[::1] artifact and homepage URLs for
    /// test-hooks builds only (A13); <paramref name="allowV11"/> permits
    /// installer declarations (Phase E; false in v1.0).
    /// </summary>
    public static IReadOnlyList<ManifestError> Validate(
        Manifest manifest,
        bool testHooksBuild = false,
        bool allowV11 = false)
    {
        var errors = new List<ManifestError>();

        ValidateBasics(manifest, errors);
        ValidateScopeAndInstaller(manifest, errors, allowV11);
        ValidateArtifacts(manifest, errors, testHooksBuild, allowV11);
        ValidateExecutables(manifest, errors);
        ValidateDependencies(manifest, errors);

        return errors;
    }

    /// <summary>Convenience: true when the manifest has no validation findings.</summary>
    public static bool IsValid(Manifest manifest, bool testHooksBuild = false, bool allowV11 = false) =>
        Validate(manifest, testHooksBuild, allowV11).Count == 0;

    private static void ValidateBasics(Manifest m, List<ManifestError> errors)
    {
        if (m.SchemaVersion != "1")
        {
            errors.Add(new("schemaVersion", $"Expected \"1\", got \"{m.SchemaVersion}\"."));
        }

        if (!Identifiers.IsValidPackageId(m.Id))
        {
            errors.Add(new("id", $"'{m.Id}' is not a valid package id (A9: lowercase, " +
                "^[a-z0-9](?:[a-z0-9.-]{0,62}[a-z0-9])?$, no '..', no reserved device names)."));
        }

        // A9: version must already be normalized (D3) — strict comparison.
        if (string.IsNullOrWhiteSpace(m.Version))
        {
            errors.Add(new("version", "Version is required."));
        }
        else if (!NuGet.Versioning.NuGetVersion.TryParse(m.Version, out var v) || v.ToNormalizedString() != m.Version)
        {
            errors.Add(new("version", $"'{m.Version}' is not a normalized NuGetVersion " +
                "(A9 requires the normalized form, e.g. '1.0.0', not 'v1.0' or '1.0')."));
        }

        if (m.VendorVersion is { Length: > MaxVendorVersionLength or 0 })
        {
            errors.Add(new("vendorVersion", $"Must be 1..{MaxVendorVersionLength} characters."));
        }

        if (string.IsNullOrWhiteSpace(m.DisplayName) || m.DisplayName.Length > MaxDisplayNameLength)
        {
            errors.Add(new("displayName", $"Required, 1..{MaxDisplayNameLength} characters."));
        }

        if (string.IsNullOrWhiteSpace(m.Description) || m.Description.Length > MaxDescriptionLength)
        {
            errors.Add(new("description", $"Required, 1..{MaxDescriptionLength} characters."));
        }

        if (!IsValidHttpsUrl(m.Homepage, out var homepageProblem))
        {
            errors.Add(new("homepage", homepageProblem));
        }

        if (!IsValidSpdxExpression(m.License, out var licenseProblem))
        {
            errors.Add(new("license", licenseProblem));
        }
    }

    private static void ValidateScopeAndInstaller(Manifest m, List<ManifestError> errors, bool allowV11)
    {
        if (m.Scope.Count == 0 || m.Scope.Distinct(StringComparer.Ordinal).Count() != m.Scope.Count)
        {
            errors.Add(new("scope", "Must declare at least one scope, without duplicates."));
        }

        foreach (var scope in m.Scope)
        {
            if (scope is not ("user" or "machine"))
            {
                errors.Add(new("scope", $"'{scope}' is not a valid scope (user | machine)."));
            }

            if (scope == "machine" && !allowV11)
            {
                errors.Add(new("scope", "'machine' scope requires WinBrew v1.1 (MSI/EXE); " +
                    "the v1.0 client accepts only ['user'] portable packages (A9, D1)."));
            }
        }

        if (m.Installer is not null && !allowV11)
        {
            errors.Add(new("installer", "Installer declarations are a v1.1 feature; " +
                "the v1.0 client rejects installer-bearing manifests (A9, D1). " +
                "Installer declarations are executable-code trust decisions, not safe data."));
        }
        else if (allowV11)
        {
            ValidateInstaller(m, errors);
        }
    }

    /// <summary>v1.1 (A9/A12): the installer declaration must match the artifacts exactly and be fully explicit.</summary>
    private static void ValidateInstaller(Manifest m, List<ManifestError> errors)
    {
        var types = m.Artifacts.Select(a => a.InstallType).Distinct(StringComparer.Ordinal).ToList();
        if (types.Count > 1)
        {
            errors.Add(new("artifacts", "All artifacts of a release must share one installType (A9)."));
            return;
        }

        var type = types.FirstOrDefault() ?? "portable";
        var installer = m.Installer;
        if (type == "portable")
        {
            if (installer is not null)
            {
                errors.Add(new("installer", "Portable packages carry no installer declaration."));
            }

            return;
        }

        if (installer is null || installer.Type != type)
        {
            errors.Add(new("installer", $"{type} artifacts need installer.type = \"{type}\" (A12)."));
            return;
        }

        if (m.Dependencies.Count > 0)
        {
            errors.Add(new("dependencies", "Installer packages cannot declare dependencies in v1.1 (documented limitation)."));
        }

        if (type == "msi")
        {
            ValidateMsi(m, installer, errors);
        }
        else
        {
            ValidateExe(m, installer, errors);
        }
    }

    private static void ValidateMsi(Manifest m, Installers.InstallerDeclaration installer, List<ManifestError> errors)
    {
        if (installer.Msi is not { } msi || installer.Exe is not null)
        {
            errors.Add(new("installer.msi", "installer.type \"msi\" needs an msi object (and no exe object)."));
            return;
        }

        if (!Installers.InstallerArguments.GuidRegex().IsMatch(msi.ProductCode))
        {
            errors.Add(new("installer.msi.productCode", "Must be an uppercase braced GUID ({XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX})."));
        }

        if (msi.UpgradeCode is not null && !Installers.InstallerArguments.GuidRegex().IsMatch(msi.UpgradeCode))
        {
            errors.Add(new("installer.msi.upgradeCode", "Must be an uppercase braced GUID."));
        }

        ValidateProperties("installer.msi.properties", msi.Properties, reservedAllowed: false, errors);
        ValidateProperties("installer.msi.userProperties", msi.UserProperties, reservedAllowed: true, errors);
        if (m.Scope.Contains("user") && (msi.UserProperties is null || msi.UserProperties.Count == 0))
        {
            errors.Add(new("installer.msi.userProperties", "A 'user' scope MSI must declare the property set it was authored for " +
                "(e.g. ALLUSERS=2, MSIINSTALLPERUSER=1); per-user installs are never guessed (A12)."));
        }
    }

    private static void ValidateProperties(string field, IReadOnlyDictionary<string, string>? properties, bool reservedAllowed, List<ManifestError> errors)
    {
        foreach (var (name, value) in properties ?? new Dictionary<string, string>())
        {
            if (!Installers.InstallerArguments.PropertyNameRegex().IsMatch(name))
            {
                errors.Add(new(field, $"'{name}' is not an MSI public property name (uppercase)."));
            }
            else if (!reservedAllowed && Installers.InstallerArguments.ReservedProperties.Contains(name))
            {
                errors.Add(new(field, $"'{name}' is controlled by WinBrew (scope/reboot) and cannot be declared here."));
            }

            if (!Installers.InstallerArguments.PropertyValueRegex().IsMatch(value))
            {
                errors.Add(new(field, $"The value of '{name}' contains characters outside the safe set (no spaces, quotes or control characters)."));
            }
        }
    }

    private static void ValidateExe(Manifest m, Installers.InstallerDeclaration installer, List<ManifestError> errors)
    {
        if (installer.Exe is not { } exe || installer.Msi is not null)
        {
            errors.Add(new("installer.exe", "installer.type \"exe\" needs an exe object (and no msi object)."));
            return;
        }

        if (exe.SilentInstallArgs.Count == 0 || !exe.SilentInstallArgs.All(Installers.InstallerArguments.IsSafeArgument))
        {
            errors.Add(new("installer.exe.silentInstallArgs", "Explicit silent install arguments are required; switches are never guessed (A12)."));
        }

        var machineOnly = !m.Scope.Contains("user");
        ValidatePattern("installer.exe.silentUninstall.path", exe.SilentUninstall.Path, mustBeExe: true, machineOnly, errors);
        if (!exe.SilentUninstall.Args.All(Installers.InstallerArguments.IsSafeArgument))
        {
            errors.Add(new("installer.exe.silentUninstall.args", "Arguments must be non-empty text without control characters."));
        }

        var detection = exe.Detection;
        if (detection.UninstallKey is null && detection.File is null)
        {
            errors.Add(new("installer.exe.detection", "At least one detection rule (uninstallKey and/or file) is required (A12)."));
        }

        if (detection.UninstallKey is { } key && (key.Length is 0 or > 128 || key.Any(c => c is '\\' or '/' || char.IsControl(c))))
        {
            errors.Add(new("installer.exe.detection.uninstallKey", "Must be a single registry key name (no separators)."));
        }

        if (detection.File is { } file)
        {
            ValidatePattern("installer.exe.detection.file.path", file.Path, mustBeExe: false, machineOnly, errors);
            if (file.Version is not null && !Version.TryParse(file.Version, out _))
            {
                errors.Add(new("installer.exe.detection.file.version", "Must be a dotted numeric file version."));
            }
        }
    }

    private static void ValidatePattern(string field, string pattern, bool mustBeExe, bool machineOnly, List<ManifestError> errors)
    {
        if (Installers.InstallerPaths.Split(pattern, out var problem) is not { } split)
        {
            errors.Add(new(field, $"'{pattern}' {problem}."));
            return;
        }

        if (machineOnly && !Installers.InstallerPaths.IsMachineRoot(split.Root))
        {
            errors.Add(new(field, "A machine-only package must use a machine root ({ProgramFiles}, {ProgramFilesX86}, {ProgramData})."));
        }

        if (mustBeExe && !pattern.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(new(field, "Must name an .exe."));
        }
    }

    private static void ValidateArtifacts(Manifest m, List<ManifestError> errors, bool testHooksBuild, bool allowV11)
    {
        if (m.Artifacts.Count == 0)
        {
            errors.Add(new("artifacts", "At least one artifact is required (A9)."));
            return;
        }

        var seenArchs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (artifact, index) in m.Artifacts.Select((a, i) => (a, i)))
        {
            var field = $"artifacts[{index}]";

            if (!AllowedArchitectures.Contains(artifact.Architecture, StringComparer.Ordinal))
            {
                errors.Add(new(field + ".architecture",
                    $"'{artifact.Architecture}' is not valid (x64 | arm64 | x86)."));
            }
            else if (!seenArchs.Add(artifact.Architecture))
            {
                errors.Add(new(field + ".architecture", $"Duplicate architecture '{artifact.Architecture}'."));
            }

            if (!IsValidArtifactUrl(artifact.Url, testHooksBuild, out var urlProblem))
            {
                errors.Add(new(field + ".url", urlProblem));
            }

            if (!Sha256Regex.IsMatch(artifact.Sha256))
            {
                errors.Add(new(field + ".sha256", "Must be exactly 64 lowercase hex characters."));
            }

            if (artifact.Size < 0)
            {
                errors.Add(new(field + ".size", "Must be >= 0 (bytes)."));
            }

            if (!AllowedFormats.Contains(artifact.Format, StringComparer.Ordinal))
            {
                errors.Add(new(field + ".format", $"'{artifact.Format}' is not valid (zip | msi | exe)."));
            }

            if (!AllowedInstallTypes.Contains(artifact.InstallType, StringComparer.Ordinal))
            {
                errors.Add(new(field + ".installType", $"'{artifact.InstallType}' is not valid (portable | msi | exe)."));
            }

            // format/installType pairing (A9).
            if ((artifact.Format == "zip") != (artifact.InstallType == "portable"))
            {
                errors.Add(new(field, $"format '{artifact.Format}' and installType " +
                    $"'{artifact.InstallType}' are mismatched (zip pairs with portable; " +
                    "msi/exe pair with their install types)."));
            }

            if (!allowV11 && (artifact.InstallType is "msi" or "exe"))
            {
                errors.Add(new(field + ".installType", "MSI/EXE artifacts require WinBrew v1.1 " +
                    "(the v1.0 client accepts portable packages only, A3/D1)."));
            }
        }
    }

    private static void ValidateExecutables(Manifest m, List<ManifestError> errors)
    {
        var isPortable = m.Artifacts.All(a => a.InstallType == "portable") && m.Artifacts.Count > 0;

        if (m.Executables.Count > 0 && !isPortable)
        {
            errors.Add(new("executables", "executables[] is only valid on portable packages (A9)."));
        }

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (executable, index) in m.Executables.Select((e, i) => (e, i)))
        {
            var field = $"executables[{index}]";

            if (!ShimNameRegex.IsMatch(executable.Name) ||
                executable.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                Identifiers.ReservedDeviceNames.Contains(executable.Name.Split('.')[0].ToUpperInvariant()))
            {
                errors.Add(new(field + ".name", $"'{executable.Name}' is not a valid shim name " +
                    "(no path separators, no '.exe' suffix — the binary becomes <name>.exe — " +
                    "no reserved device names)."));
            }
            else if (!seenNames.Add(executable.Name))
            {
                errors.Add(new(field + ".name", $"Duplicate shim name '{executable.Name}'."));
            }

            if (!IsValidRelativeExeTarget(executable.Target, out var targetProblem))
            {
                errors.Add(new(field + ".target", targetProblem));
            }
        }
    }

    private static void ValidateDependencies(Manifest m, List<ManifestError> errors)
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (dependency, index) in m.Dependencies.Select((d, i) => (d, i)))
        {
            var field = $"dependencies[{index}]";

            if (!Identifiers.TrySplitReference(dependency.Id, out var repo, out var id))
            {
                errors.Add(new(field + ".id", $"'{dependency.Id}' is not a valid dependency id " +
                    "(unqualified '<id>' or qualified '<repo>/<id>', A9)."));
            }
            else
            {
                if (repo is null && id == m.Id)
                {
                    errors.Add(new(field + ".id", $"A package cannot depend on itself ('{dependency.Id}')."));
                }

                if (!seenIds.Add(dependency.Id))
                {
                    errors.Add(new(field + ".id", $"Duplicate dependency id '{dependency.Id}'."));
                }
            }

            try
            {
                VersionOps.ValidateRange(dependency.Range, field);
            }
            catch (WinBrewException ex)
            {
                errors.Add(new(field + ".range", ex.Message));
            }
        }
    }

    // ---- URL helpers ----------------------------------------------------

    private static bool IsValidHttpsUrl(string url, out string problem)
    {
        problem = string.Empty;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            problem = "Must be an absolute HTTPS URL.";
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            problem = $"Must be HTTPS (got '{uri.Scheme}').";
            return false;
        }

        return true;
    }

    private static bool IsValidArtifactUrl(string url, bool testHooksBuild, out string problem)
    {
        problem = string.Empty;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            problem = "Must be an absolute URL.";
            return false;
        }

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return true;
        }

        // A13: the only HTTP exception is localhost/loopback, and only in
        // test-hooks builds; release builds reject it outright.
        if (testHooksBuild && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)
        {
            return true;
        }

        problem = testHooksBuild
            ? "Only HTTPS is allowed (except http://loopback in test-hooks builds, A13)."
            : "Must be HTTPS (A13).";
        return false;
    }

    // ---- executable target rules ----------------------------------------

    internal static bool IsValidRelativeExeTarget(string target, out string problem)
    {
        problem = string.Empty;

        if (string.IsNullOrWhiteSpace(target))
        {
            problem = "Target is required.";
            return false;
        }

        // Forward slashes only (ZIP entry semantics); backslash would be
        // ambiguous with Windows separators (A13 backslash/forward-slash rule).
        if (target.Contains('\\'))
        {
            problem = "Use '/' separators in targets (backslash is rejected to avoid ambiguity).";
            return false;
        }

        if (target.StartsWith('/') || target.StartsWith("//", StringComparison.Ordinal))
        {
            problem = "Target must be relative to the package root.";
            return false;
        }

        if (Regex.IsMatch(target, "^[A-Za-z]:"))
        {
            problem = "Drive letters are not allowed in targets.";
            return false;
        }

        var segments = target.Split('/');
        foreach (var segment in segments)
        {
            if (segment == "..")
            {
                problem = "Target must stay inside the package ('..' is rejected).";
                return false;
            }

            if (segment.Length == 0)
            {
                problem = "Empty path segment in target.";
                return false;
            }

            if (Identifiers.ReservedDeviceNames.Contains(segment.Split('.')[0].ToUpperInvariant()))
            {
                problem = $"Reserved device name '{segment}' in target.";
                return false;
            }
        }

        if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            problem = "Target must be an .exe (A9; shims for non-.exe entry points are a non-goal, A3).";
            return false;
        }

        return true;
    }

    // ---- SPDX (pragmatic structural validation) --------------------------

    /// <summary>
    /// Structural SPDX expression validation (A9): license identifiers or
    /// LicenseRef-* ids connected by AND/OR, optional WITH exceptions,
    /// balanced parentheses. Membership in the SPDX list is NOT enforced —
    /// the list evolves; manifest-spec.md documents this choice.
    /// </summary>
    internal static bool IsValidSpdxExpression(string expression, out string problem)
    {
        problem = string.Empty;

        if (string.IsNullOrWhiteSpace(expression))
        {
            problem = "License expression is required (SPDX).";
            return false;
        }

        // Tokenize with whitespace BETWEEN tokens allowed; the tokens must
        // cover the entire string (only whitespace may remain unmatched).
        var tokenMatches = Regex.Matches(expression, @"\s+|(?<id>[A-Za-z0-9][A-Za-z0-9.\-+]*)|\(|\)");
        var tokens = new List<(string Value, int Index)>();
        var covered = 0;
        foreach (Match match in tokenMatches)
        {
            if (match.Index != covered)
            {
                problem = $"'{expression}' is not a valid SPDX expression (unexpected characters at {covered}).";
                return false;
            }

            covered += match.Length;
            if (match.Value.Trim().Length > 0)
            {
                tokens.Add((match.Value, match.Index));
            }
        }

        if (covered != expression.Length || tokens.Count == 0)
        {
            problem = $"'{expression}' is not a valid SPDX expression (unexpected characters).";
            return false;
        }

        var depth = 0;
        var expectOperand = true;
        var afterWith = false; // the token after WITH is the exception id — not a new operand
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i].Value;
            switch (token)
            {
                case "(":
                    depth++;
                    if (!expectOperand)
                    {
                        problem = $"Unexpected '(' at position {tokens[i].Index} in '{expression}'.";
                        return false;
                    }

                    break;
                case ")":
                    depth--;
                    if (depth < 0 || expectOperand)
                    {
                        problem = $"Unbalanced ')' at position {tokens[i].Index} in '{expression}'.";
                        return false;
                    }

                    break;
                case "AND" or "OR":
                    if (expectOperand)
                    {
                        problem = $"Operator '{token}' needs a left operand in '{expression}'.";
                        return false;
                    }

                    expectOperand = true;
                    afterWith = false;
                    break;
                case "WITH":
                    if (expectOperand || i == tokens.Count - 1)
                    {
                        problem = $"'WITH' needs an exception id after it in '{expression}'.";
                        return false;
                    }

                    afterWith = true;
                    break;
                default:
                    if (afterWith)
                    {
                        afterWith = false; // exception id consumed; still not expecting a new operand
                        break;
                    }

                    if (!expectOperand)
                    {
                        problem = $"Missing operator before '{token}' in '{expression}'.";
                        return false;
                    }

                    expectOperand = false;
                    break;
            }
        }

        if (depth != 0)
        {
            problem = $"Unbalanced parentheses in '{expression}'.";
            return false;
        }

        if (expectOperand)
        {
            problem = $"'{expression}' ends with an operator (missing operand).";
            return false;
        }

        return true;
    }
}
