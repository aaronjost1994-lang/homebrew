using System.Globalization;
using NuGet.Versioning;
using WinBrew.Core.Artifacts;

namespace WinBrew.Core.Resolution;

/// <summary>
/// PubGrub (D2): incompatibility-based version solving with conflict-driven
/// clause learning and derivation-tree explanations. Pure — it consumes only
/// the <see cref="ResolverRequest"/> snapshot and performs no I/O.
///
/// Determinism (D2 rules 4–5): unit propagation always runs to completion
/// first; the next package to decide is the undecided package with an
/// active (positive) constraint whose normalized <c>&lt;repo&gt;/&lt;id&gt;</c> is
/// ordinally smallest; the version follows rules 1–3 (pins fixed; install
/// prefers the installed version; upgrade takes the highest). Every input
/// collection is sorted before use, so input order never changes the plan.
/// </summary>
public static class PubGrubResolver
{
    public static ResolverResult Resolve(ResolverRequest request) => new Solver(request).Run();
}

internal readonly record struct Term(int Package, VersionSet Set);

internal enum IncompatKind
{
    Root,
    Requirement,
    Dependency,
    Unavailable,
    Pinned,
    NoVersions,
    Derived,
}

internal sealed class Incompat(IEnumerable<Term> terms, IncompatKind kind)
{
    /// <summary>
    /// "not (t1 and t2 …)": terms on the same package intersect, and a term
    /// that is always true (it covers every state, e.g. "not in any version"
    /// of a package with no catalog versions) is dropped — not (true ∧ X) ≡ not X.
    /// </summary>
    public IReadOnlyList<Term> Terms { get; } = Normalize(terms);

    public IncompatKind Kind { get; } = kind;

    public Incompat? Left { get; init; }

    public Incompat? Right { get; init; }

    /// <summary>External facts: the text explanations use, plus the structured cause.</summary>
    public ConflictCause? External { get; init; }

    public string? MissingRepository { get; init; }

    private static List<Term> Normalize(IEnumerable<Term> terms)
    {
        var merged = new Dictionary<int, VersionSet>();
        foreach (var t in terms)
        {
            merged[t.Package] = merged.TryGetValue(t.Package, out var s) ? s.And(t.Set) : t.Set;
        }

        return merged.Where(kv => !kv.Value.IsFull).OrderBy(kv => kv.Key).Select(kv => new Term(kv.Key, kv.Value)).ToList();
    }
}

internal sealed class Solver
{
    private const int RootIndex = 0;
    private const string RootName = "$root";
    private const int MaxSteps = 1_000_000;

    private sealed class Package(int index, string qualified, string repo, string id, IReadOnlyList<(NuGetVersion Parsed, CandidateVersion Entry)> universe)
    {
        public int Index { get; } = index;
        public string Qualified { get; } = qualified;
        public string Repo { get; } = repo;
        public string Id { get; } = id;
        public IReadOnlyList<(NuGetVersion Parsed, CandidateVersion Entry)> Universe { get; } = universe;
        public int Size => Universe.Count + 1;
        public bool Unknown { get; init; }
    }

    private sealed record Assignment(int Package, VersionSet Set, int Level, int Index, Incompat? Cause);

    private readonly ResolverRequest _request;
    private readonly Dictionary<string, SnapshotPackage> _snapshot;
    private readonly HashSet<string> _prereleaseAllowed;
    private readonly List<Package> _packages = [];
    private readonly Dictionary<string, int> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<int, List<Incompat>> _incompatsByPackage = [];
    private readonly Dictionary<(int Package, VersionSet Set), string> _labels = [];

    // partial solution
    private readonly List<Assignment> _assignments = [];
    private readonly Dictionary<int, VersionSet> _accumulated = [];
    private readonly Dictionary<int, int> _decisions = [];
    private int _level;

