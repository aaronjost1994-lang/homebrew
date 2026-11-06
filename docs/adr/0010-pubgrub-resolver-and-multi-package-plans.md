# ADR-0010: PubGrub resolver, multi-package plans, pins, cleanup (D2, A10, D8)

**Status:** accepted (Phase C, 2026-11-06)

## Decisions

1. **Resolver = PubGrub in `WinBrew.Core/Resolution`**, pure by
   construction: Core references no WinBrew I/O assembly and
   `PubGrubResolver.Resolve` takes only a `ResolverRequest` (the in-memory
   catalog snapshot, the host architecture, roots, installed versions, pins,
   mode). The Application layer builds the snapshot from SQLite BEFORE
   resolution by walking every catalog package reachable from the roots.
2. **Finite version universes.** Each package's universe is its catalog
   versions (prereleases only when D2 rule 6 allows them), so a term is a
   bitset over those versions plus one "not selected" element: positive
   "p ∈ S" = S, negative "p ∉ S" = ¬S ∪ {absent}. Intersection, negation and
   "satisfies" become exact bit operations. Terms that are always true are
   dropped from incompatibilities (not (true ∧ X) ≡ not X).
3. **Facts as incompatibilities.** No artifact for the host (A10 order) →
   a unary incompatibility per version; a fully qualified dependency on an
   unconfigured repository (A9) → unary incompatibility carrying the
   repository name (the CLI then exits **3** naming it); pins (D2 rule 1) →
   "any other version is forbidden". Explanations therefore name the real
   reason.
4. **Heuristic (D2 rule 4–5).** Unit propagation runs to completion; the
   next package is the undecided package with a positive term whose
   `<repo>/<id>` is ordinally smallest. Version: pinned → the pin; install
   → the installed version if allowed, else the highest; upgrade → the
   highest for targets (all unpinned packages when no target is given).
   Dependencies are added in ordinal order; every input list is sorted, so
   100 randomized orderings give the same plan and the same explanation
   (tested).
5. **Roots.** Install resolves the requested package PLUS every explicitly
   installed package (any version), so an install never silently breaks
   what is installed; if the only solution upgrades an installed package,
   the plan says so (`upgrade` item). Installed packages that left the
   catalog are reported and left alone.
6. **Explanations** follow PubGrub's derivation tree: numbered lines
   "Because X and Y, Z." naming repository-qualified ids and the ORIGINAL
   range strings; `--json` carries `error.details.conflict` with the
   explanation lines and the structured external causes.
7. **Cycles** are refused after resolution (exit 6) with the path
   `a → b → a`; the install order is a deterministic topological order,
   dependencies first.
8. **One operation per plan.** Every changing package of a plan is
   prepared (artifact, extract, validate) and placed under ONE journaled
   operation with ONE `StateCommitted` transaction (installed rows, shims,
   dependency edges); compensation therefore restores every previous
   version and shim if anything fails before the commit. Manifests for the
   whole plan are fetched and checked against the catalog (exit 5) BEFORE
   the operation starts, and the verified manifests are cached only after
   all of them pass — a mismatch changes nothing.
9. **Edges and explicitness.** A package's dependency edges are replaced
   with its resolved dependencies at commit; `explicit` is only ever
   promoted (installing a dependency by name makes it explicit).
   `autoremove` removes non-explicit, unpinned packages no installed
   package references, repeatedly (dependents first).
10. **Cleanup** trims `cache\artifacts\` oldest-first to `cache.maxBytes`.
    Protected: the artifact of every installed version (active; pins are a
    subset) and of every retained inactive version directory, identified
    from their verified cached manifests. If any protected artifact cannot
    be identified, cleanup removes NOTHING and says why.

## Consequences

- Lockfile recording of the re-selected architecture is Phase D (bundles);
  installed state records it now.
- A plan that changes several packages is all-or-nothing up to
  `StateCommitted`; afterwards only roll-forward cleanup remains.
