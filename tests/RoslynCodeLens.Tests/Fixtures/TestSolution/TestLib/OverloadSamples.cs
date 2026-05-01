using System.Collections.Generic;
using System.Linq;

namespace TestLib;

public class OverloadSamples
{
    /// <summary>Add two integers.</summary>
    public int Add(int a, int b) => a + b;

    /// <summary>Add a sequence of integers.</summary>
    public int Add(params int[] values) => values.Sum();

    /// <summary>Add with a comparer.</summary>
    public TKey Add<TKey>(TKey a, TKey b, IComparer<TKey> comparer) => a;

    /// <summary>Echo a string with optional repeat count.</summary>
    public string Echo(string s, int times = 1)
        => string.Concat(Enumerable.Repeat(s, times));

    public static OverloadSamples FromString(string s) => new();
    public static OverloadSamples FromString(string s, int multiplier) => new();

    // Exercises every RefKind so the modifier-rendering switch in BuildParameter is covered.
    public bool TryParse(string s, out int value) { value = 0; return false; }
    public void Increment(ref int value) => value++;
    public int InspectIn(in int value) => value;
    public int InspectRefReadonly(ref readonly int value) => value;
}

public static class OverloadExtensions
{
    public static int Doubled(this int x) => x * 2;
    public static int Doubled(this int x, int factor) => x * factor;
}