    public Solver(ResolverRequest request)
    {
        _request = request;
        _snapshot = request.Snapshot.Packages
            .OrderBy(p => p.Qualified, StringComparer.Ordinal)
            .GroupBy(p => p.Qualified, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        _prereleaseAllowed = ComputePrereleaseAllowance();

        var root = new Package(RootIndex, RootName, string.Empty, RootName, [(new NuGetVersion(0, 0, 0), new CandidateVersion("0.0.0", [], []))]);
        _packages.Add(root);
        _byName[RootName] = RootIndex;
    }

    // ==== setup ================================================================================

    /// <summary>D2 rule 6: prereleases only when requested exactly or named by a prerelease range bound (or already installed).</summary>
    private HashSet<string> ComputePrereleaseAllowance()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in _request.Roots)
        {
            if ((root.ExactVersion is not null && NuGetVersion.Parse(root.ExactVersion).IsPrerelease) ||
                (root.Range is not null && VersionOps.RangeRequestsPrerelease(VersionOps.ValidateRange(root.Range, root.Qualified))))
            {
                allowed.Add(root.Qualified);
            }
        }

        foreach (var (name, version) in _request.Installed)
        {
            if (NuGetVersion.TryParse(version, out var v) && v.IsPrerelease)
            {
                allowed.Add(name);
            }
        }

        foreach (var package in _snapshot.Values)
        {
            foreach (var version in package.Versions)
            {
                foreach (var dep in version.Dependencies)
                {
                    if (VersionOps.RangeRequestsPrerelease(VersionOps.ValidateRange(dep.Range, package.Qualified)))
                    {
                        allowed.Add(Qualify(package.Repo, dep.Id));
                    }
                }
            }
        }

