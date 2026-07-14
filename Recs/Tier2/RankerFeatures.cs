// SPDX-FileCopyrightText: 2026 Dana Buehre
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using Jellyfin.Plugin.Gelo.Constants;
using Jellyfin.Plugin.Gelo.Persistence;

namespace Jellyfin.Plugin.Gelo.Recs.Tier2;

/// <summary>
/// Builds the ranker's per-item feature vector: the L2-normalised MiniLM embedding (the dominant
/// content signal) followed by a small block of scalar metadata, PLUS the Tier-1 heuristic affinity
/// as one feature, so the logistic model can learn how much to trust/adjust the heuristic score
/// rather than re-derive it blindly. Shared verbatim by training and inference so the layout is identical.
/// </summary>
internal static class RankerFeatures
{
    public const int EmbeddingSlots = Tuning.EmbeddingDim;        // 384
    public const int ScalarCount = 8;                             // affinity, year, rating, runtime, recency, typeMovie, typeSeries, typeOther
    public const int FeatureDim = EmbeddingSlots + ScalarCount;   // 392

    public static float[] Build(IndexedItem it, float affinity)
    {
        var v = new float[FeatureDim];
        var src = it.Vector;
        var n = Math.Min(src.Length, EmbeddingSlots);
        for (var i = 0; i < n; i++)
        {
            v[i] = src[i];
        }

        // Pad remaining embedding slots with 0 if an item's vector is shorter than expected.
        v[EmbeddingSlots + 0] = affinity;
        v[EmbeddingSlots + 1] = NormYear(it.Year);
        v[EmbeddingSlots + 2] = NormRating(it.CommunityRating);
        v[EmbeddingSlots + 3] = NormRuntime(it.RunTimeTicks);
        v[EmbeddingSlots + 4] = NormRecency(it.DateCreatedTicks);
        v[EmbeddingSlots + 5] = IsMovie(it.ItemType) ? 1f : 0f;
        v[EmbeddingSlots + 6] = IsSeries(it.ItemType) ? 1f : 0f;
        v[EmbeddingSlots + 7] = (!IsMovie(it.ItemType) && !IsSeries(it.ItemType)) ? 1f : 0f; // Episode/Season/Other
        return v;
    }

    private static float NormYear(int? y) => y.HasValue ? Clamp((y.Value - 1950f) / 80f) : 0.5f;

    private static float NormRating(double? r) => r.HasValue ? Clamp((float)(r.Value / 10.0)) : 0.5f;

    private static float NormRuntime(long? ticks)
        => ticks.HasValue ? Clamp(ticks.Value / (TimeSpan.TicksPerSecond * 60f * 240f)) : 0.5f;

    private static float NormRecency(long? ticks)
    {
        if (!ticks.HasValue)
        {
            return 0.5f;
        }

        var ageDays = (DateTime.UtcNow.Ticks - ticks.Value) / (double)TimeSpan.TicksPerDay;
        return ageDays <= 0 ? 1f : Clamp(1f - (float)(ageDays / 365.0));
    }

    private static bool IsMovie(string t) => string.Equals(t, "Movie", StringComparison.OrdinalIgnoreCase);

    private static bool IsSeries(string t) => string.Equals(t, "Series", StringComparison.OrdinalIgnoreCase);

    private static float Clamp(float x) => x < 0 ? 0 : (x > 1 ? 1 : x);
}
