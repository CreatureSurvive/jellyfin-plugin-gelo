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
    /// </summary>
    public List<SimilarItemDto> Recommendations(Guid userId, int limit, bool unwatched, string? type)
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

        var centroid = (watchedCount >= contentMin && interactions.Count > 0) ? GetCentroid(userId) : null;
        if (centroid is not null && centroid.ItemCount > 0)
        {
            // Warm: cosine to the taste centroid over an oversampled pool, optional trained rerank.
            var poolSize = Math.Min(limit * 5, 100);
            var pool = VectorMath.TopKSimilar(centroid.Vector, items, null, poolSize, Pred);

            IEnumerable<(IndexedItem Item, float Affinity)> ranked = pool.Select(p => (p.Item, p.Score));
            if (cfg?.EnableTier2Ranker is true && _ranker.IsAvailable(userId) && pool.Count > 0)
            {
                var poolItems = pool.Select(p => p.Item).ToList();
                var aff = pool.ToDictionary(p => p.Item.Id, p => p.Score);
                var probs = _ranker.Score(userId, poolItems, aff);
                if (probs is { Length: > 0 } && probs.Length == pool.Count)
                {
                    ranked = pool
                        .Select((p, i) => (p.Item, Affinity: p.Score, Prob: probs[i]))
                        .OrderByDescending(t => t.Prob)
                        .ThenByDescending(t => t.Affinity)
                        .Select(t => (t.Item, t.Affinity));
                }
            }

            return ranked
                .Take(limit)
                .Select(t => new SimilarItemDto(t.Item.Id, t.Item.Name, t.Item.ItemType, t.Affinity))
                .ToList();
        }

        // Cold: no taste profile yet — rank by community rating, then recency.
        return items
            .Where(i => i.Vector.Length > 0 && Pred(i))
            .OrderByDescending(i => i.CommunityRating ?? 0)
            .ThenByDescending(i => i.DateCreatedTicks ?? 0)
            .Take(limit)
            .Select(i => new SimilarItemDto(i.Id, i.Name, i.ItemType, i.CommunityRating.HasValue ? (float)i.CommunityRating.Value : 0f))
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
