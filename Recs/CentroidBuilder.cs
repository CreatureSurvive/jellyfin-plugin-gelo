// SPDX-FileCopyrightText: 2026 Dana Buehre
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Linq;
using System.Numerics.Tensors;
using Jellyfin.Plugin.Gelo.Constants;
using Jellyfin.Plugin.Gelo.Persistence;

namespace Jellyfin.Plugin.Gelo.Recs;

/// <summary>
/// Builds a user's taste centroid: a relevance-weighted, L2-normalised mean of the embeddings of
/// items they've <i>engaged</i> with. Only <c>IsEngaged</c> items contribute, each weighted by its
/// (per-user-normalised) relevance score, so recent, frequent, and favorite plays dominate the centroid.
/// </summary>
public sealed class CentroidBuilder
{
    private readonly VectorStore _store;

    public CentroidBuilder(VectorStore store)
    {
        _store = store;
    }

    public UserCentroid Build(Guid userId)
    {
        var dim = Plugin.Instance?.Configuration.EmbeddingDim ?? Tuning.EmbeddingDim;
        var sum = new float[dim];
        var interactions = _store.GetInteractionsForUser(userId);
        if (interactions.Count == 0)
        {
            return new UserCentroid { UserId = userId, Vector = sum, ItemCount = 0, UpdatedAtTicks = DateTime.UtcNow.Ticks };
        }

        // playCountNorm is normalized against this user's own peak play count (per-user maxPlayCount).
        var maxPlayCount = interactions.Max(i => i.PlayCount);

        var totalWeight = 0.0;
        var count = 0;
        foreach (var ix in interactions)
        {
            var item = _store.GetItem(ix.ItemId);
            if (item is null || item.Vector.Length != dim)
            {
                continue;
            }

            var rel = Relevance.Recompute(ix, maxPlayCount);
            // Admission gate is isEngaged (not relevanceScore > 0): a fully-watched film whose
            // decay has driven relevance near 0 still counts once via playCount/favorite.
            var pcn = Relevance.PlayCountNorm(ix.PlayCount, maxPlayCount);
            if (!Relevance.IsEngaged(ix.CompletionPct, pcn, ix.IsFavorite) || rel <= 0)
            {
                continue;
            }

            var w = (float)rel;
            TensorPrimitives.AddMultiply(sum, item.Vector, w, sum);
            totalWeight += rel;
            count++;
        }

        if (totalWeight > 0)
        {
            // weighted MEAN (divide the weighted sum by total weight), then L2-normalise.
            TensorPrimitives.Multiply(sum, (float)(1.0 / totalWeight), sum);
        }

        VectorMath.NormalizeInPlace(sum);
        return new UserCentroid
        {
            UserId = userId,
            Vector = sum,
            ItemCount = count,
            UpdatedAtTicks = DateTime.UtcNow.Ticks
        };
    }
}
