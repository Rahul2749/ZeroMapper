using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace ZeroMapper.Generator;

internal sealed record MapperModel(
    string Namespace, string Name, bool IsStatic, string FullName, EquatableArray<MethodModel> Methods);

internal sealed record MethodModel(
    string Modifiers, string ReturnType, string Name, bool Update, string SourceName, string DestinationName,
    EquatableArray<ParameterModel> Parameters, EquatableArray<string> Bindings);

internal readonly record struct ParameterModel(string Type, string Name);

// ImmutableArray itself compares the backing array by identity. Emission needs element equality.
internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>
{
    public EquatableArray(ImmutableArray<T> items) => Items = items;

    public ImmutableArray<T> Items { get; }

    public bool Equals(EquatableArray<T> other)
    {
        if (Items.Equals(other.Items)) return true;
        if (Items.IsDefault || other.Items.IsDefault || Items.Length != other.Items.Length) return false;
        var comparer = EqualityComparer<T>.Default;
        for (int i = 0; i < Items.Length; i++)
            if (!comparer.Equals(Items[i], other.Items[i])) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        if (Items.IsDefault) return 0;
        var comparer = EqualityComparer<T>.Default;
        int hash = 17;
        foreach (var item in Items)
            hash = unchecked(hash * 31 + comparer.GetHashCode(item!));
        return hash;
    }
}
