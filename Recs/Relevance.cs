// SPDX-FileCopyrightText: 2026 Dana Buehre
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using Jellyfin.Plugin.Gelo.Constants;
using Jellyfin.Plugin.Gelo.Persistence;

namespace Jellyfin.Plugin.Gelo.Recs;

/// <summary>
/// Time-decay and relevance scoring for a single user interaction.
/// </summary>
/// <remarks>
/// relevanceScore = (0.40·completion + 0.30·playCountNorm + 0.20·isFavorite + 0.10·dwellNorm) · temporalDecay.
/// <c>wasRequested</c> is deliberately excluded — requests are surfaced via dedicated shelves, not by
/// inflating relevance. <c>playCountNorm</c> is normalized against the USER'S max play count, not a global cap.
/// </remarks>
internal static class Relevance
{
    /// <summary>
    /// Exponential temporal decay with a 90-day half-life (configurable) and a 0.01 floor.
    /// No-date fallbacks: an item with completion but no last-played date decays to 0.5; an item
    /// with neither defaults to 1.0 (treated as untouched).
    /// </summary>
    public static double TemporalDecay(DateTime? lastPlayed, double halfLifeDays, double completionIfNoDate = 0.0)
    {
        if (lastPlayed.HasValue)
        {
            var days = (DateTime.UtcNow - lastPlayed.Value).TotalDays;
            if (days <= 0)
            {
                return 1.0;
            }

            return Math.Max(Math.Pow(0.5, days / Math.Max(1.0, halfLifeDays)), Tuning.TemporalDecayFloor);
        }

        // No last-played date: fall back on engagement state rather than zeroing the item out.
        return completionIfNoDate > 0 ? 0.5 : 1.0;
    }

    /// <summary>
    /// Per-user log-scaled play-count normalization: <c>log(1+count)/log(1+max(1,maxPlayCount))</c>.
    /// <paramref name="maxPlayCount"/> is the user's highest play count across their history, so a
    /// serial rewatcher's counts are normalized against their own peak rather than a global cap.
    /// </summary>
    public static double PlayCountNorm(int playCount, int maxPlayCount)
        => playCount <= 0 ? 0.0 : Math.Min(1.0, Math.Log(1.0 + playCount) / Math.Log(1.0 + Math.Max(1, maxPlayCount)));

    /// <summary>An item is "engaged" (admitted into the user profile) iff it clears the engagement gate.</summary>
    public static bool IsEngaged(double completionPct, double playCountNorm, bool isFavorite)
        => completionPct > EffectiveThreshold(Plugin.Instance?.Configuration?.EngagedCompletionThreshold, Tuning.EngagedCompletionThreshold)
            || playCountNorm > 0 || isFavorite;

    /// <summary>Compute relevance from raw engagement signals + precomputed decay.</summary>
    public static double Score(
        double completion,
        double playCountNorm,
        bool isFavorite,
        double dwellNorm,
        double temporalDecay)
    {
        var c = Plugin.Instance?.Configuration;
        var wCompletion = EffectiveWeight(c?.WeightCompletion, Tuning.WeightCompletion);
        var wPlayCount = EffectiveWeight(c?.WeightPlayCount, Tuning.WeightPlayCount);
        var wFavorite = EffectiveWeight(c?.WeightFavorite, Tuning.WeightFavorite);
        var wDwell = EffectiveWeight(c?.WeightDwell, Tuning.WeightDwell);
        return (wCompletion * completion
            + wPlayCount * playCountNorm
            + wFavorite * (isFavorite ? 1 : 0)
            + wDwell * dwellNorm) * temporalDecay;
    }

    private static double EffectiveWeight(double? configured, double fallback)
        => configured.HasValue && configured.Value >= 0 ? configured.Value : fallback;

    private static double EffectiveThreshold(double? configured, double fallback)
        => configured.HasValue && configured.Value >= 0 && configured.Value <= 1 ? configured.Value : fallback;

    /// <summary>
    /// Recompute a fully-fidelity relevance score for a stored interaction, given the user's max
    /// play count (so playCountNorm is per-user). Used on the read path (centroids, shelves) where
    /// the whole interaction set — and thus the true max — is known. Decay is recomputed from
    /// <paramref name="r"/>'s last-played date + the CURRENT configured half-life (not the value
    /// stored at upsert time), so a half-life config change takes effect for all history instantly.
    /// </summary>
    public static double Recompute(InteractionRecord r, int maxPlayCount)
    {
        var pcn = PlayCountNorm(r.PlayCount, maxPlayCount);
        var lastPlayed = r.LastPlayedTicks.HasValue
            ? new DateTime(r.LastPlayedTicks.Value, DateTimeKind.Utc)
            : (DateTime?)null;
        var decay = TemporalDecay(
            lastPlayed,
            Plugin.Instance?.Configuration.DecayHalfLifeDays ?? Tuning.DecayHalfLifeDays,
            r.CompletionPct);
        return Score(r.CompletionPct, pcn, r.IsFavorite, r.DwellNorm, decay);
    }
}
