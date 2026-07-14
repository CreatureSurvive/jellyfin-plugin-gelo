// SPDX-FileCopyrightText: 2026 Dana Buehre
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Gelo.Persistence;

namespace Jellyfin.Plugin.Gelo.Recs.Shelves;

/// <summary>
/// A hardcoded genre+tag+rating+runtime+type combination defining a Mood or MicroGenre shelf.
/// </summary>
internal sealed record ShelfPreset(
    string Key,
    string Title,
    string[] Genres,
    string[] Tags,
    string[] OfficialRatings,
    double? MinCommunityRating,
    int? MinRuntimeMinutes,
    int? MaxRuntimeMinutes,
    string[] ItemTypes); // "movie" and/or "series"

/// <summary>
/// The 10 lifestyle Mood presets (NOT affinity-gated) and 13 affinity-gated MicroGenre presets,
/// plus the 19-genre pool used by the Wildcard shelf. Runtime filters apply client-side (here, in-
/// process on the warm cache) since Jellyfin has no runtime filter.
/// </summary>
internal static class ShelfPresets
{
    private static readonly string[] Movie = { "movie" };
    private static readonly string[] Series = { "series" };
    private static readonly string[] MovieSeries = { "movie", "series" };
    private static readonly string[] Empty = Array.Empty<string>();

    public static readonly ShelfPreset[] Moods =
    {
        new("90-min-laughs", "90-minute laughs", new[] { "Comedy" }, Empty, Empty, null, null, 95, Movie),
        new("slow-burn-dramas", "Slow-burn dramas", new[] { "Drama" }, new[] { "slow-burn", "atmospheric", "meditative", "contemplative" }, Empty, 7.0, 120, null, Movie),
        new("long-but-worth-it", "Long, but worth it", Empty, Empty, Empty, 7.5, 150, null, Movie),
        new("binge-worthy", "Binge-worthy series", Empty, Empty, Empty, 8.0, null, null, Series),
        new("rainy-day-picks", "Rainy day picks", new[] { "Drama", "Romance", "Family" }, new[] { "heartwarming", "cozy", "feel-good", "wholesome" }, Empty, 6.5, null, null, Movie),
        new("quick-thrills", "Quick thrills", new[] { "Thriller", "Horror", "Action" }, Empty, Empty, null, null, 100, Movie),
        new("late-night-picks", "Late-night picks", new[] { "Horror", "Thriller", "Mystery" }, new[] { "dark", "moody", "unsettling" }, new[] { "R", "TV-MA", "NC-17" }, null, null, null, MovieSeries),
        new("easy-watch", "Easy to watch", new[] { "Comedy", "Romance", "Family", "Animation" }, Empty, new[] { "G", "PG", "PG-13", "TV-G", "TV-PG", "TV-14" }, null, null, 110, Movie),
        new("weekend-epic", "Weekend epic", new[] { "Adventure", "Drama", "Fantasy", "Action", "History" }, Empty, Empty, 7.0, 140, null, Movie),
        new("critically-acclaimed", "Critically acclaimed", Empty, Empty, Empty, 8.0, null, null, MovieSeries),
    };

    public static readonly ShelfPreset[] MicroGenres =
    {
        new("gritty-action", "Gritty action", new[] { "Action", "Crime", "Thriller" }, new[] { "dark", "gritty", "violent", "brutal", "hard-hitting" }, Empty, null, null, null, MovieSeries),
        new("raunchy-comedy", "Raunchy comedies", new[] { "Comedy" }, Empty, new[] { "R", "NC-17", "TV-MA" }, null, null, null, MovieSeries),
        new("slow-burn-thriller", "Slow-burn thrillers", new[] { "Thriller", "Mystery" }, new[] { "slow-burn", "atmospheric", "tense" }, Empty, 7.0, 110, null, MovieSeries),
        new("cerebral-scifi", "Cerebral sci-fi", new[] { "Science Fiction", "Sci-Fi" }, new[] { "thought-provoking", "mind-bending", "philosophical", "cerebral" }, Empty, 7.0, null, null, MovieSeries),
        new("feelgood-comedy", "Feel-good comedies", new[] { "Comedy", "Family" }, new[] { "feel-good", "heartwarming", "uplifting", "wholesome" }, new[] { "G", "PG", "PG-13", "TV-G", "TV-PG", "TV-14" }, null, null, null, MovieSeries),
        new("atmospheric-horror", "Atmospheric horror", new[] { "Horror" }, new[] { "atmospheric", "slow-burn", "supernatural", "psychological", "eerie" }, Empty, null, null, null, MovieSeries),
        new("cozy-mystery", "Cozy mysteries", new[] { "Mystery" }, Empty, new[] { "G", "PG", "PG-13", "TV-G", "TV-PG", "TV-14" }, null, null, 130, MovieSeries),
        new("action-blockbusters", "Action blockbusters", new[] { "Action", "Adventure" }, Empty, Empty, 6.5, 110, null, MovieSeries),
        new("mind-bending-scifi", "Mind-bending sci-fi", new[] { "Science Fiction", "Sci-Fi", "Thriller" }, new[] { "twist", "mind-bending", "psychological", "surreal" }, Empty, null, null, null, MovieSeries),
        new("crime-epic", "Crime epics", new[] { "Crime", "Drama" }, Empty, Empty, 7.0, 130, null, MovieSeries),
        new("romantic-drama", "Tearjerker romances", new[] { "Romance", "Drama" }, new[] { "emotional", "tearjerker", "romantic" }, Empty, null, null, null, MovieSeries),
        new("high-octane", "High-octane thrills", new[] { "Action", "Thriller" }, new[] { "fast-paced", "intense", "explosive", "high-octane" }, Empty, null, null, 120, MovieSeries),
        new("noir-vibes", "Noir-tinged stories", new[] { "Crime", "Mystery", "Drama" }, new[] { "noir", "neo-noir", "moody", "shadowy" }, Empty, null, null, null, MovieSeries),
    };