        return allowed;
    }

    /// <summary>A9: an unqualified dependency belongs to the declaring package's repository.</summary>
    private static string Qualify(string declaringRepo, string dependencyId)
    {
        Identifiers.TrySplitReference(dependencyId, out var repo, out var id);
        return Identifiers.Qualified(repo ?? declaringRepo, id!);
    }

    private int GetPackage(string qualified)
    {
        if (_byName.TryGetValue(qualified, out var existing))
        {
            return existing;
        }

        var slash = qualified.IndexOf('/', StringComparison.Ordinal);
        var repo = qualified[..slash];
        var id = qualified[(slash + 1)..];
        var allowPre = _prereleaseAllowed.Contains(qualified);

        var universe = _snapshot.TryGetValue(qualified, out var snap)
            ? snap.Versions
                .Select(v => (Parsed: NuGetVersion.Parse(v.Version), Entry: v))
                .Where(v => allowPre || !v.Parsed.IsPrerelease)
                .GroupBy(v => v.Parsed)
                .Select(g => g.First())
                .OrderBy(v => v.Parsed)
                .ToList()
            : [];

        var package = new Package(_packages.Count, qualified, repo, id, universe) { Unknown = snap is null };
        _packages.Add(package);
        _byName[qualified] = package.Index;
        AddUnaryIncompatibilities(package);
        return package.Index;
    }

    /// <summary>Facts about single versions: no artifact for this host (A10), an unconfigured dependency repository (A9), pins (D2 rule 1).</summary>
    private void AddUnaryIncompatibilities(Package p)
    {
        for (var i = 0; i < p.Universe.Count; i++)
        {
            var (_, entry) = p.Universe[i];
            if (HostArchitectureRules.Select(_request.Host, entry.Architectures) is null)
            {
                var reason = $"has no artifact for this {HostArchitectureRules.HostName(_request.Host)} host " +
                             $"(offers {string.Join(", ", entry.Architectures)}; A10)";
                Add(new Incompat([new Term(p.Index, VersionSet.OfVersions(p.Size, [i]))], IncompatKind.Unavailable)
                {
                    External = new ConflictCause("unavailable", p.Qualified, entry.Version, null, null, $"{p.Qualified} {entry.Version} {reason}"),
                });
                continue;
            }

            foreach (var dep in entry.Dependencies.OrderBy(d => d.Id, StringComparer.Ordinal))
            {
                var target = Qualify(p.Repo, dep.Id);
                var targetRepo = target[..target.IndexOf('/', StringComparison.Ordinal)];
                if (!_request.Snapshot.ConfiguredRepositories.Contains(targetRepo))
                {
                    Add(new Incompat([new Term(p.Index, VersionSet.OfVersions(p.Size, [i]))], IncompatKind.Unavailable)
                    {
                        External = new ConflictCause("missingRepository", p.Qualified, entry.Version, target, dep.Range,
                            $"{p.Qualified} {entry.Version} depends on {target} {dep.Range}, but repository '{targetRepo}' is not configured (A9)"),
                        MissingRepository = targetRepo,
                    });
                    break;
                }
            }
        }

        if (_request.Pins.TryGetValue(p.Qualified, out var pinned))
        {
            var others = Enumerable.Range(0, p.Universe.Count).Where(i => p.Universe[i].Entry.Version != pinned).ToList();
            if (others.Count > 0)
            {
                Add(new Incompat([new Term(p.Index, VersionSet.OfVersions(p.Size, others))], IncompatKind.Pinned)
                {
                    External = new ConflictCause("pinned", p.Qualified, pinned, null, null, $"{p.Qualified} is pinned to {pinned}"),
                });
            }
        }
    }

    private void Add(Incompat incompat)
    {
        foreach (var term in incompat.Terms)
        {
            if (!_incompatsByPackage.TryGetValue(term.Package, out var list))
            {
                _incompatsByPackage[term.Package] = list = [];
            }

            list.Add(incompat);
        }
    }

    private static VersionSet AllVersions(Package p) => VersionSet.OfVersions(p.Size, Enumerable.Range(0, p.Universe.Count));

    private static VersionSet RangeSet(Package p, VersionRange range) =>
        VersionSet.OfVersions(p.Size, Enumerable.Range(0, p.Universe.Count).Where(i => range.Satisfies(p.Universe[i].Parsed)));

    // ==== main loop ==============================================================================

    public ResolverResult Run()
    {
        // The root must be selected.
        var rootSet = VersionSet.OfVersions(2, [0]);
        Add(new Incompat([new Term(RootIndex, rootSet.Not())], IncompatKind.Root));

        foreach (var root in _request.Roots.OrderBy(r => r.Qualified, StringComparer.Ordinal))
        {
            var p = _packages[GetPackage(root.Qualified)];
            VersionSet required;
            string label;
            if (root.ExactVersion is not null)
            {
                required = VersionSet.OfVersions(p.Size, Enumerable.Range(0, p.Universe.Count).Where(i => p.Universe[i].Entry.Version == root.ExactVersion));
                label = root.ExactVersion;
            }
            else if (root.Range is not null)
            {
                required = RangeSet(p, VersionOps.ValidateRange(root.Range, root.Qualified));
                label = root.Range;
            }
            else
            {
                required = AllVersions(p);
                label = "any version";
            }

            _labels[(p.Index, required)] = label;
            Add(new Incompat([new Term(RootIndex, rootSet), new Term(p.Index, required.Not())], IncompatKind.Requirement)
            {
                External = new ConflictCause("requirement", p.Qualified, root.ExactVersion, null, root.Range,
                    root.ExactVersion is null && root.Range is null ? root.Label : $"{root.Label} ({p.Qualified} {label})"),
            });
        }

        try
        {
            var next = RootIndex;
            for (var steps = 0; ; steps++)
            {
                if (steps > MaxSteps)
                {
                    throw new WinBrewException(WinBrewErrorCode.GeneralFailure, "Dependency resolution did not converge (internal limit).");
                }

                Propagate(next);
                var chosen = ChooseAndDecide();
                if (chosen is null)
                {
                    return new ResolverResult(BuildSolution(), null);
                }

                next = chosen.Value;
            }
        }
        catch (SolveFailure failure)
        {
            return new ResolverResult([], Report(failure.Incompat));
        }
    }

    private sealed class SolveFailure(Incompat incompat) : Exception("version solving failed")
    {
        public Incompat Incompat { get; } = incompat;
    }

    // ---- unit propagation ------------------------------------------------------------------------

    private enum Relation { Satisfied, Contradicted, Inconclusive }

    private VersionSet Accumulated(int package) =>
        _accumulated.TryGetValue(package, out var s) ? s : VersionSet.Full(_packages[package].Size);

    private Relation Relate(Term term)
    {
        var acc = Accumulated(term.Package);
        if (acc.IsSubsetOf(term.Set))
        {
            return Relation.Satisfied;
        }

        return acc.Intersects(term.Set) ? Relation.Inconclusive : Relation.Contradicted;
    }

    private void Propagate(int start)
    {
        var changed = new SortedSet<int> { start };
        while (changed.Count > 0)
        {
            var package = changed.Min;
            changed.Remove(package);
            if (!_incompatsByPackage.TryGetValue(package, out var list))
            {
                continue;
            }

            for (var i = list.Count - 1; i >= 0; i--)
            {
                var incompat = list[i];
                var result = PropagateOne(incompat, out var conflict);
                if (conflict)
                {
                    // Learn, backjump, then the learned incompatibility derives exactly one term.
                    var learned = ResolveConflict(incompat);
                    var forced = PropagateOne(learned, out var again);
                    if (again || forced is null)
                    {
                        throw new InvalidOperationException("PubGrub invariant violated: the learned incompatibility did not derive a term.");
                    }

                    changed.Clear();
                    changed.Add(forced.Value);
                    break;
                }

                if (result is int derived)
                {
                    changed.Add(derived);
                }
            }
        }
    }

    /// <summary>Returns the package that received a derivation, or null; sets <paramref name="conflict"/> when every term is satisfied.</summary>
    private int? PropagateOne(Incompat incompat, out bool conflict)
    {
        conflict = false;
        Term? unsatisfied = null;
        foreach (var term in incompat.Terms)
        {
            switch (Relate(term))
            {
                case Relation.Contradicted:
                    return null;
                case Relation.Inconclusive:
                    if (unsatisfied is not null)
                    {
                        return null;
                    }

                    unsatisfied = term;
                    break;
                default:
                    break;
            }
        }

        if (unsatisfied is not Term unit)
        {
            conflict = true;
            return null;
        }

        Assign(unit.Package, unit.Set.Not(), incompat);
        return unit.Package;
    }

    // ---- conflict resolution (learning) -------------------------------------------------------------

    private static bool IsFailure(Incompat incompat) =>
        incompat.Terms.Count == 0 ||
        (incompat.Terms.Count == 1 && incompat.Terms[0].Package == RootIndex && incompat.Terms[0].Set.IsPositive);

    private Incompat ResolveConflict(Incompat incompat)
    {
        var learnedNew = false;
        while (true)
        {
            if (IsFailure(incompat))
            {
                throw new SolveFailure(incompat);
            }

            Term? mostRecentTerm = null;
            Assignment? mostRecentSatisfier = null;
            VersionSet? difference = null;
            var previousLevel = 0;

            foreach (var term in incompat.Terms)
            {
                var satisfier = Satisfier(term);
                if (mostRecentSatisfier is null)
                {
                    mostRecentTerm = term;
                    mostRecentSatisfier = satisfier;
                }
                else if (mostRecentSatisfier.Index < satisfier.Index)
                {
                    previousLevel = Math.Max(previousLevel, mostRecentSatisfier.Level);
                    mostRecentTerm = term;
                    mostRecentSatisfier = satisfier;
                    difference = null;
                }
                else
                {
                    previousLevel = Math.Max(previousLevel, satisfier.Level);
                }

                if (mostRecentTerm == term)
                {
                    // The part of the satisfier that the term does not cover.
                    var diff = mostRecentSatisfier.Set.And(term.Set.Not());
                    difference = diff.IsEmpty ? null : diff;
                    if (difference is not null)
                    {
                        previousLevel = Math.Max(previousLevel, Satisfier(new Term(term.Package, difference.Not())).Level);
                    }
                }
            }

            if (previousLevel < mostRecentSatisfier!.Level || mostRecentSatisfier.Cause is null)
            {
                Backtrack(previousLevel);
                if (learnedNew)
                {
                    Add(incompat);
                }

                return incompat;
            }

            var cause = mostRecentSatisfier.Cause;
            var terms = incompat.Terms.Where(t => t != mostRecentTerm)
                .Concat(cause.Terms.Where(t => t.Package != mostRecentSatisfier.Package))
                .ToList();
            if (difference is not null)
            {
                terms.Add(new Term(mostRecentSatisfier.Package, difference.Not()));
            }

            incompat = new Incompat(terms, IncompatKind.Derived) { Left = incompat, Right = cause };
            learnedNew = true;
        }
    }

    // ---- partial solution -----------------------------------------------------------------------------

    private void Assign(int package, VersionSet set, Incompat? cause)
    {
        _assignments.Add(new Assignment(package, set, _level, _assignments.Count, cause));
        _accumulated[package] = Accumulated(package).And(set);
    }

    private void Decide(int package, int versionIndex)
    {
        _level++;
        _decisions[package] = versionIndex;
        Assign(package, VersionSet.OfVersions(_packages[package].Size, [versionIndex]), null);
    }

    private Assignment Satisfier(Term term)
    {
        var acc = VersionSet.Full(_packages[term.Package].Size);
        foreach (var a in _assignments)
        {
            if (a.Package != term.Package)
            {
                continue;
            }

            acc = acc.And(a.Set);
            if (acc.IsSubsetOf(term.Set))
            {
                return a;
            }
        }

        throw new InvalidOperationException("PubGrub invariant violated: no satisfier for a satisfied term.");
    }

    private void Backtrack(int level)
    {
        _assignments.RemoveAll(a => a.Level > level);
        _level = level;
        _accumulated.Clear();
        _decisions.Clear();
        foreach (var a in _assignments)
        {
            _accumulated[a.Package] = Accumulated(a.Package).And(a.Set);
            if (a.Cause is null)
            {
                _decisions[a.Package] = a.Set.Versions().Single();
            }
        }
    }

    // ---- decisions (D2 rule 4: which package; rules 1–3: which version) ---------------------------------

    private int? ChooseAndDecide()
    {
        // Active constraints: a positive term in the partial solution and no decision yet.
        var candidates = _accumulated
            .Where(kv => kv.Value.IsPositive && !_decisions.ContainsKey(kv.Key))
            .Select(kv => _packages[kv.Key])
            .OrderBy(p => p.Qualified, StringComparer.Ordinal)
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var package = candidates[0];
        var allowed = Accumulated(package.Index);
        var versions = allowed.Versions().ToList();

        if (versions.Count == 0)
        {
            var reason = package.Unknown ? "is not in the catalog" : "has no version that matches";
            Add(new Incompat([new Term(package.Index, allowed)], IncompatKind.NoVersions)
            {
                External = new ConflictCause("noVersions", package.Qualified, null, null, null,
                    $"{package.Qualified} {reason}{(package.Unknown ? string.Empty : " " + SetText(package, allowed))}"),
            });
            return package.Index;
        }

        var chosen = ChooseVersion(package, versions);
        var conflict = false;
        if (package.Index != RootIndex)
        {
            var (_, entry) = package.Universe[chosen];
            foreach (var dep in entry.Dependencies.OrderBy(d => Qualify(package.Repo, d.Id), StringComparer.Ordinal).ThenBy(d => d.Range, StringComparer.Ordinal))
            {
                var target = _packages[GetPackage(Qualify(package.Repo, dep.Id))];
                var rangeSet = RangeSet(target, VersionOps.ValidateRange(dep.Range, package.Qualified));
                _labels.TryAdd((target.Index, rangeSet), dep.Range);
                var incompat = new Incompat(
                    [new Term(package.Index, VersionSet.OfVersions(package.Size, [chosen])), new Term(target.Index, rangeSet.Not())],
                    IncompatKind.Dependency)
                {
                    External = new ConflictCause("dependency", package.Qualified, entry.Version, target.Qualified, dep.Range,
                        $"{package.Qualified} {entry.Version} depends on {target.Qualified} {dep.Range}" +
                        (target.Unknown ? $", which is not in the catalog of '{target.Repo}'" : string.Empty)),
                };
                Add(incompat);
                conflict |= incompat.Terms.All(t => t.Package == package.Index || Relate(t) == Relation.Satisfied);
            }
        }

        if (!conflict)
        {
            Decide(package.Index, chosen);
        }

        return package.Index;
    }

    private int ChooseVersion(Package package, List<int> allowed)
    {
        if (package.Index == RootIndex)
        {
            return 0;
        }

        int? installed = null;
        if (_request.Installed.TryGetValue(package.Qualified, out var installedVersion))
        {
            var idx = allowed.FindIndex(i => package.Universe[i].Entry.Version == installedVersion);
            installed = idx >= 0 ? allowed[idx] : null;
        }

        var upgradeThis = _request.Mode == ResolveMode.Upgrade &&
                          !_request.Pins.ContainsKey(package.Qualified) &&
                          (_request.UpgradeTargets.Count == 0 || _request.UpgradeTargets.Contains(package.Qualified));

        return !upgradeThis && installed is int keep ? keep : allowed[^1];
    }

    // ==== solution ===================================================================================

    private List<ResolvedPackage> BuildSolution()
    {
        var selected = new Dictionary<string, ResolvedPackage>(StringComparer.Ordinal);
        foreach (var (index, version) in _decisions.Where(d => d.Key != RootIndex))
        {
            var p = _packages[index];
            var entry = p.Universe[version].Entry;
            var arch = HostArchitectureRules.Select(_request.Host, entry.Architectures)!;
            var deps = entry.Dependencies.Select(d => Qualify(p.Repo, d.Id)).Distinct(StringComparer.Ordinal).OrderBy(d => d, StringComparer.Ordinal).ToList();
            selected[p.Qualified] = new ResolvedPackage(p.Repo, p.Id, entry.Version, arch, deps);
        }

        return GraphOrder.InstallOrder(selected);
    }

    // ==== explanations (D2: repository-qualified ids, original range strings) ===========================

    private ConflictReport Report(Incompat failure)
    {
        var lines = new List<string>();
        var numbers = new Dictionary<Incompat, int>(ReferenceEqualityComparer.Instance);
        var causes = new List<ConflictCause>();
        string? missingRepository = null;

        void Collect(Incompat i)
        {
            if (i.Kind == IncompatKind.Derived)
            {
                Collect(i.Left!);
                Collect(i.Right!);
                return;
            }

            if (i.External is { } cause && !causes.Contains(cause))
            {
                causes.Add(cause);
            }

            missingRepository ??= i.MissingRepository;
        }

        string Ref(Incompat i) => i.Kind == IncompatKind.Derived ? $"{Describe(i)} ({numbers[i]})" : Describe(i);

        void Visit(Incompat i)
        {
            if (i.Kind != IncompatKind.Derived || numbers.ContainsKey(i))
            {
                return;
            }

            Visit(i.Left!);
            Visit(i.Right!);
            var text = IsFailure(i)
                ? $"Because {Ref(i.Left!)} and {Ref(i.Right!)}, version solving failed."
                : $"Because {Ref(i.Left!)} and {Ref(i.Right!)}, {Describe(i)}.";
            lines.Add($"({lines.Count + 1}) {text}");
            numbers[i] = lines.Count;
        }

        Collect(failure);
        if (failure.Kind == IncompatKind.Derived)
        {
            Visit(failure);
        }
        else
        {
            lines.Add($"(1) Because {Describe(failure)}, version solving failed.");
        }

        return new ConflictReport(lines, causes, missingRepository);
    }

    private string Describe(Incompat i)
    {
        if (i.External is { } external)
        {
            return external.Text;
        }

        var terms = i.Terms.Where(t => t.Package != RootIndex).ToList();

        if (terms.Count == 0)
        {
            return "version solving failed";
        }

        if (terms.Count == 1)
        {
            var t = terms[0];
            return t.Set.IsPositive ? $"{TermText(t)} is forbidden" : $"{PositiveText(t)} is required";
        }

        var positives = terms.Where(t => t.Set.IsPositive).ToList();
        var negatives = terms.Where(t => !t.Set.IsPositive).ToList();
        if (positives.Count == 1 && negatives.Count == 1)
        {
            return $"{TermText(positives[0])} requires {PositiveText(negatives[0])}";
        }

        if (negatives.Count == 0)
        {
            return string.Join(" is incompatible with ", positives.Select(TermText));
        }

        return "one of " + string.Join(", ", terms.Select(TermText)) + " must be false";
    }

    /// <summary>A positive term as "pkg versions".</summary>
    private string TermText(Term t)
    {
        var p = _packages[t.Package];
        return t.Set.IsPositive ? Named(p, t.Set) : $"not {PositiveText(t)}";
    }

    private string Named(Package p, VersionSet positive)
    {
        var text = SetText(p, positive);
        return text == "any version" ? p.Qualified : $"{p.Qualified} {text}";
    }

    /// <summary>The positive reading of a negative term (what it requires).</summary>
    private string PositiveText(Term t)
    {
        var p = _packages[t.Package];
        return Named(p, t.Set.Not());
    }

    private string SetText(Package p, VersionSet positive)
    {
        if (_labels.TryGetValue((p.Index, positive), out var label))
        {
            return label;
        }

        var versions = positive.Versions().Select(i => p.Universe[i].Entry.Version).ToList();
        if (versions.Count == p.Universe.Count && versions.Count > 0)
        {
            return "any version";
        }

        return versions.Count switch
        {
            0 => "(no versions)",
            1 => versions[0],
            _ => "{" + string.Join(", ", versions) + "}",
        };
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"Solver({_packages.Count} packages)");
}
