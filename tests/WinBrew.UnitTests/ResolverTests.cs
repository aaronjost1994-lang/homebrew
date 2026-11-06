using System.Runtime.InteropServices;
using WinBrew.Core;
using WinBrew.Core.Resolution;

namespace WinBrew.UnitTests;

/// <summary>
/// PubGrub (D2) — classic cases from the PubGrub documentation translated to
/// NuGet interval notation, plus the WinBrew rules: diamonds, pins, cycles,
/// architecture availability (A10), dependency qualification (A9),
/// prereleases (rule 6), install-vs-upgrade preference (rules 2–3),
/// explanations and determinism.
/// </summary>
public class ResolverTests
{
    // ---- tiny catalog DSL -------------------------------------------------------------------

    private sealed class Catalog
    {
        private readonly List<(string Repo, string Id, string Version, string[] Archs, (string Id, string Range)[] Deps)> _rows = [];

        public HashSet<string> Repos { get; } = new(StringComparer.Ordinal) { "main" };

        public Catalog V(string qualified, string version, params (string Id, string Range)[] deps) =>
            VArch(qualified, version, ["x64"], deps);

        public Catalog VArch(string qualified, string version, string[] archs, params (string Id, string Range)[] deps)
        {
            var parts = qualified.Split('/');
            _rows.Add((parts[0], parts[1], version, archs, deps));
            Repos.Add(parts[0]);
            return this;
        }

        public ResolverSnapshot Snapshot(Random? shuffle = null)
        {
            var packages = _rows.GroupBy(r => (r.Repo, r.Id)).Select(g =>
            {
                var versions = g.Select(r => new CandidateVersion(r.Version, r.Archs, r.Deps.Select(d => new CandidateDependency(d.Id, d.Range)).ToList())).ToList();
                if (shuffle is not null)
                {
                    versions = [.. versions.OrderBy(_ => shuffle.Next())];
                }

                return new SnapshotPackage(g.Key.Repo, g.Key.Id, versions);
            }).ToList();
            if (shuffle is not null)
            {
                packages = [.. packages.OrderBy(_ => shuffle.Next())];
            }

            return new ResolverSnapshot(packages, Repos);
        }
    }

    private static ResolverResult Solve(
        Catalog catalog,
        IEnumerable<string> roots,
        Dictionary<string, string>? installed = null,
        Dictionary<string, string>? pins = null,
        ResolveMode mode = ResolveMode.Install,
        Architecture host = Architecture.X64,
        Random? shuffle = null,
        IReadOnlySet<string>? targets = null)
    {
        var requirements = roots.Select(r =>
        {
            var parts = r.Split(' ', 2);
            var range = parts.Length > 1 ? parts[1] : null;
            var exact = range is not null && !range.StartsWith('[') && !range.StartsWith('(') ? range : null;
            return new RootRequirement(parts[0], exact, exact is null ? range : null, "you requested " + parts[0]);
        }).ToList();
        return PubGrubResolver.Resolve(new ResolverRequest(catalog.Snapshot(shuffle), host, requirements,
            installed ?? [], pins ?? [], mode, targets ?? new HashSet<string>()));
    }

    private static Dictionary<string, string> Versions(ResolverResult result)
    {
        Assert.True(result.Success, result.Conflict is null ? "" : string.Join("\n", result.Conflict.Explanation));
        return result.Packages.ToDictionary(p => p.Qualified, p => p.Version);
    }

    // ---- classic PubGrub cases ---------------------------------------------------------------

    [Fact]
    public void NoConflicts()
    {
        var c = new Catalog()
            .V("main/foo", "1.0.0", ("bar", "[1.0.0,2.0.0)"))
            .V("main/bar", "1.0.0").V("main/bar", "1.1.0").V("main/bar", "2.0.0");
        Assert.Equal(new Dictionary<string, string> { ["main/foo"] = "1.0.0", ["main/bar"] = "1.1.0" }, Versions(Solve(c, ["main/foo"])));
    }

    [Fact]
    public void AvoidingConflictDuringDecisionMaking()
    {
        var c = new Catalog()
            .V("main/foo", "1.0.0").V("main/foo", "1.1.0", ("bar", "[2.0.0,3.0.0)"))
            .V("main/bar", "1.0.0").V("main/bar", "1.1.0").V("main/bar", "2.0.0");
        var v = Versions(Solve(c, ["main/foo [1.0.0,2.0.0)", "main/bar [1.0.0,2.0.0)"]));
        Assert.Equal("1.0.0", v["main/foo"]);
        Assert.Equal("1.1.0", v["main/bar"]);
    }

