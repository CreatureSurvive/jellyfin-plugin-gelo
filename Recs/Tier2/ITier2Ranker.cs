// SPDX-FileCopyrightText: 2026 Dana Buehre
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Gelo.Persistence;

namespace Jellyfin.Plugin.Gelo.Recs.Tier2;

/// <summary>
/// Phase-2 per-user trained re-ranker. When a model is available for a user, RerankPipeline orders a
/// shelf's candidate set by the model's predicted engagement probability (plus the existing more-like
/// + freshness lifts) instead of the heuristic centroid-affinity score. Implementations must be safe
/// to call from the hot read path (Shelves) and must degrade gracefully (return null / false) when no
/// trained model exists, so the engine silently falls back to the heuristic.
/// </summary>
public interface ITier2Ranker
{
    /// <summary>True iff a trained model is loaded (or loadable from disk) for this user.</summary>
    bool IsAvailable(Guid userId);

    /// <summary>
    /// Predict P(engage) for each item. <paramref name="affinity"/> is the Phase-1 heuristic
    /// centroid-affinity per item (fed to the model as one feature). Returns null if no model is
    /// available; the caller then ranks by the heuristic alone.
    /// </summary>
    float[]? Score(Guid userId, IReadOnlyList<IndexedItem> items, Dictionary<Guid, float> affinity);

    /// <summary>
    /// (Re)train and persist the model for this user from their current interactions, then warm the
    /// cache. Called by the RetrainTask when <c>EnableTier2Ranker</c> is on. No-ops (and drops any stale
    /// model) when there is too little engagement to train.
    /// </summary>
    void TrainForUser(Guid userId);
}
