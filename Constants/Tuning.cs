// SPDX-FileCopyrightText: 2026 Dana Buehre
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Jellyfin.Plugin.Gelo.Constants;

/// <summary>
/// Algorithm constants for the Gelo recommendations engine.
/// </summary>
/// <remarks>
/// Two tiers: (1) a semantic content tier built on all-MiniLM-L6-v2 embeddings with a
/// relevance-weighted user centroid, and (2) an optional per-user trained logistic ranker
/// (<c>Recs.Tier2</c>) that re-orders shelves by predicted engagement probability. The values
/// here are the engine defaults; the matching properties on <c>GeloPluginConfiguration</c>
/// override them on the hot path with these as fallbacks.
/// </remarks>
internal static class Tuning
{
    // ───────────────────────── Tier 1: semantic content (MiniLM) ─────────────────────────
    // all-MiniLM-L6-v2 produces 384-dim vectors. This is the item-content backbone: a
    // relevance-weighted mean of these vectors is the user's taste centroid, and cosine
    // similarity against it drives every shelf and More-Like-This result.
    public const int EmbeddingDim = 384;

    /// <summary>all-MiniLM-L6-v2 supports 512; metadata blocks are short, so cap for speed.</summary>
    public const int MaxSequenceLength = 256;

    /// <summary>Top-N cast members (by sort order) included in the embedding text block.</summary>
    public const int MaxCastInBlock = 5;

    /// <summary>
    /// Metadata → embedding text block template, composed by <c>MetadataBlockBuilder</c>.
    /// The overview dominates: it is what lets the embedding capture "plot vibes" semantic
    /// similarity (Interstellar ↔ Arrival) that pure structured metadata misses.
    /// </summary>
    public const string MetadataBlockTemplate =
        "{title} ({year}). Genres: {genres}. Directed by {directors}. Starring {cast}. {overview}";

    // ───────────────────────── Temporal decay + relevance ─────────────────────────
    // 90-day half-life exponential decay with a 0.01 floor. Items watched yesterday ≈ 0.99,
    // 30d ≈ 0.79, 90d ≈ 0.50, 1y ≈ 0.06.
    public const double DecayHalfLifeDays = 90.0;
    public const double TemporalDecayFloor = 0.01;

    /// <summary>
    /// relevanceScore = (WeightCompletion*completion + WeightPlayCount*playCountNorm +
    /// WeightFavorite*isFavorite + WeightDwell*dwellNorm) * temporalDecay. wasRequested is
    /// deliberately excluded from the score — requested/watchlist items are surfaced via their
    /// own shelves rather than by inflating relevance, which would dilute the profile toward the
    /// catalog centroid.
    /// </summary>
    public const double WeightCompletion = 0.40;
    public const double WeightPlayCount = 0.30;
    public const double WeightFavorite = 0.20;
    public const double WeightDwell = 0.10;

    /// <summary>
    /// An interaction counts as "engaged" (admitted into the user profile) iff
    /// completionPct &gt; <see cref="EngagedCompletionThreshold"/> OR playCountNorm &gt; 0 OR favorite.
    /// This gate keeps requested-but-unwatched noise out of the taste centroid.
    /// </summary>
    public const double EngagedCompletionThreshold = 0.05;

    /// <summary>
    /// Phase-gate threshold (distinct from <see cref="EngagedCompletionThreshold"/>). An item counts
    /// as "watched" for phase selection iff completionPct &gt; 0.1; the watched COUNT then selects
    /// ruleBased (0) / contentSimilarity (1-9) / fullModel (10+).
    /// </summary>
    public const double WatchedCompletionThreshold = 0.1;

    // ───────────────────────── Shelves ─────────────────────────
    /// <summary>Mood shelf runtime bands (minutes).</summary>
    public const int QuickFixMaxMinutes = 30;
    public const int DeepDiveMinMinutes = 90;

    public const int DefaultItemsPerShelf = 12;
    public const int DefaultSimilarLimit = 12;

    /// <summary>Minimum items for a shelf to be worth showing; thinner shelves are dropped so the home
    /// isn't dotted with 1–2 item rails.</summary>
    public const int MinItemsPerShelf = 3;

    /// <summary>Minimum engaged items before the trained ranker may run.</summary>
    public const int MinItemsForTraining = 10;

    /// <summary>Phase thresholds: 0 watched = ruleBased, 1-9 = contentSimilarity, 10+ = fullModel.</summary>
    public const int ContentSimilarityMinWatched = 1;
    public const int FullModelMinWatched = 10;
}