    [Fact]
    public void PerformingConflictResolution()
    {
        var c = new Catalog()
            .V("main/foo", "1.0.0").V("main/foo", "2.0.0", ("bar", "[1.0.0,2.0.0)"))
            .V("main/bar", "1.0.0", ("foo", "[1.0.0,2.0.0)"));
        var v = Versions(Solve(c, ["main/foo [1.0.0,)"]));
        Assert.Equal("1.0.0", v["main/foo"]);
        Assert.False(v.ContainsKey("main/bar"));
    }

    [Fact]
    public void ConflictResolutionWithAPartialSatisfier()
    {
        var c = new Catalog()
            .V("main/foo", "1.0.0").V("main/foo", "1.1.0", ("left", "[1.0.0,2.0.0)"), ("right", "[1.0.0,2.0.0)"))
            .V("main/left", "1.0.0", ("shared", "[1.0.0,)"))
            .V("main/right", "1.0.0", ("shared", "(,2.0.0)"))
            .V("main/shared", "2.0.0").V("main/shared", "1.0.0", ("target", "[1.0.0,2.0.0)"))
            .V("main/target", "2.0.0").V("main/target", "1.0.0");
        var v = Versions(Solve(c, ["main/foo [1.0.0,2.0.0)", "main/target [2.0.0,3.0.0)"]));
        Assert.Equal("1.0.0", v["main/foo"]);
        Assert.Equal("2.0.0", v["main/target"]);
        Assert.Equal(2, v.Count);
    }

    [Fact]
    public void LinearErrorReporting_ExplainsWithQualifiedIdsAndOriginalRanges()
    {
        var c = new Catalog()
            .V("main/foo", "1.0.0", ("bar", "[2.0.0,3.0.0)"))
            .V("main/bar", "2.0.0", ("baz", "[3.0.0,4.0.0)"))
            .V("main/baz", "1.0.0").V("main/baz", "3.0.0");
        var result = Solve(c, ["main/foo [1.0.0,2.0.0)", "main/baz [1.0.0,2.0.0)"]);
        Assert.False(result.Success);
        var text = string.Join("\n", result.Conflict!.Explanation);
        Assert.Contains("main/foo 1.0.0 depends on main/bar [2.0.0,3.0.0)", text, StringComparison.Ordinal);
        Assert.Contains("main/bar 2.0.0 depends on main/baz [3.0.0,4.0.0)", text, StringComparison.Ordinal);
        Assert.Contains("version solving failed", result.Conflict.Explanation[^1], StringComparison.Ordinal);
        Assert.Contains(result.Conflict.Causes, k => k.Kind == "dependency" && k.Range == "[2.0.0,3.0.0)");
    }

    [Fact]
    public void BranchingErrorReporting_FailsWithAnExplanation()
    {
        var c = new Catalog()
            .V("main/foo", "1.0.0", ("a", "[1.0.0,2.0.0)"), ("b", "[1.0.0,2.0.0)"))
            .V("main/foo", "1.1.0", ("x", "[1.0.0,2.0.0)"), ("y", "[1.0.0,2.0.0)"))
            .V("main/a", "1.0.0", ("b", "[2.0.0,3.0.0)"))
            .V("main/b", "1.0.0").V("main/b", "2.0.0")
            .V("main/x", "1.0.0", ("y", "[2.0.0,3.0.0)"))
            .V("main/y", "1.0.0").V("main/y", "2.0.0");
        var result = Solve(c, ["main/foo [1.0.0,2.0.0)"]);
        Assert.False(result.Success);
        Assert.True(result.Conflict!.Explanation.Count >= 2);
    }

    // ---- WinBrew rules ----------------------------------------------------------------------------

    [Fact]
    public void Diamond_PicksTheHighestSharedVersion_AndOrdersDependenciesFirst()
    {
        var c = new Catalog()
            .V("main/app", "1.0.0", ("left", "[1.0.0,2.0.0)"), ("right", "[1.0.0,2.0.0)"))
            .V("main/left", "1.0.0", ("base", "[1.0.0,2.0.0)"))
            .V("main/right", "1.0.0", ("base", "[1.2.0,3.0.0)"))
            .V("main/base", "1.0.0").V("main/base", "1.2.0").V("main/base", "1.5.0").V("main/base", "2.0.0");
        var result = Solve(c, ["main/app"]);
        Assert.Equal("1.5.0", Versions(result)["main/base"]);
        var order = result.Packages.Select(p => p.Qualified).ToList();
        Assert.Equal("main/base", order[0]);
        Assert.Equal("main/app", order[^1]);
        Assert.True(order.IndexOf("main/left") < order.IndexOf("main/app"));
    }

