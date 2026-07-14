// SPDX-FileCopyrightText: 2026 Dana Buehre
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.Gelo.Constants;
using Jellyfin.Plugin.Gelo.Persistence;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Gelo.Recs;

/// <summary>A normalized term (genre, person, item, "decade|genre", collection) with a [0,1] weight.</summary>
public sealed record WeightedTerm(string Key, string DisplayName, double Weight);

/// <summary>
/// The user's taste profile: weighted top-N lists of genres, people, source items, collections, and
/// decades. Drives SmartShelf category selection (which anchors to build shelves around).
/// </summary>
public sealed class TasteProfile
{
    public IReadOnlyList<WeightedTerm> TopGenres { get; init; } = Array.Empty<WeightedTerm>();
    public IReadOnlyList<WeightedTerm> TopDirectors { get; init; } = Array.Empty<WeightedTerm>();
    public IReadOnlyList<WeightedTerm> TopCast { get; init; } = Array.Empty<WeightedTerm>();
    public IReadOnlyList<WeightedTerm> TopComposers { get; init; } = Array.Empty<WeightedTerm>();
    public IReadOnlyList<WeightedTerm> TopSourceItems { get; init; } = Array.Empty<WeightedTerm>();
    public IReadOnlyList<WeightedTerm> TopRecentPlays { get; init; } = Array.Empty<WeightedTerm>();
    public IReadOnlyList<WeightedTerm> TopFavoriteAnchors { get; init; } = Array.Empty<WeightedTerm>();
    public IReadOnlyList<WeightedTerm> TopPeriodGenres { get; init; } = Array.Empty<WeightedTerm>();
    public IReadOnlyList<WeightedTerm> TopCollections { get; init; } = Array.Empty<WeightedTerm>();

    public bool IsCold { get; init; }

    public static TasteProfile Empty { get; } = new();
}

