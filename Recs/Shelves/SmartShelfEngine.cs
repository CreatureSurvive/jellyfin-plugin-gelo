// SPDX-FileCopyrightText: 2026 Dana Buehre
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Gelo.Api;
using Jellyfin.Plugin.Gelo.Constants;
using Jellyfin.Plugin.Gelo.Persistence;
using Jellyfin.Plugin.Gelo.Recs.Tier2;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Gelo.Recs.Shelves;

/// <summary>
/// Smart shelf builder. Produces up to 10 shelves round-robin across 10 categories (genre,
/// similar×2, collection, mood, period, microGenre, director, actor, composer, wildcard), each
/// seeded-rotated per build and semantic-reranked by user-centroid affinity. Runs only for non-cold
/// users (≥1 engaged item); cold users get ruleBased shelves from <see cref="RecommendationService"/>.
/// </summary>
internal sealed class SmartShelfEngine
{
    private const int CandidatesPerShelf = 30;
    private const int DefaultPerShelf = 15;
    private const int MaxShelves = 10;

    private readonly IReadOnlyList<IndexedItem> _items;
    private readonly Dictionary<Guid, IndexedItem> _byId;
    private readonly TasteProfile _profile;
    private readonly AffinityTables _tables;
    private readonly Dictionary<Guid, float> _affinity;
    private readonly IReadOnlyDictionary<Guid, string>? _feedback;
    private readonly ITier2Ranker? _ranker;
    private readonly Guid? _userId;
    private readonly ILibraryManager _library;
    private readonly ILogger _logger;
    private readonly SeededRandom _rng;
    private readonly ulong _rotationSeed;
    private readonly int _perShelf;
    private readonly bool _skipDiversity;
    private readonly bool _skipExploration;

    public SmartShelfEngine(
        IReadOnlyList<IndexedItem> items,
        TasteProfile profile,
        AffinityTables tables,
        Dictionary<Guid, float> affinity,
        ILibraryManager library,
        ILogger logger,
        ulong? rotationSeed = null,
        IReadOnlyDictionary<Guid, string>? feedback = null,
        ITier2Ranker? ranker = null,
        Guid? userId = null)
    {
        _items = items;
        _byId = items.ToDictionary(i => i.Id);
        _profile = profile;
        _tables = tables;
        _affinity = affinity;
        _feedback = feedback;
        _ranker = ranker;
        _userId = userId;
        _library = library;
        _logger = logger;
        _rotationSeed = rotationSeed ?? NextSeed();
        _rng = new SeededRandom(_rotationSeed);
        var cfg = Plugin.Instance?.Configuration;
        _perShelf = cfg?.ItemsPerShelf is { } n and > 0 ? Math.Min(n, DefaultPerShelf) : DefaultPerShelf;
        // Diversity is on by default; exploration is off (page-0 discovery only).
        _skipDiversity = cfg?.EnableDiversity is false;
        _skipExploration = cfg?.EnableExploration is not true;
    }

    public List<ShelfDto> Build(int maxShelves = MaxShelves)
    {
        // Early exit: cold profile with no collections → no smart shelves (ruleBased handles it).
        if (_profile.IsCold && _profile.TopCollections.Count == 0)
        {
            return new List<ShelfDto>();
        }

        // Per-category plan lists, in canonical priority order.
        var categories = new List<List<Func<ShelfDto?>>>
        {
            GenrePlans(),
            SimilarPlans(),
            CollectionPlans(),
            MoodPlans(),
            PeriodPlans(),
            MicroGenrePlans(),
            PersonPlans(_profile.TopDirectors, ShelfTitles.Director, "Person:Director", "director"),
            PersonPlans(_profile.TopCast, ShelfTitles.Actor, "Person:Actor", "actor"),
            PersonPlans(_profile.TopComposers, ShelfTitles.Composer, "Person:Composer", "composer"),
            WildcardPlans()
        };

        // Round-robin interleave (earlier categories win ties), up to maxShelves.
        var interleaved = new List<Func<ShelfDto?>>(maxShelves);
        var indices = new int[categories.Count];
        while (interleaved.Count < maxShelves)
        {
            var addedAny = false;
            for (var c = 0; c < categories.Count; c++)
            {
                if (indices[c] >= categories[c].Count)
                {
                    continue;
                }

                interleaved.Add(categories[c][indices[c]]);
                indices[c]++;
                addedAny = true;
                if (interleaved.Count >= maxShelves)
                {
                    break;
                }
            }

            if (!addedAny)
            {
                break;
            }
        }

        var shelves = new List<ShelfDto>(interleaved.Count);
        foreach (var materialize in interleaved)
        {
            if (materialize() is { } shelf && shelf.Items.Count > 0)
            {
                shelves.Add(shelf);
            }
        }

        return shelves;
    }

