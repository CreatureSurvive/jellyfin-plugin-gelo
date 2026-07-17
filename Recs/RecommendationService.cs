// SPDX-FileCopyrightText: 2026 Dana Buehre
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics.Tensors;
using Jellyfin.Plugin.Gelo.Api;
using Jellyfin.Plugin.Gelo.Constants;
using Jellyfin.Plugin.Gelo.Embedding;
using Jellyfin.Plugin.Gelo.Persistence;
using Jellyfin.Plugin.Gelo.Recs.Shelves;
using Jellyfin.Plugin.Gelo.Recs.Tier2;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Gelo.Recs;

/// <summary>
/// The hot read path. <see cref="Similar"/> is a sub-ms SIMD scan over the warm cache and is
/// phase-agnostic (always semantic). <see cref="Shelves"/> applies three-phase gating:
/// 0 watched → ruleBased; 1-9 → contentSimilarity (centroid affinity); 10+ → fullModel (degrades to
/// contentSimilarity until the trained ranker exists), then delegates to <see cref="SmartShelfEngine"/>.
/// </summary>
public sealed class RecommendationService
{
    private readonly VectorStore _store;
    private readonly CentroidBuilder _centroids;
    private readonly EmbeddingService _embeddings;
    private readonly TasteProfileBuilder _profileBuilder;
    private readonly ITier2Ranker _ranker;
    private readonly ILibraryManager _library;
    private readonly ILogger<RecommendationService> _logger;
    private readonly int _itemsPerShelf;

    public RecommendationService(
        VectorStore store,
        CentroidBuilder centroids,
        EmbeddingService embeddings,
        TasteProfileBuilder profileBuilder,
        ITier2Ranker ranker,
        ILibraryManager library,
        ILogger<RecommendationService> logger)
    {
        _store = store;
        _centroids = centroids;
        _embeddings = embeddings;
        _profileBuilder = profileBuilder;
        _ranker = ranker;
        _library = library;
        _logger = logger;
        _itemsPerShelf = Plugin.Instance?.Configuration.ItemsPerShelf ?? Tuning.DefaultItemsPerShelf;
    }

    public List<SimilarItemDto> Similar(
        Guid itemId,
        int limit,
        string? type,
        bool unwatched,
        Guid? userId,
        string? collection)
    {
        if (IsDisabled)
        {
            return new List<SimilarItemDto>();
        }

        var target = _store.GetItem(itemId);
        if (target is null || target.Vector.Length == 0)
        {
            return new List<SimilarItemDto>();
        }

        limit = Math.Clamp(limit, 1, 100);
        var items = _store.GetAllItems();

        Func<IndexedItem, bool>? predicate = BuildPredicate(type, unwatched, userId, collection);

        var top = VectorMath.TopKSimilar(target.Vector, items, itemId, limit, predicate);
        return top
            .Select(t => new SimilarItemDto(t.Item.Id, t.Item.Name, t.Item.ItemType, t.Score))
            .ToList();
    }