/// <summary>Per-dimension affinity tables (genre/director/cast/composer/writer/tag), [term→weight].</summary>
public sealed class AffinityTables
{
    public Dictionary<string, double> Genre { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> Director { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> Cast { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> Composer { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> Writer { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> Tag { get; } = new(StringComparer.OrdinalIgnoreCase);

    public double GenreAffinity(string genre) => Genre.TryGetValue(genre, out var w) ? w : 0;
}

/// <summary>
/// Builds <see cref="TasteProfile"/> + <see cref="AffinityTables"/> from the warm item cache and a
/// user's interactions: top genres/people/source-items/collections/decades, plus the per-dimension
/// affinity tables shelves use to boost candidates.
/// </summary>
public sealed class TasteProfileBuilder
{
    private const int RotationPoolDepth = 16;
    private const double AffinityWeightFloor = 0.05; // buildAffinityTables gate
    private const double SourceItemMinRelevance = 0.15;
    private const double RecentRecencyHalfLifeDays = 30.0;
    private const double RecentRecencyFloor = 0.05;
    private const int RecentWindowDays = 120;
    private const double RecentCompletionGate = 0.15;
    private const int PeriodMinEngaged = 3;
    private const double CollectionMinScore = 0.05;
    private const int CollectionMinChildren = 2;

    private readonly VectorStore _store;
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly ILogger<TasteProfileBuilder> _logger;

    public TasteProfileBuilder(VectorStore store, ILibraryManager library, IUserManager users, ILogger<TasteProfileBuilder> logger)
    {
        _store = store;
        _library = library;
        _users = users;
        _logger = logger;
    }

    /// <summary>Build the profile + tables for a user.</summary>
    public (TasteProfile Profile, AffinityTables Tables) Build(Guid userId)
    {
        var interactions = _store.GetInteractionsForUser(userId);
        if (interactions.Count == 0)
        {
            return (TasteProfile.Empty, new AffinityTables());
        }

        var maxPlayCount = interactions.Max(i => i.PlayCount);
        var tables = new AffinityTables();

        // Affinity accumulation over engaged interactions, weighted by per-user relevance.
        var periodSum = new Dictionary<string, double>(StringComparer.Ordinal);
        var periodCount = new Dictionary<string, int>(StringComparer.Ordinal);

        var sourceItems = new List<(InteractionRecord Ix, IndexedItem Item, double Rel)>();
        var recentPlays = new List<(InteractionRecord Ix, IndexedItem Item, double Recency)>();
        var favAnchors = new List<(InteractionRecord Ix, IndexedItem Item, double Rel)>();

        foreach (var ix in interactions)
        {
            if (_store.GetItem(ix.ItemId) is not { } item)
            {
                continue;
            }

            var pcn = Relevance.PlayCountNorm(ix.PlayCount, maxPlayCount);
            var engaged = Relevance.IsEngaged(ix.CompletionPct, pcn, ix.IsFavorite);
            var rel = Relevance.Recompute(ix, maxPlayCount);

            // Affinity tables: engaged + weight floor. (Feedback weighting moreLike 0.90 / lessLike
            // skip is applied by FeedbackStore — wired in a later step; default weight = relevance.)
            if (engaged && rel > AffinityWeightFloor)
            {
                Accumulate(tables.Genre, item.Genres, rel);
                Accumulate(tables.Director, item.DirectorNames, rel);
                Accumulate(tables.Cast, item.CastNames, rel);
                Accumulate(tables.Composer, item.ComposerNames, rel);
                Accumulate(tables.Writer, item.WriterNames, rel);
                Accumulate(tables.Tag, item.Tags, rel);

                foreach (var genre in item.Genres)
                {
                    if (item.Year is { } year && year >= 1950 && year <= 2030)
                    {
                        var decade = (year / 10) * 10;
                        var key = FormattableString.Invariant($"{decade}|{genre}");
                        periodSum[key] = periodSum.GetValueOrDefault(key) + rel;
                        periodCount[key] = periodCount.GetValueOrDefault(key) + 1;
                    }
                }
            }

            if (!ix.IsFavorite && rel > SourceItemMinRelevance)
            {
                sourceItems.Add((ix, item, rel));
            }

            // Recent plays: non-favorite, within window, engagement gate, 30-day half-life recency.
            if (!ix.IsFavorite && ix.LastPlayedTicks is { } ticks)
            {
                var last = new DateTime(ticks, DateTimeKind.Utc);
                var days = (DateTime.UtcNow - last).TotalDays;
                if (days <= RecentWindowDays && (ix.CompletionPct > RecentCompletionGate || ix.PlayCount > 0))
                {
                    var recency = Math.Max(Math.Pow(0.5, days / RecentRecencyHalfLifeDays), RecentRecencyFloor);
                    recentPlays.Add((ix, item, recency));
                }
            }

            if (ix.IsFavorite)
            {
                favAnchors.Add((ix, item, Math.Max(rel, 0.5)));
            }
        }

        var topGenres = TopTerms(tables.Genre, RotationPoolDepth, 0.30, DisplayGenre);
        var topDirectors = TopTerms(tables.Director, RotationPoolDepth, 0.20, Name => Name);
        var topCast = TopTerms(tables.Cast, RotationPoolDepth, 0.30, name => name);
        var topComposers = TopTerms(tables.Composer, RotationPoolDepth, 0.20, name => name);

        var topSources = sourceItems
            .OrderByDescending(t => t.Rel)
            .Take(RotationPoolDepth * 2)
            .Select(t => new WeightedTerm(t.Item.Id.ToString(), t.Item.Name, Math.Min(t.Rel, 1.0)))
            .ToList();

        var topRecent = recentPlays
            .OrderByDescending(t => t.Recency)
            .Take(RotationPoolDepth * 2)
            .Select(t => new WeightedTerm(t.Item.Id.ToString(), t.Item.Name, t.Recency))
            .ToList();

        var topFavs = favAnchors
            .OrderByDescending(t => t.Rel)
            .Take(RotationPoolDepth * 2)
            .Select(t => new WeightedTerm(t.Item.Id.ToString(), t.Item.Name, Math.Min(t.Rel, 1.0)))
            .ToList();

        var topPeriod = TopPeriod(periodSum, periodCount, RotationPoolDepth);
        var topCollections = TopCollections(userId, interactions, maxPlayCount);

        var isCold = topSources.Count == 0 && topGenres.Count == 0 && topDirectors.Count == 0;

        var profile = new TasteProfile
        {
            TopGenres = topGenres,
            TopDirectors = topDirectors,
            TopCast = topCast,
            TopComposers = topComposers,
            TopSourceItems = topSources,
            TopRecentPlays = topRecent,
            TopFavoriteAnchors = topFavs,
            TopPeriodGenres = topPeriod,
            TopCollections = topCollections,
            IsCold = isCold
        };

        return (profile, tables);
    }

    private static void Accumulate(Dictionary<string, double> table, string[] terms, double weight)
    {
        foreach (var term in terms)
        {
            if (string.IsNullOrWhiteSpace(term))
            {
                continue;
            }

            table[term] = table.GetValueOrDefault(term) + weight;
        }
    }

    /// <summary>topTerms: keep value &gt;= maxValue*minWeight, sort DESC, take topN, normalize to [0,1].</summary>
    private static List<WeightedTerm> TopTerms(Dictionary<string, double> table, int topN, double minWeight, Func<string, string> display)
    {
        if (table.Count == 0)
        {
            return new List<WeightedTerm>();
        }

        var max = table.Values.Max();
        if (max <= 0)
        {
            return new List<WeightedTerm>();
        }

        return table
            .Where(kv => kv.Value >= max * minWeight)
            .OrderByDescending(kv => kv.Value)
            .Take(topN)
            .Select(kv => new WeightedTerm(kv.Key, display(kv.Key), kv.Value / max))
            .ToList();
    }

    private static List<WeightedTerm> TopPeriod(Dictionary<string, double> sum, Dictionary<string, int> count, int topN)
    {
        if (sum.Count == 0)
        {
            return new List<WeightedTerm>();
        }

        var eligible = sum
            .Where(kv => count.GetValueOrDefault(kv.Key) >= PeriodMinEngaged)
            .ToList();
        if (eligible.Count == 0)
        {
            return new List<WeightedTerm>();
        }

        var max = eligible.Max(kv => kv.Value);
        return eligible
            .OrderByDescending(kv => kv.Value)
            .Take(topN)
            .Select(kv => new WeightedTerm(kv.Key, DecadeGenreLabel(kv.Key), kv.Value / max))
            .ToList();
    }

    private List<WeightedTerm> TopCollections(Guid userId, IReadOnlyList<InteractionRecord> interactions, int maxPlayCount)
    {
        var watched = interactions
            .Where(ix => Relevance.IsEngaged(ix.CompletionPct, Relevance.PlayCountNorm(ix.PlayCount, maxPlayCount), ix.IsFavorite))
            .GroupBy(ix => ix.ItemId)
            .ToDictionary(g => g.Key, g => g.Max(ix => Relevance.Recompute(ix, maxPlayCount)));

        var result = new List<WeightedTerm>();
        try
        {
            var boxsets = _library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.BoxSet },
                Recursive = true
            });

            foreach (var box in boxsets.OfType<BoxSet>())
            {
                var children = box.Children ?? Array.Empty<BaseItem>();
                if (children.Count() < CollectionMinChildren)
                {
                    continue;
                }

                var childIds = children.Select(c => c.Id).ToHashSet();
                var watchedChildren = childIds.Where(id => watched.ContainsKey(id)).ToList();
                var unwatchedChildren = childIds.Where(id => !watched.ContainsKey(id)).ToList();
                if (watchedChildren.Count == 0 || unwatchedChildren.Count == 0)
                {
                    continue; // need at least one watched AND one unwatched
                }

                var watchedRel = watchedChildren.Select(id => watched[id]).Average();
                var unwatchedFraction = (double)unwatchedChildren.Count / childIds.Count;
                var progressBoost = 1.0 + 0.25 * unwatchedFraction;
                var score = watchedRel * progressBoost;
                if (score <= CollectionMinScore)
                {
                    continue;
                }

                result.Add(new WeightedTerm(box.Id.ToString(), box.Name, score));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Collection scan failed; skipping collection shelves.");
        }

        if (result.Count == 0)
        {
            return result;
        }

        var max = result.Max(t => t.Weight);
        return result
            .OrderByDescending(t => t.Weight)
            .Take(RotationPoolDepth)
            .Select(t => t with { Weight = t.Weight / max })
            .ToList();
    }

    internal static string DisplayGenre(string key)
        => key.ToLowerInvariant() switch
        {
            "sci-fi" or "science fiction" => "Sci-Fi",
            "tv movie" => "TV",
            "sci fi" => "Sci-Fi",
            _ => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(key.ToLowerInvariant())
        };

    /// <summary>decadeGenreLabel: renders a "decade|genre" key as "90's comedies", "2000s sci-fi".</summary>
    internal static string DecadeGenreLabel(string key)
    {
        var parts = key.Split('|', 2);
        if (parts.Length != 2 || !int.TryParse(parts[0], out var decade))
        {
            return key;
        }

        var decadeLabel = decade < 2000
            ? FormattableString.Invariant($"{decade % 100}'s")
            : FormattableString.Invariant($"{decade}s");

        var genre = parts[1].ToLowerInvariant() switch
        {
            "sci-fi" or "science fiction" => "sci-fi",
            "tv movie" => "TV movies",
            "comedy" => "comedies",
            "documentary" => "documentaries",
            "news" => "news",
            _ => parts[1].ToLowerInvariant() + "s"
        };

        return FormattableString.Invariant($"{decadeLabel} {genre}");
    }
}