    // ───────────────────────────────────── Categories ─────────────────────────────────────

    private List<Func<ShelfDto?>> GenrePlans()
        => PickRotated(_profile.TopGenres, 3, salt: 1)
            .Select(term => (Func<ShelfDto?>)(() => GenreShelf(term)))
            .ToList();

    private ShelfDto? GenreShelf(WeightedTerm term)
    {
        var candidates = _items.Where(it => Intersects(it.Genres, term.Key)).Take(CandidatesPerShelf).ToList();
        var picked = Rerank(candidates);
        if (picked.Count == 0)
        {
            return null;
        }

        var display = TasteProfileBuilder.DisplayGenre(term.Key);
        var title = ShelfTitles.Pick(ShelfTitles.Genre, _rotationSeed, term.Key, display, display.ToLowerInvariant());
        return new ShelfDto(title, $"Genre:{term.Key}", picked);
    }

    private List<Func<ShelfDto?>> SimilarPlans()
    {
        var plans = new List<Func<ShelfDto?>>();
        var fav = PickRotated(_profile.TopFavoriteAnchors, 1, salt: 2);
        var seenKeys = fav.Select(t => t.Key).ToHashSet();
        var combined = _profile.TopRecentPlays.Concat(_profile.TopSourceItems)
            .Where(t => seenKeys.Add(t.Key))
            .ToList();
        var recent = PickRotated(combined, 3, salt: 7);

        foreach (var anchor in fav)
        {
            plans.Add(() => SimilarShelf(anchor, ShelfTitles.FavoriteAnchored, "Similar:Favorite"));
        }

        foreach (var anchor in recent)
        {
            plans.Add(() => SimilarShelf(anchor, ShelfTitles.RecentlyWatched, "Similar:Recent"));
        }

        return plans;
    }

    private ShelfDto? SimilarShelf(WeightedTerm anchor, string[] titlePool, string paradigm)
    {
        if (!Guid.TryParse(anchor.Key, out var anchorId) || !_byId.TryGetValue(anchorId, out var anchorItem))
        {
            return null;
        }

        var anchorVec = anchorItem.Vector;
        var candidates = _items
            .Where(it => it.Id != anchorId && it.Vector.Length == anchorVec.Length)
            .Select(it => (Item: it, Score: Dot(anchorVec, it.Vector)))
            .OrderByDescending(t => t.Score)
            .Take(CandidatesPerShelf)
            .Select(t => t.Item)
            .ToList();

        var picked = Rerank(candidates);
        if (picked.Count == 0)
        {
            return null;
        }

        var title = ShelfTitles.Pick(titlePool, _rotationSeed, anchor.Key, anchor.DisplayName, anchor.DisplayName.ToLowerInvariant());
        return new ShelfDto(title, paradigm, picked);
    }

    private List<Func<ShelfDto?>> CollectionPlans()
        => PickRotated(_profile.TopCollections, 2, salt: 6)
            .Select(term => (Func<ShelfDto?>)(() => CollectionShelf(term)))
            .ToList();

    private ShelfDto? CollectionShelf(WeightedTerm term)
    {
        if (!Guid.TryParse(term.Key, out var boxId))
        {
            return null;
        }

        List<IndexedItem> children;
        try
        {
            children = (_library.GetItemById(boxId) as BoxSet)?.Children
                ?.Select(c => _byId.GetValueOrDefault(c.Id))
                .Where(i => i is not null)
                .Cast<IndexedItem>()
                .ToList() ?? new List<IndexedItem>();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Collection {Id} failed to resolve.", boxId);
            return null;
        }

        if (children.Count < 2)
        {
            return null;
        }

        // Order by affinity; the diversity pipeline then handles final ordering (an unwatched-first
        // refinement for collection shelves is deferred for now).
        var ordered = children
            .OrderByDescending(c => _affinity.GetValueOrDefault(c.Id))
            .Take(CandidatesPerShelf)
            .ToList();
        var picked = Rerank(ordered);
        if (picked.Count == 0)
        {
            return null;
        }

        var title = ShelfTitles.Pick(ShelfTitles.Collection, _rotationSeed, term.Key, term.DisplayName, term.DisplayName.ToLowerInvariant());
        return new ShelfDto(title, "Collection", picked);
    }

