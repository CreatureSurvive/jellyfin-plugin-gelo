// SPDX-FileCopyrightText: 2026 Dana Buehre
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Gelo.Persistence;
using System.Numerics.Tensors;

namespace Jellyfin.Plugin.Gelo.Recs;

/// <summary>
/// SIMD similarity. Stored vectors are L2-normalised at embed time, so cosine == dot product.
/// Uses <see cref="TensorPrimitives"/> (.NET 9 hardware-accelerated) for the inner loops.
/// </summary>
internal static class VectorMath
{
    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
        => a.Length == b.Length ? TensorPrimitives.Dot(a, b) : 0f;

    public static double Norm(ReadOnlySpan<float> v) => Math.Sqrt(TensorPrimitives.SumOfSquares(v));

    /// <summary>Score every cached item against the query, apply an optional filter, return top-k.</summary>
    public static List<(IndexedItem Item, float Score)> TopKSimilar(
        ReadOnlyMemory<float> query,
        IReadOnlyList<IndexedItem> items,
        Guid? excludeId,
        int k,
        Func<IndexedItem, bool>? predicate = null)
    {
        var q = query.Span;
        var scored = new List<(IndexedItem Item, float Score)>(items.Count);
        foreach (var item in items)
        {
            if (excludeId.HasValue && item.Id == excludeId.Value)
            {
                continue;
            }

            if (predicate is not null && !predicate(item))
            {
                continue;
            }

            if (item.Vector.Length != q.Length)
            {
                continue;
            }

            scored.Add((item, TensorPrimitives.Dot(q, item.Vector)));
        }

        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        var take = Math.Min(k, scored.Count);
        if (take < scored.Count)
        {
            scored.RemoveRange(take, scored.Count - take);
        }

        return scored;
    }

    /// <summary>Normalise in place (used for user centroids that are not pre-normalised).</summary>
    public static void NormalizeInPlace(Span<float> v)
    {
        var n = Norm(v);
        if (n <= 0)
        {
            return;
        }

        TensorPrimitives.Multiply(v, (float)(1.0 / n), v);
    }
}