    /// <summary>
    /// Flat "Recommended For You" list — the engine's primary surface. Ranks the whole library by
    /// cosine similarity to the user's taste centroid and, when the trained ranker is enabled,
    /// re-orders an oversampled pool by its predicted engagement probability. Cold users (no profile)
    /// fall back to a community-rating ranking. When <paramref name="unwatched"/> is true (the
    /// default), items the user has already engaged with are excluded.
    /// <para>
    /// <paramref name="variety"/> ("off"|"low"|"medium"|"high") optionally diversifies the result and
    /// injects exploration picks so the list is not the same static set every time. "off" (the default)
    /// reproduces the exact deterministic top-N. Exploration is seeded per user per day (stable within
    /// a day, rotates daily); pass an explicit <paramref name="seed"/> to override (e.g. to page or
    /// force a fresh shuffle). An unset <paramref name="variety"/> falls back to the server's
    /// <c>RecommendationsVariety</c> config.
    /// </para>
    /// </summary>
    public List<SimilarItemDto> Recommendations(Guid userId, int limit, bool unwatched, string? type, string? variety = null, int? seed = null)
    {
        if (IsDisabled)
        {
            return new List<SimilarItemDto>();
        }

        var items = _store.GetAllItems();
        if (items.Count == 0)
        {
            return new List<SimilarItemDto>();
        }

        limit = Math.Clamp(limit, 1, 100);

        var cfg = Plugin.Instance?.Configuration;
        var interactions = _store.GetInteractionsForUser(userId);
        var watchedThreshold = Effective(cfg?.WatchedCompletionThreshold, Tuning.WatchedCompletionThreshold, 0, 1);
        var watchedCount = interactions.Count(ix => ix.CompletionPct > watchedThreshold);
        var contentMin = EffectiveInt(cfg?.ContentSimilarityMinWatched, Tuning.ContentSimilarityMinWatched, 0, 1000);

        // Predicate: type filter + "less"-feedback suppression (via BuildPredicate), plus an
        // unwatched guard that drops anything the user has already engaged with.
        var basePredicate = BuildPredicate(type, unwatched: false, userId, null);
        var engaged = unwatched ? interactions.Select(i => i.ItemId).ToHashSet() : null;
        bool Pred(IndexedItem it)
            => (basePredicate is null || basePredicate(it)) && (engaged is null || !engaged.Contains(it.Id));

        var level = ResolveVariety(variety, cfg?.RecommendationsVariety);
        var (poolMult, doDiversity, exploreFrac) = VarietyProfile(level);
        // Off keeps the original oversample cap (limit*5, max 100) so behavior is byte-identical;
        // higher variety widens the pool so exploration can reach deeper, lower-affinity items.
        var poolSize = level == Variety.Off
            ? Math.Min(limit * 5, 100)
            : Math.Clamp(limit * poolMult, limit, 400);
        poolSize = Math.Min(poolSize, items.Count);

        // Build an ordered candidate pool + the per-item display score (cosine affinity, or community
        // rating on the cold path). The trained ranker re-orders the warm pool when enabled.
        List<IndexedItem> ranked;
        var scoreOf = new Dictionary<Guid, float>();

        var centroid = (watchedCount >= contentMin && interactions.Count > 0) ? GetCentroid(userId) : null;
        if (centroid is not null && centroid.ItemCount > 0)
        {
            // Warm: cosine to the taste centroid over an oversampled pool, optional trained rerank.
            var pool = VectorMath.TopKSimilar(centroid.Vector, items, null, poolSize, Pred);
            scoreOf = pool.ToDictionary(p => p.Item.Id, p => p.Score);

            IEnumerable<IndexedItem> ordered = pool.OrderByDescending(p => p.Score).Select(p => p.Item);
            if (cfg?.EnableTier2Ranker is true && _ranker.IsAvailable(userId) && pool.Count > 0)
            {
                var poolItems = pool.Select(p => p.Item).ToList();
                var aff = pool.ToDictionary(p => p.Item.Id, p => p.Score);
                var probs = _ranker.Score(userId, poolItems, aff);
                if (probs is { Length: > 0 } && probs.Length == pool.Count)
                {
                    ordered = pool
                        .Select((p, i) => (p.Item, Prob: probs[i], Aff: p.Score))
                        .OrderByDescending(t => t.Prob)
                        .ThenByDescending(t => t.Aff)
                        .Select(t => t.Item);
                }
            }

            ranked = ordered.ToList();
        }
        else
        {
            // Cold: no taste profile yet — rank by community rating, then recency.
            ranked = items
                .Where(i => i.Vector.Length > 0 && Pred(i))
                .OrderByDescending(i => i.CommunityRating ?? 0)
                .ThenByDescending(i => i.DateCreatedTicks ?? 0)
                .Take(poolSize)
                .ToList();
            foreach (var i in ranked)
            {
                scoreOf[i.Id] = i.CommunityRating.HasValue ? (float)i.CommunityRating.Value : 0f;
            }
        }

        // Variety post-processing: diversity soft-caps (deterministic spread) then seeded exploration
        // (rotating discovery picks). Skipped entirely for Off, which keeps the legacy exact top-N.
        if (level != Variety.Off && ranked.Count > 0)
        {
            if (doDiversity)
            {
                ranked = RerankPipeline.DiversifyRanking(ranked);
            }

            var explorationCount = Math.Clamp((int)Math.Ceiling(limit * exploreFrac), 0, Math.Max(0, limit / 2 - 1));
            if (explorationCount > 0 && ranked.Count > limit)
            {
                var rng = new SeededRandom(ResolveSeed(userId, seed));
                ranked = RerankPipeline.InjectExplorationFlat(ranked, rng, limit, explorationCount);
            }
        }

        return ranked
            .Take(limit)
            .Select(t => new SimilarItemDto(t.Id, t.Name, t.ItemType, scoreOf.GetValueOrDefault(t.Id)))
            .ToList();
    }

