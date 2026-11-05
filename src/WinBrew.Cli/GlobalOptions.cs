using System.CommandLine;

namespace WinBrew.Cli;

/// <summary>
/// Global options (A8), recursive from the root command.
/// Note (SCL 2.0 stable API): Option&lt;T&gt;(name, "...") treats the second
/// string as an ALIAS, not a description — descriptions are the Description
/// property (set in the object initializer below).
/// </summary>
internal static class GlobalOptions
{
    public static readonly Option<bool> Json = new("--json")
    {
        Description = "Emit exactly one JSON document on stdout (A8).",
        Recursive = true,
    };

    public static readonly Option<bool> NoColor = new("--no-color")
    {
        Description = "Disable colored output (overrides color config and NO_COLOR).",
        Recursive = true,
    };

    public static readonly Option<bool> NonInteractive = new("--non-interactive")
    {
        Description = "Never prompt; missing consent exits 10 (A8).",
        Recursive = true,
    };

    public static readonly Option<bool> Yes = new("--yes")
    {
        Description = "Approve the displayed operation plan (A8; never implies trust-root/modify-path/allow-installer/scope consents).",
        Recursive = true,
    };

    public static readonly Option<bool> Verbose = new("--verbose")
    {
        Description = "Verbose diagnostics to stderr.",
        Recursive = true,
    };

    public static readonly Option<bool> Quiet = new("--quiet")
    {
        Description = "Suppress informational output; errors still appear on stderr.",
        Recursive = true,
    };

    public static readonly Option<bool> Offline = new("--offline")
    {
        Description = "Use only verified cache/metadata; never refresh (A15).",
        Recursive = true,
    };

    public static void AddToRoot(RootCommand root)
    {
        root.Options.Add(Json);
        root.Options.Add(NoColor);
        root.Options.Add(NonInteractive);
        root.Options.Add(Yes);
        root.Options.Add(Verbose);
        root.Options.Add(Quiet);
        root.Options.Add(Offline);
    }
}
