// SPDX-FileCopyrightText: 2026 Dana Buehre
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Gelo.Api;
using Jellyfin.Plugin.Gelo.Persistence;

namespace Jellyfin.Plugin.Gelo.Recs.Shelves;

/// <summary>
/// Cold-start shelves for users with no engagement history (phase 0 = ruleBased). A server has no
/// onboarding taste picker, so we surface community-rating + mood-preset shelves to keep a
/// brand-new home from being empty.
/// </summary>
internal sealed class RuleBasedShelves
{
    private static readonly Random _rng = new();
    private readonly IReadOnlyList<IndexedItem> _items;
    private readonly int _perShelf;
    private readonly HashSet<Guid> _suppressed;

    public RuleBasedShelves(IReadOnlyList<IndexedItem> items, IReadOnlyDictionary<Guid, string>? feedback = null)
    {
        _items = items;
        _perShelf = Plugin.Instance?.Configuration.ItemsPerShelf is { } n and > 0 ? Math.Min(n, 15) : 15;
        // "less" votes suppress the item even on cold-start shelves.
        _suppressed = feedback is { Count: > 0 }
            ? feedback.Where(kv => kv.Value == "less").Select(kv => kv.Key).ToHashSet()
            : new HashSet<Guid>();
    }

    public List<ShelfDto> Build()
    {
        var shelves = new List<ShelfDto>();
        Add(shelves, TopRated("Movies", "Movie"));
        Add(shelves, TopRated("Shows", "Series"));

        // Two distinct affinity-free mood presets, reranked by community rating.
        foreach (var preset in PickMoods(2))
        {
            Add(shelves, MoodShelf(preset));
        }

        return shelves;
    }

    private ShelfDto? TopRated(string label, string itemType)
    {
        var picked = _items
            .Where(it => string.Equals(it.ItemType, itemType, StringComparison.OrdinalIgnoreCase)
                         && it.CommunityRating.HasValue
                         && !_suppressed.Contains(it.Id))
            .OrderByDescending(it => it.CommunityRating!.Value)
            .Take(_perShelf)
            .Select(it => new ShelfItemDto(it.Id, it.Name, it.ItemType, (float)(it.CommunityRating ?? 0), "Top rated"))
            .ToList();
        return picked.Count == 0 ? null : new ShelfDto($"Top rated {label.ToLowerInvariant()}", $"RuleBased:TopRated:{label}", picked);
    }

    private ShelfDto? MoodShelf(ShelfPreset preset)
    {
        var picked = ShelfPresets.Filter(_items, preset, requireTags: true)
            .Where(it => !_suppressed.Contains(it.Id))
            .OrderByDescending(it => it.CommunityRating ?? 0)
            .Take(_perShelf)
            .Select(it => new ShelfItemDto(it.Id, it.Name, it.ItemType, (float)(it.CommunityRating ?? 0), preset.Title))
            .ToList();
        return picked.Count == 0 ? null : new ShelfDto(preset.Title, $"Mood:{preset.Key}", picked);
    }

    private List<ShelfPreset> PickMoods(int count)
    {
        var pool = ShelfPresets.Moods.ToList();
        var picked = new List<ShelfPreset>(count);
        while (pool.Count > 0 && picked.Count < count)
        {
            var idx = _rng.Next(pool.Count);
            picked.Add(pool[idx]);
            pool.RemoveAt(idx);
        }

        return picked;
    }

    private static void Add(List<ShelfDto> shelves, ShelfDto? shelf)
    {
        if (shelf is not null)
        {
            shelves.Add(shelf);
        }
    }
}