    public List<ShelfDto> Shelves(Guid userId, int? limit = null, bool unwatched = false, string? type = null)
    {
        if (IsDisabled)
        {
            return new List<ShelfDto>();
        }

        var items = _store.GetAllItems();
        if (items.Count == 0)
        {
            return new List<ShelfDto>();
        }

        var cfg = Plugin.Instance?.Configuration;
        var interactions = _store.GetInteractionsForUser(userId);
        var feedback = _store.GetFeedbackForUser(userId);

        // PHASE GATE: watchedCount = items with completionPct above the configured watched threshold.
        var watchedThreshold = Effective(cfg?.WatchedCompletionThreshold, Tuning.WatchedCompletionThreshold, 0, 1);
        var watchedCount = interactions.Count(ix => ix.CompletionPct > watchedThreshold);

        List<ShelfDto> result;

        // Phase 0 — ruleBased (cold start): no engagement yet.
        var contentMin = EffectiveInt(cfg?.ContentSimilarityMinWatched, Tuning.ContentSimilarityMinWatched, 0, 1000);
        if (watchedCount < contentMin)
        {
            result = new RuleBasedShelves(items, feedback).Build();
        }
        else
        {
            // Phase 1-9 (contentSimilarity) and 10+ (fullModel) both build a taste profile + centroid
            // and let SmartShelfEngine rerank by centroid affinity. When the Phase-2 ranker is enabled AND
            // the user has crossed FullModelMinWatched, hand the trained re-ranker down: RerankPipeline then
            // orders candidates by the model's P(engage) instead of the heuristic (falling back gracefully
            // if no model is trained yet).
            var (profile, tables) = _profileBuilder.Build(userId);
            if (profile.IsCold && profile.TopCollections.Count == 0)
            {
                result = new RuleBasedShelves(items, feedback).Build();
            }
            else
            {
                var fullMin = EffectiveInt(cfg?.FullModelMinWatched, Tuning.FullModelMinWatched, 1, 1000);
                ITier2Ranker? activeRanker = cfg?.EnableTier2Ranker is true && watchedCount >= fullMin ? _ranker : null;

                var centroid = GetCentroid(userId);
                var affinity = Affinities(centroid, items);
                var maxShelves = EffectiveInt(cfg?.MaxShelves, 10, 1, 10);
                result = new SmartShelfEngine(items, profile, tables, affinity, _library, _logger, feedback: feedback, ranker: activeRanker, userId: userId).Build(maxShelves);
            }
        }

        return PostFilterShelves(result, userId, limit, unwatched, type);
    }