    private List<Func<ShelfDto?>> MoodPlans()
    {
        // Unweighted random pick of 2 presets (moods are NOT affinity-gated).
        var pool = ShelfPresets.Moods.ToList();
        var picked = new List<ShelfPreset>();
        while (pool.Count > 0 && picked.Count < 2)
        {
            var idx = _rng.NextInt(pool.Count);
            picked.Add(pool[idx]);
            pool.RemoveAt(idx);
        }

        return picked.Select(p => (Func<ShelfDto?>)(() => MoodShelf(p))).ToList();
    }

    private ShelfDto? MoodShelf(ShelfPreset preset)
    {
        var matched = ShelfPresets.Filter(_items, preset, requireTags: true);
        var picked = Rerank(matched);
        return picked.Count == 0 ? null : new ShelfDto(preset.Title, $"Mood:{preset.Key}", picked);
    }

    private List<Func<ShelfDto?>> PeriodPlans()
        => PickRotated(_profile.TopPeriodGenres, 2, salt: 8)
            .Select(term => (Func<ShelfDto?>)(() => PeriodShelf(term)))
            .ToList();

    private ShelfDto? PeriodShelf(WeightedTerm term)
    {
        var parts = term.Key.Split('|', 2);
        if (parts.Length != 2 || !int.TryParse(parts[0], out var decade))
        {
            return null;
        }

        var genre = parts[1];
        var yearMin = decade;
        var yearMax = decade + 9;
        var candidates = _items
            .Where(it => it.Year.HasValue && it.Year.Value >= yearMin && it.Year.Value <= yearMax
                         && Intersects(it.Genres, genre))
            .Take(CandidatesPerShelf)
            .ToList();
        var picked = Rerank(candidates);
        if (picked.Count == 0)
        {
            return null;
        }

        return new ShelfDto(TasteProfileBuilder.DecadeGenreLabel(term.Key), $"Period:{term.Key}", picked);
    }

    private List<Func<ShelfDto?>> MicroGenrePlans()
    {
        // Affinity-gated: a preset's best-genre affinity must exceed 0.15.
        var eligible = ShelfPresets.MicroGenres
            .Select(p => (Preset: p, Best: p.Genres.Any() ? p.Genres.Max(g => GenreAffinity(g)) : 0))
            .Where(t => t.Best > 0.15)
            .ToList();
        if (eligible.Count == 0)
        {
            return new List<Func<ShelfDto?>>();
        }

        var terms = eligible.Select(t => new WeightedTerm(t.Preset.Key, t.Preset.Title, t.Best)).ToList();
        return PickRotated(terms, 3, salt: 9)
            .Select(term => (Func<ShelfDto?>)(() => MicroGenreShelf(term)))
            .ToList();
    }

    private ShelfDto? MicroGenreShelf(WeightedTerm term)
    {
        var preset = Array.Find(ShelfPresets.MicroGenres, p => p.Key == term.Key);
        if (preset is null)
        {
            return null;
        }

        var matched = ShelfPresets.Filter(_items, preset, requireTags: true);
        var picked = Rerank(matched);
        return picked.Count == 0 ? null : new ShelfDto(preset.Title, $"MicroGenre:{preset.Key}", picked);
    }

    private List<Func<ShelfDto?>> PersonPlans(IReadOnlyList<WeightedTerm> people, string[] titlePool, string paradigm, string role)
        => PickRotated(people, 2, salt: role switch { "director" => 3, "actor" => 4, _ => 5 })
            .Select(term => (Func<ShelfDto?>)(() => PersonShelf(term, titlePool, paradigm, role)))
            .ToList();

