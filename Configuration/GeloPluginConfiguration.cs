// SPDX-FileCopyrightText: 2026 Dana Buehre
// SPDX-License-Identifier: GPL-3.0-or-later

using Jellyfin.Plugin.Gelo.Constants;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Gelo.Configuration;

/// <summary>
/// User-facing tunables, surfaced in Dashboard → Plugins → Gelo Recommendations. Every value here
/// overrides the corresponding <see cref="Tuning"/> default on the hot path, with the const as the
/// fallback, so an untouched install runs on the engine defaults.
/// </summary>
public class GeloPluginConfiguration : BasePluginConfiguration
{
    // ───────────────────────── Master switches ─────────────────────────
    /// <summary>Kill switch for the whole plugin (no APIs return recommendations when off).</summary>
    public bool EnablePlugin { get; set; } = true;

    /// <summary>
    /// Master switch for all native/background work (SQLite open, ONNX inference, event hooks).
    /// Safe-mode bring-up is complete, so this defaults to <c>true</c>. Flip to <c>false</c> to park
    /// the plugin in a pure-managed state without touching a native lib.
    /// </summary>
    public bool EnableIndexing { get; set; } = true;

    /// <summary>Trained ML.NET ranker: re-order shelves by learned engagement probability. Off by default — enable after enough watch history exists, then run "Trigger model retrain".</summary>
    public bool EnableTier2Ranker { get; set; } = false;

    // ───────────────────────── Relevance scoring ─────────────────────────
    /// <summary>relevanceScore = (Wc·completion + Wp·playCountNorm + Wf·isFavorite + Wd·dwellNorm)·decay.</summary>
    public double WeightCompletion { get; set; } = Tuning.WeightCompletion;

    public double WeightPlayCount { get; set; } = Tuning.WeightPlayCount;

    public double WeightFavorite { get; set; } = Tuning.WeightFavorite;

    public double WeightDwell { get; set; } = Tuning.WeightDwell;

    /// <summary>90-day exponential half-life for temporal decay (floor 0.01).</summary>
    public double DecayHalfLifeDays { get; set; } = Tuning.DecayHalfLifeDays;

    /// <summary>completionPct above which an interaction is "engaged" (admitted to the user profile).</summary>
    public double EngagedCompletionThreshold { get; set; } = Tuning.EngagedCompletionThreshold;

    /// <summary>completionPct above which an item counts as "watched" for phase selection.</summary>
    public double WatchedCompletionThreshold { get; set; } = Tuning.WatchedCompletionThreshold;

    // ───────────────────────── Phase gating ─────────────────────────
    /// <summary>Watched count at/above which contentSimilarity (centroid) shelves are produced.</summary>
    public int ContentSimilarityMinWatched { get; set; } = Tuning.ContentSimilarityMinWatched;

    /// <summary>Watched count at/above which the trained ranker (instead of the heuristic) orders shelves.</summary>
    public int FullModelMinWatched { get; set; } = Tuning.FullModelMinWatched;

    // ───────────────────────── Shelves & post-processing ─────────────────────────
    /// <summary>How many shelves the home page round-robin produces (default 10).</summary>
    public int MaxShelves { get; set; } = 10;

    public int ItemsPerShelf { get; set; } = Tuning.DefaultItemsPerShelf;

    public int SimilarLimit { get; set; } = Tuning.DefaultSimilarLimit;

    /// <summary>Diversity soft-caps (genre/decade/creator windows) on shelf rerank.</summary>
    public bool EnableDiversity { get; set; } = true;

    /// <summary>Inject lower-ranked exploration items into shelves (page 0 / discovery).</summary>
    public bool EnableExploration { get; set; } = false;

    /// <summary>
    /// Boost recently-added items on home shelves: +0.08·(1 − ageDays/30) for items under 30 days old.
    /// Off by default (best with a library that regularly gets new media).
    /// </summary>
    public bool EnableFreshnessBoost { get; set; } = false;

    // ───────────────────────── Web UI integration ─────────────────────────
    /// <summary>
    /// Inject the Gelo client script into jellyfin-web's home (recommendation shelves) and detail pages
    /// ("More Like This"). On by default. Flip off to serve the vanilla web client with no injection
    /// (takes effect on next page load — index.html is served no-cache).
    /// </summary>
    public bool EnableWebUI { get; set; } = true;

    /// <summary>How many Gelo recommendation rails to render on the home screen.</summary>
    public int WebUIShelfCount { get; set; } = 4;

    /// <summary>Where the Gelo rails appear on home: "top", "afterFirst" (default), or "bottom".</summary>
    public string WebUIPosition { get; set; } = "afterFirst";

    public int QuickFixMaxMinutes { get; set; } = Tuning.QuickFixMaxMinutes;

    public int DeepDiveMinMinutes { get; set; } = Tuning.DeepDiveMinMinutes;

    // ───────────────────────── Embedding / engine ─────────────────────────
    public string ModelPath { get; set; } = string.Empty;

    public string VocabPath { get; set; } = string.Empty;

    public int EmbeddingDim { get; set; } = Tuning.EmbeddingDim;

    public int MaxSequenceLength { get; set; } = Tuning.MaxSequenceLength;

    /// <summary>Per-batch embedding count for the full reindex (ONNX throughput vs. memory).</summary>
    public int BatchSize { get; set; } = 16;

    /// <summary>Throttle for the background ONNX worker — protects CPU on low-power hosts.</summary>
    public int MaxEmbedsPerSecond { get; set; } = 4;

    /// <summary>Comma-separated EP preference, e.g. "CPU" (this host is Intel-only).</summary>
    public string ExecutionProviderOrder { get; set; } = "CPU";
}