    /// <summary>
    /// Optional request-time shaping of already-built shelves: an item-type filter, an unwatched
    /// filter (drops items the user has played), and a per-shelf item cap. Shelves that empty out
    /// are dropped. This is a coarse trim applied AFTER the diversity/ordering pipeline, so prefer
    /// server config for structural control and reserve these for one-off client needs.
    /// </summary>
    private List<ShelfDto> PostFilterShelves(List<ShelfDto> shelves, Guid userId, int? limit, bool unwatched, string? type)
    {
        if (!limit.HasValue && !unwatched && string.IsNullOrWhiteSpace(type))
        {
            return shelves;
        }

        var played = unwatched
            ? _store.GetInteractionsForUser(userId).Where(i => i.Played).Select(i => i.ItemId).ToHashSet()
            : null;
        var perShelf = limit.HasValue ? Math.Clamp(limit.Value, 1, 100) : (int?)null;
        var hasType = !string.IsNullOrWhiteSpace(type);

        var result = new List<ShelfDto>(shelves.Count);
        foreach (var s in shelves)
        {
            IEnumerable<ShelfItemDto> filtered = s.Items;
            if (hasType)
            {
                filtered = filtered.Where(i => string.Equals(i.Type, type, StringComparison.OrdinalIgnoreCase));
            }

            if (played is { Count: > 0 })
            {
                filtered = filtered.Where(i => !played.Contains(i.Id));
            }

            if (perShelf.HasValue)
            {
                filtered = filtered.Take(perShelf.Value);
            }

            var list = filtered.ToList();
            if (list.Count > 0)
            {
                result.Add(s with { Items = list });
            }
        }

        return result;
    }

    private static bool IsDisabled
        => Plugin.Instance?.Configuration is { } c && (!c.EnablePlugin || !c.EnableIndexing);

    private static double Effective(double? configured, double fallback, double min, double max)
        => configured.HasValue && configured.Value >= min && configured.Value <= max ? configured.Value : fallback;

    private static int EffectiveInt(int? configured, int fallback, int min, int max)
        => configured.HasValue && configured.Value >= min && configured.Value <= max ? configured.Value : fallback;

    /// <summary>Flat-list variety tiers. <see cref="Recommendations"/> post-processing intensity.</summary>
    private enum Variety { Off, Low, Medium, High }

    private static Variety ResolveVariety(string? requested, string? configuredDefault)
    {
        var raw = !string.IsNullOrWhiteSpace(requested) ? requested : configuredDefault;
        return raw?.Trim().ToLowerInvariant() switch
        {
            "low" or "1" => Variety.Low,
            "medium" or "med" or "2" => Variety.Medium,
            "high" or "3" => Variety.High,
            _ => Variety.Off,
        };
    }

    /// <summary>Per-tier profile: oversample multiplier, whether to diversify, and the exploration
    /// fraction of <c>limit</c>. Low only spreads the existing top matches; Medium/High widen the pool
    /// and rotate discovery picks in.</summary>
    private static (int PoolMult, bool Diversity, double ExploreFrac) VarietyProfile(Variety v) => v switch
    {
        Variety.Low => (5, true, 0.0),
        Variety.Medium => (10, true, 0.15),
        Variety.High => (20, true, 0.30),
        _ => (5, false, 0.0),
    };

