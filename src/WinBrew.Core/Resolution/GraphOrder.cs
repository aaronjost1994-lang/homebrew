namespace WinBrew.Core.Resolution;

/// <summary>
/// Install order and cycle detection over a resolved package set (A10:
/// "cycle detection with a reported path"). Dependencies come first; ties
/// are broken by ordinal &lt;repo&gt;/&lt;id&gt; so the order is deterministic.
/// A cycle is refused with exit 6 naming the path (a → b → a).
/// </summary>
public static class GraphOrder
{
    public static List<ResolvedPackage> InstallOrder(IReadOnlyDictionary<string, ResolvedPackage> selected)
    {
        var order = new List<ResolvedPackage>();
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 1 = visiting, 2 = done
        var stack = new List<string>();

        void Visit(string name)
        {
            if (state.TryGetValue(name, out var s))
            {
                if (s == 1)
                {
                    var start = stack.IndexOf(name);
                    var cycle = stack.Skip(start).Append(name).ToList();
                    throw new WinBrewException(WinBrewErrorCode.ConflictOrRefused,
                        $"Dependency cycle: {string.Join(" → ", cycle)} (A10).",
                        new Dictionary<string, object?> { ["cycle"] = cycle });
                }

                return;
            }

            state[name] = 1;
            stack.Add(name);
            foreach (var dep in selected[name].Dependencies.Where(selected.ContainsKey).OrderBy(d => d, StringComparer.Ordinal))
            {
                Visit(dep);
            }

            stack.RemoveAt(stack.Count - 1);
            state[name] = 2;
            order.Add(selected[name]);
        }

        foreach (var name in selected.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            Visit(name);
        }

        return order;
    }
}