    /// <summary>Wildcard draws one genre NOT in the user's top genres from this pool.</summary>
    public static readonly string[] CommonGenres =
    {
        "Action", "Adventure", "Animation", "Biography", "Comedy", "Crime", "Documentary",
        "Drama", "Family", "Fantasy", "History", "Horror", "Music", "Mystery", "Romance",
        "Science Fiction", "Thriller", "War", "Western"
    };

    private const long TicksPerMinute = 600_000_000L;

    /// <summary>Filter the warm item cache by a preset. Returns the matching items.</summary>
    public static IReadOnlyList<IndexedItem> Filter(
        IReadOnlyList<IndexedItem> items,
        ShelfPreset preset,
        bool requireTags)
    {
        var genres = preset.Genres;
        var tags = requireTags ? preset.Tags : Array.Empty<string>();
        var ratings = preset.OfficialRatings;
        var minRating = preset.MinCommunityRating;
        var minRunTicks = preset.MinRuntimeMinutes is { } m ? m * TicksPerMinute : (long?)null;
        var maxRunTicks = preset.MaxRuntimeMinutes is { } x ? x * TicksPerMinute : (long?)null;
        var types = preset.ItemTypes;

        var matched = new List<IndexedItem>(items.Count / 8);
        foreach (var it in items)
        {
            if (!TypeMatches(it.ItemType, types))
            {
                continue;
            }

            if (genres.Length > 0 && !Intersects(it.Genres, genres))
            {
                continue;
            }

            if (tags.Length > 0 && !Intersects(it.Tags, tags))
            {
                continue;
            }

            if (ratings.Length > 0 && (it.OfficialRating is null || !Contains(ratings, it.OfficialRating)))
            {
                continue;
            }

            if (minRating.HasValue && (!it.CommunityRating.HasValue || it.CommunityRating.Value < minRating.Value))
            {
                continue;
            }

            if (minRunTicks.HasValue && (!it.RunTimeTicks.HasValue || it.RunTimeTicks.Value < minRunTicks.Value))
            {
                continue;
            }

            if (maxRunTicks.HasValue && (!it.RunTimeTicks.HasValue || it.RunTimeTicks.Value > maxRunTicks.Value))
            {
                continue;
            }

            matched.Add(it);
        }

        // Tag fallback: if a tag-restricted preset matched <5, retry without tags.
        if (requireTags && preset.Tags.Length > 0 && matched.Count < 5)
        {
            return Filter(items, preset, requireTags: false);
        }

        return matched;
    }

    private static bool TypeMatches(string itemType, string[] types)
    {
        if (types.Length == 0)
        {
            return true;
        }

        var t = itemType.ToLowerInvariant();
        // Stored ItemType is the C# type name ("Movie"/"Series"); map to the preset's vocabulary.
        var norm = t switch
        {
            "movie" => "movie",
            "series" => "series",
            _ when t.Contains("movie") => "movie",
            _ when t.Contains("series") || t.Contains("show") => "series",
            _ => "movie"
        };
        return Array.IndexOf(types, norm) >= 0;
    }

    private static bool Intersects(string[] a, string[] b)
    {
        if (a.Length == 0 || b.Length == 0)
        {
            return false;
        }

        foreach (var x in a)
        {
            foreach (var y in b)
            {
                if (string.Equals(x, y, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool Contains(string[] haystack, string needle)
    {
        foreach (var h in haystack)
        {
            if (string.Equals(h, needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