    /// <summary>Exploration seed: an explicit value wins; otherwise a stable-per-user, rotating-per-day
    /// bucket so the "For You" list feels like a daily mix rather than a per-refresh shuffle.</summary>
    private static ulong ResolveSeed(Guid userId, int? seed)
    {
        if (seed.HasValue)
        {
            return (ulong)seed.Value;
        }

        var dayIndex = (int)(DateTime.UtcNow.Date - new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalDays;
        return SeededRandom.StableSeed(userId.ToString()) ^ ((ulong)dayIndex * 0x9E3779B97F4A7C15UL);
    }

    public StatusDto Status()
    {
        return new StatusDto(
            _store.ItemCount,
            _embeddings.Dimension,
            _embeddings.IsReady,
            FormatTicks(_store.GetMeta("last_full_reindex")),
            ModelId: _store.GetMeta("model_id"));
    }

    /// <summary>
    /// Non-admin readiness probe. Same engine state as <see cref="Status"/> but without the fields
    /// (model id, last reindex) a regular client doesn't need, and served to ANY authenticated user.
    /// <see cref="PingDto.Ready"/> is true only when the plugin is enabled, embeddings are loaded,
    /// and at least one item is indexed.
    /// </summary>
    public PingDto Ping()
    {
        var enabled = Plugin.Instance?.Configuration is { } c && c.EnablePlugin && c.EnableIndexing;
        var embeddingsReady = _embeddings.IsReady;
        var itemCount = _store.ItemCount;
        // Ready = the engine can serve recommendations right now (plugin enabled and indexed items are
        // present, so shelves/similar/recommendations work from the cached vectors). EmbeddingsReady is
        // reported separately and intentionally NOT part of Ready: it only flips true once the ONNX
        // session is lazily loaded (on the first embed), so it can be false right after a restart even
        // though serving already works. It tells the client whether NEW items can be embedded/reindexed.
        var ready = enabled && itemCount > 0;
        return new PingDto(enabled, embeddingsReady, itemCount, ready);
    }

    private Func<IndexedItem, bool>? BuildPredicate(string? type, bool unwatched, Guid? userId, string? collection)
    {
        var filters = new List<Func<IndexedItem, bool>>(3);

        if (!string.IsNullOrWhiteSpace(type))
        {
            filters.Add(it => string.Equals(it.ItemType, type, StringComparison.OrdinalIgnoreCase));
        }

        if (unwatched && userId.HasValue)
        {
            var played = _store.GetInteractionsForUser(userId.Value)
                .Where(i => i.Played)
                .Select(i => i.ItemId)
                .ToHashSet();
            filters.Add(it => !played.Contains(it.Id));
        }

        // Suppress items the user explicitly voted "less" on (when the request is user-scoped).
        if (userId.HasValue)
        {
            var lessItems = _store.GetFeedbackForUser(userId.Value)
                .Where(kv => kv.Value == "less")
                .Select(kv => kv.Key)
                .ToHashSet();
            if (lessItems.Count > 0)
            {
                filters.Add(it => !lessItems.Contains(it.Id));
            }
        }

        if (!string.IsNullOrWhiteSpace(collection))
        {
            var c = collection;
            filters.Add(it =>
                (it.Studios is { Length: > 0 } && it.Studios.Any(s => s.Contains(c, StringComparison.OrdinalIgnoreCase)))
                || (it.Tags is { Length: > 0 } && it.Tags.Any(t => t.Contains(c, StringComparison.OrdinalIgnoreCase))));
        }

        if (filters.Count == 0)
        {
            return null;
        }

        return it =>
        {
            foreach (var f in filters)
            {
                if (!f(it))
                {
                    return false;
                }
            }

            return true;
        };
    }

    private UserCentroid GetCentroid(Guid userId)
    {
        var existing = _store.GetCentroid(userId);
        var latestInteraction = _store.GetInteractionsForUser(userId)
            .Select(i => i.UpdatedAtTicks)
            .DefaultIfEmpty(0)
            .Max();

        if (existing is null || existing.ItemCount == 0 || existing.UpdatedAtTicks < latestInteraction)
        {
            var built = _centroids.Build(userId);
            _store.SetCentroid(built);
            return built;
        }

        return existing;
    }

    private static Dictionary<Guid, float> Affinities(UserCentroid centroid, IReadOnlyList<IndexedItem> items)
    {
        var span = centroid.Vector.AsSpan();
        var aff = new Dictionary<Guid, float>(items.Count);
        foreach (var item in items)
        {
            if (item.Vector.Length == span.Length)
            {
                aff[item.Id] = TensorPrimitives.Dot(span, item.Vector);
            }
        }

        return aff;
    }

    private static string? FormatTicks(string? ticks)
        => long.TryParse(ticks, NumberStyles.None, CultureInfo.InvariantCulture, out var t)
            ? new DateTime(t, DateTimeKind.Utc).ToString("u", CultureInfo.InvariantCulture)
            : null;
}