    private ShelfDto? PersonShelf(WeightedTerm term, string[] titlePool, string paradigm, string role)
    {
        var name = term.DisplayName;
        var candidates = _items
            .Where(it => role switch
            {
                "director" => it.DirectorNames.Any(d => string.Equals(d, name, StringComparison.OrdinalIgnoreCase)),
                "actor" => it.CastNames.Any(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase)),
                "composer" => it.ComposerNames.Any(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase)),
                _ => false
            })
            .Take(CandidatesPerShelf)
            .ToList();

        var picked = Rerank(candidates);
        if (picked.Count == 0)
        {
            return null;
        }

        var title = ShelfTitles.Pick(titlePool, _rotationSeed, name, name, name.ToLowerInvariant());
        return new ShelfDto(title, paradigm, picked);
    }

    private List<Func<ShelfDto?>> WildcardPlans()
    {
        var topGenreKeys = _profile.TopGenres.Select(g => g.Key.ToLowerInvariant()).ToHashSet();
        var candidates = ShelfPresets.CommonGenres.Where(g => !topGenreKeys.Contains(g.ToLowerInvariant())).ToList();
        if (candidates.Count == 0)
        {
            return new List<Func<ShelfDto?>>();
        }

        var genre = candidates[_rng.NextInt(candidates.Count)];
        return new List<Func<ShelfDto?>> { () => WildcardShelf(genre) };
    }

    private ShelfDto? WildcardShelf(string genre)
    {
        // skipRerank: sort by community rating DESC (with random tiebreak), min 6.5.
        var picked = _items
            .Where(it => it.CommunityRating.HasValue && it.CommunityRating >= 6.5 && Intersects(it.Genres, genre))
            .Where(it => !IsLessFeedback(it.Id))
            .OrderByDescending(it => it.CommunityRating!.Value)
            .Take(_perShelf)
            .Select(it => ToShelfItem(it, 0, "Off-genre pick"))
            .ToList();
        if (picked.Count == 0)
        {
            return null;
        }

        var display = TasteProfileBuilder.DisplayGenre(genre);
        var title = ShelfTitles.Pick(ShelfTitles.Wildcard, _rotationSeed, genre, display, display.ToLowerInvariant());
        return new ShelfDto(title, "Wildcard", picked);
    }

    // ───────────────────────────────────── Helpers ─────────────────────────────────────

    /// <summary>
    /// Rerank candidates through the post-processing pipeline: centroid affinity + affinity boost,
    /// then diversity soft-caps (genre/decade/creator), then exploration injection. Wildcard bypasses
    /// this (uses its own community-rating ordering).
    /// </summary>
    private List<ShelfItemDto> Rerank(IReadOnlyList<IndexedItem> candidates)
        => RerankPipeline.Finish(candidates, _affinity, _tables, _rng, _perShelf, _skipDiversity, _skipExploration, _feedback, _ranker, _userId);

    private static string ReasonFor(IndexedItem it)
        => it.Year is { } y ? $"{y}" : it.CommunityRating is { } r ? $"★ {r:F1}" : string.Empty;

    private ShelfItemDto ToShelfItem(IndexedItem it, float score, string reason)
        => new(it.Id, it.Name, it.ItemType, score, reason);

    private double GenreAffinity(string genre)
    {
        foreach (var g in _profile.TopGenres)
        {
            if (string.Equals(g.Key, genre, StringComparison.OrdinalIgnoreCase))
            {
                return g.Weight;
            }
        }

        return 0;
    }

    /// <summary>pickRotated: weighted sampling without replacement, draw chance ∝ max(weight, 0.01).</summary>
    private List<WeightedTerm> PickRotated(IReadOnlyList<WeightedTerm> terms, int take, ulong salt)
    {
        if (terms.Count <= take)
        {
            return terms.ToList();
        }

        var rng = new SeededRandom(_rotationSeed + salt);
        var pool = terms.ToList();
        var picked = new List<WeightedTerm>(take);
        for (var i = 0; i < take && pool.Count > 0; i++)
        {
            var totalWeight = pool.Sum(t => Math.Max(t.Weight, 0.01));
            var target = rng.NextDouble() * totalWeight;
            var accumulated = 0.0;
            for (var idx = 0; idx < pool.Count; idx++)
            {
                accumulated += Math.Max(pool[idx].Weight, 0.01);
                if (target < accumulated)
                {
                    picked.Add(pool[idx]);
                    pool.RemoveAt(idx);
                    break;
                }
            }
        }

        return picked;
    }

    private static bool Intersects(string[] a, string b)
    {
        foreach (var x in a)
        {
            if (string.Equals(x, b, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True if the user explicitly voted "less" on this item (suppress it).</summary>
    private bool IsLessFeedback(Guid id)
        => _feedback is { Count: > 0 } && _feedback.TryGetValue(id, out var k) && k == "less";

    private static float Dot(float[] a, float[] b)
    {
        if (a.Length != b.Length)
        {
            return 0;
        }

        float sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    private static ulong NextSeed()
    {
        // Per-build variety: reseed every build. Combine a tick count with a per-app nonce.
        var ticks = (ulong)Environment.TickCount64;
        var nonce = 0x9E3779B97F4A7C15UL;
        return SeededRandom.StableSeed(FormattableString.Invariant($"gelo-{ticks}-{nonce}"));
    }
}
