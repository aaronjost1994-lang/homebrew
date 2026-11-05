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
/// rejects installer-bearing manifests; Phase E flips that switch.
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

        if (m.Installer is not null && JsonElementIsDefined(m.Installer.Value) && !allowV11)
        {
            errors.Add(new("installer", "Installer declarations are a v1.1 feature; " +
                "the v1.0 client rejects installer-bearing manifests (A9, D1). " +
                "Installer declarations are executable-code trust decisions, not safe data."));
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

        var tokens = Regex.Matches(expression, @"[A-Za-z0-9][A-Za-z0-9.\-+]*|\(|\)");
        if (tokens.Count == 0 || string.Concat(tokens.Select(t => t.Value)) != expression.Replace(" ", string.Empty, StringComparison.Ordinal))
        {
            problem = $"'{expression}' is not a valid SPDX expression (unexpected characters).";
            return false;
        }

        var depth = 0;
        var expectOperand = true;
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
                    break;
                case "WITH":
                    if (expectOperand || i == tokens.Count - 1)
                    {
                        problem = $"'WITH' needs an exception id after it in '{expression}'.";
                        return false;
                    }

                    break;
                default:
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

    private static bool JsonElementIsDefined(System.Text.Json.JsonElement element) =>
        element.ValueKind is not (System.Text.Json.JsonValueKind.Undefined or System.Text.Json.JsonValueKind.Null);
}
