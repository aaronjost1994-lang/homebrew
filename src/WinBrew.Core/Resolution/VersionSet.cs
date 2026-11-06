using System.Numerics;

namespace WinBrew.Core.Resolution;

/// <summary>
/// A set of package STATES over a finite universe: the package's catalog
/// versions (indices 0..n-1, ascending) plus one extra element n = "not
/// selected". Because every package's universe is finite, PubGrub's term
/// algebra becomes exact bit arithmetic:
/// a positive term "p ∈ S" is S (without the absent bit); a negative term
/// "p ∉ S" is the complement of S, absent bit included. Intersection is AND,
/// negation is NOT, "satisfies" is subset.
/// </summary>
internal sealed class VersionSet : IEquatable<VersionSet>
{
    private readonly ulong[] _words;

    private VersionSet(int size, ulong[] words)
    {
        Size = size;
        _words = words;
    }

    /// <summary>Number of elements in the universe (versions + 1).</summary>
    public int Size { get; }

    public int Absent => Size - 1;

    public static VersionSet Empty(int size) => new(size, new ulong[(size + 63) / 64]);

    public static VersionSet Full(int size)
    {
        var set = Empty(size);
        for (var i = 0; i < size; i++)
        {
            set._words[i / 64] |= 1UL << (i % 64);
        }

        return set;
    }

    /// <summary>The positive term "p is one of these versions".</summary>
    public static VersionSet OfVersions(int size, IEnumerable<int> versions)
    {
        var set = Empty(size);
        foreach (var v in versions)
        {
            set._words[v / 64] |= 1UL << (v % 64);
        }

        return set;
    }

    public bool Contains(int index) => (_words[index / 64] & (1UL << (index % 64))) != 0;

    /// <summary>A positive term never allows "not selected".</summary>
    public bool IsPositive => !Contains(Absent);

    public bool IsEmpty => _words.All(w => w == 0);

    public bool IsFull => Equals(Full(Size));

    public int Count => _words.Sum(w => BitOperations.PopCount(w));

    public VersionSet And(VersionSet other) => Combine(other, (a, b) => a & b);

    public VersionSet Or(VersionSet other) => Combine(other, (a, b) => a | b);

    public VersionSet Not()
    {
        var full = Full(Size);
        var words = new ulong[_words.Length];
        for (var i = 0; i < words.Length; i++)
        {
            words[i] = ~_words[i] & full._words[i];
        }

        return new VersionSet(Size, words);
    }

    public bool IsSubsetOf(VersionSet other) => And(other.Not()).IsEmpty;

    public bool Intersects(VersionSet other) => !And(other).IsEmpty;

    /// <summary>The version indices (absent excluded), ascending.</summary>
    public IEnumerable<int> Versions()
    {
        for (var i = 0; i < Absent; i++)
        {
            if (Contains(i))
            {
                yield return i;
            }
        }
    }

    private VersionSet Combine(VersionSet other, Func<ulong, ulong, ulong> op)
    {
        if (other.Size != Size)
        {
            throw new InvalidOperationException("VersionSet universes differ.");
        }

        var words = new ulong[_words.Length];
        for (var i = 0; i < words.Length; i++)
        {
            words[i] = op(_words[i], other._words[i]);
        }

        return new VersionSet(Size, words);
    }

    public bool Equals(VersionSet? other) => other is not null && other.Size == Size && _words.AsSpan().SequenceEqual(other._words);

    public override bool Equals(object? obj) => Equals(obj as VersionSet);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Size);
        foreach (var w in _words)
        {
            hash.Add(w);
        }

        return hash.ToHashCode();
    }
}
