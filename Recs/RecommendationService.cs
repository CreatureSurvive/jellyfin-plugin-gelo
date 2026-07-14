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

    public List<ShelfDto> Shelves(Guid userId)
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

        // Phase 0 — ruleBased (cold start): no engagement yet.
        var contentMin = EffectiveInt(cfg?.ContentSimilarityMinWatched, Tuning.ContentSimilarityMinWatched, 0, 1000);
        if (watchedCount < contentMin)
        {
            return new RuleBasedShelves(items, feedback).Build();
        }

        // Phase 1-9 (contentSimilarity) and 10+ (fullModel) both build a taste profile + centroid
        // and let SmartShelfEngine rerank by centroid affinity. When the Phase-2 ranker is enabled AND
        // the user has crossed FullModelMinWatched, hand the trained re-ranker down: RerankPipeline then
        // orders candidates by the model's P(engage) instead of the heuristic (falling back gracefully
        // if no model is trained yet).
        var (profile, tables) = _profileBuilder.Build(userId);
        if (profile.IsCold && profile.TopCollections.Count == 0)
        {
            return new RuleBasedShelves(items, feedback).Build();
        }

        var fullMin = EffectiveInt(cfg?.FullModelMinWatched, Tuning.FullModelMinWatched, 1, 1000);
        ITier2Ranker? activeRanker = cfg?.EnableTier2Ranker is true && watchedCount >= fullMin ? _ranker : null;

        var centroid = GetCentroid(userId);
        var affinity = Affinities(centroid, items);
        var maxShelves = EffectiveInt(cfg?.MaxShelves, 10, 1, 10);
        return new SmartShelfEngine(items, profile, tables, affinity, _library, _logger, feedback: feedback, ranker: activeRanker, userId: userId).Build(maxShelves);
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