    [Fact]
    public void PinnedConflict_IsExplained()
    {
        var c = new Catalog()
            .V("main/app", "1.0.0", ("lib", "[2.0.0,3.0.0)"))
            .V("main/lib", "1.0.0").V("main/lib", "2.0.0");
        var result = Solve(c, ["main/app"], installed: new() { ["main/lib"] = "1.0.0" }, pins: new() { ["main/lib"] = "1.0.0" });
        Assert.False(result.Success);
        Assert.Contains(result.Conflict!.Causes, k => k.Kind == "pinned" && k.Package == "main/lib");
        Assert.Contains("pinned to 1.0.0", string.Join("\n", result.Conflict.Explanation), StringComparison.Ordinal);
    }

    [Fact]
    public void PinnedPackage_StaysAtItsVersion_DuringUpgrade()
    {
        var c = new Catalog().V("main/lib", "1.0.0").V("main/lib", "2.0.0");
        var v = Versions(Solve(c, ["main/lib"], installed: new() { ["main/lib"] = "1.0.0" }, pins: new() { ["main/lib"] = "1.0.0" }, mode: ResolveMode.Upgrade));
        Assert.Equal("1.0.0", v["main/lib"]);
    }

    [Fact]
    public void Cycle_IsRefusedWithItsPath()
    {
        var c = new Catalog()
            .V("main/a", "1.0.0", ("b", "[1.0.0,2.0.0)"))
            .V("main/b", "1.0.0", ("c", "[1.0.0,2.0.0)"))
            .V("main/c", "1.0.0", ("a", "[1.0.0,2.0.0)"));
        var ex = Assert.Throws<WinBrewException>(() => Solve(c, ["main/a"]));
        Assert.Equal(WinBrewErrorCode.ConflictOrRefused, ex.Code);
        Assert.Contains("main/a → main/b → main/c → main/a", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ArchitectureUnavailable_FallsBackToAnOlderVersion()
    {
        var c = new Catalog().V("main/tool", "1.0.0").VArch("main/tool", "2.0.0", ["arm64"]);
        Assert.Equal("1.0.0", Versions(Solve(c, ["main/tool"]))["main/tool"]);
        Assert.Equal("2.0.0", Versions(Solve(c, ["main/tool"], host: Architecture.Arm64))["main/tool"]);
    }

    [Fact]
    public void ArchitectureUnavailable_Everywhere_IsExplained()
    {
        var c = new Catalog().VArch("main/tool", "1.0.0", ["arm64"]);
        var result = Solve(c, ["main/tool"]);
        Assert.False(result.Success);
        Assert.Contains("has no artifact for this x64 host", string.Join("\n", result.Conflict!.Explanation), StringComparison.Ordinal);
    }

    [Fact]
    public void X86Fallback_IsReportedOnTheSelection()
    {
        var c = new Catalog().VArch("main/legacy", "1.0.0", ["x86"]);
        var selected = Assert.Single(Solve(c, ["main/legacy"]).Packages);
        Assert.True(selected.Architecture.IsX86Fallback);
    }

    [Fact]
    public void UnqualifiedDependency_BelongsToTheDeclaringRepository()
    {
        var c = new Catalog()
            .V("main/app", "1.0.0", ("lib", "[1.0.0,2.0.0)"))
            .V("main/lib", "1.0.0")
            .V("community/lib", "1.5.0");
        var result = Solve(c, ["main/app"]);
        Assert.Contains(result.Packages, p => p.Qualified == "main/lib");
        Assert.DoesNotContain(result.Packages, p => p.Repo == "community");
    }

    [Fact]
    public void QualifiedCrossRepositoryDependency_Resolves_WhenConfigured()
    {
        var c = new Catalog()
            .V("main/app", "1.0.0", ("community/lib", "[1.0.0,2.0.0)"))
            .V("community/lib", "1.5.0");
        Assert.Equal("1.5.0", Versions(Solve(c, ["main/app"]))["community/lib"]);
    }

    [Fact]
    public void QualifiedCrossRepositoryDependency_OnAnUnconfiguredRepository_NamesIt()
    {
        var c = new Catalog().V("main/app", "1.0.0", ("ghost/lib", "[1.0.0,2.0.0)"));
        c.Repos.Remove("ghost");
        var result = Solve(c, ["main/app"]);
        Assert.False(result.Success);
        Assert.Equal("ghost", result.Conflict!.MissingRepository);
    }

    [Fact]
    public void MissingPackage_IsExplained()
    {
        var c = new Catalog().V("main/app", "1.0.0", ("nothere", "[1.0.0,2.0.0)"));
        var result = Solve(c, ["main/app"]);
        Assert.False(result.Success);
        Assert.Contains("main/nothere", string.Join("\n", result.Conflict!.Explanation), StringComparison.Ordinal);
    }

    [Fact]
    public void Prereleases_OnlyWhenRequested()
    {
        var c = new Catalog().V("main/tool", "1.0.0").V("main/tool", "2.0.0-beta.1");
        Assert.Equal("1.0.0", Versions(Solve(c, ["main/tool"]))["main/tool"]);
        Assert.Equal("2.0.0-beta.1", Versions(Solve(c, ["main/tool 2.0.0-beta.1"]))["main/tool"]);
        Assert.Equal("2.0.0-beta.1", Versions(Solve(c, ["main/tool [2.0.0-alpha,3.0.0)"]))["main/tool"]);
    }

    [Fact]
    public void Install_PrefersTheInstalledVersion_Upgrade_TakesTheHighest()
    {
        var c = new Catalog()
            .V("main/app", "1.0.0", ("lib", "[1.0.0,3.0.0)"))
            .V("main/lib", "1.0.0").V("main/lib", "2.0.0");
        var installed = new Dictionary<string, string> { ["main/lib"] = "1.0.0" };
        Assert.Equal("1.0.0", Versions(Solve(c, ["main/app"], installed))["main/lib"]);
        Assert.Equal("2.0.0", Versions(Solve(c, ["main/app"], installed, mode: ResolveMode.Upgrade))["main/lib"]);
        // Upgrade with explicit targets leaves the others at their installed versions.
        Assert.Equal("1.0.0", Versions(Solve(c, ["main/app"], installed, mode: ResolveMode.Upgrade,
            targets: new HashSet<string> { "main/app" }))["main/lib"]);
    }

    // ---- determinism ----------------------------------------------------------------------------------

    private static Catalog Busy() => new Catalog()
        .V("main/app", "1.0.0", ("left", "[1.0.0,3.0.0)"), ("right", "[1.0.0,3.0.0)"))
        .V("main/app", "2.0.0", ("left", "[2.0.0,3.0.0)"), ("right", "[1.0.0,2.0.0)"))
        .V("main/left", "1.0.0", ("base", "[1.0.0,2.0.0)")).V("main/left", "2.0.0", ("base", "[2.0.0,3.0.0)"))
        .V("main/right", "1.0.0", ("base", "[1.0.0,3.0.0)")).V("main/right", "2.0.0", ("base", "[1.0.0,2.0.0)"))
        .V("main/base", "1.0.0").V("main/base", "1.5.0").V("main/base", "2.0.0").V("main/base", "2.1.0");

    [Fact]
    public void SamePlan_Across100RandomizedMetadataOrderings()
    {
        var expected = Solve(Busy(), ["main/app"]).Packages.Select(p => $"{p.Qualified}@{p.Version}").ToList();
        for (var seed = 0; seed < 100; seed++)
        {
            var actual = Solve(Busy(), ["main/app"], shuffle: new Random(seed)).Packages.Select(p => $"{p.Qualified}@{p.Version}").ToList();
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void SameExplanation_Across100RandomizedOrderings()
    {
        var c = () => new Catalog()
            .V("main/foo", "1.0.0", ("bar", "[2.0.0,3.0.0)"))
            .V("main/bar", "2.0.0", ("baz", "[3.0.0,4.0.0)"))
            .V("main/baz", "1.0.0").V("main/baz", "3.0.0");
        var expected = Solve(c(), ["main/foo", "main/baz [1.0.0,2.0.0)"]).Conflict!.Explanation;
        for (var seed = 0; seed < 100; seed++)
        {
            Assert.Equal(expected, Solve(c(), ["main/foo", "main/baz [1.0.0,2.0.0)"], shuffle: new Random(seed)).Conflict!.Explanation);
        }
    }

    [Fact]
    public void SamePlan_WhetherOrNotAnotherRepositoryHasSameNamedPackages()
    {
        var withoutOther = Solve(Busy(), ["main/app"]).Packages.Select(p => $"{p.Qualified}@{p.Version}").ToList();
        var other = Busy().V("other/base", "9.0.0").V("other/left", "9.0.0").V("other/app", "9.0.0");
        var withOther = Solve(other, ["main/app"]).Packages.Select(p => $"{p.Qualified}@{p.Version}").ToList();
        Assert.Equal(withoutOther, withOther);
    }

    [Fact]
    public void TheResolver_LivesInCore_WhichHasNoIoDependencies()
    {
        // D2: pure by construction — Core references no Application/Infrastructure assembly,
        // and the resolver's only input is the ResolverRequest snapshot.
        var references = typeof(PubGrubResolver).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();
        Assert.DoesNotContain(references, n => n!.StartsWith("WinBrew.", StringComparison.Ordinal));
        Assert.DoesNotContain(references, n => n!.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) || n!.Contains("Http", StringComparison.OrdinalIgnoreCase));
        Assert.Single(typeof(PubGrubResolver).GetMethod(nameof(PubGrubResolver.Resolve))!.GetParameters(), p => p.ParameterType == typeof(ResolverRequest));
    }
}
