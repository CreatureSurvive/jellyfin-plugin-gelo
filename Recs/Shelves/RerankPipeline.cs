// SPDX-FileCopyrightText: 2026 Dana Buehre
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Gelo.Api;
using Jellyfin.Plugin.Gelo.Constants;
using Jellyfin.Plugin.Gelo.Persistence;
using Jellyfin.Plugin.Gelo.Recs.Tier2;

namespace Jellyfin.Plugin.Gelo.Recs.Shelves;

/// <summary>
/// Post-processing pipeline for a shelf's candidate set, producing the final ordered top-N:
/// affinity boost, then diversity soft-caps (genre / decade / creator), then exploration injection.
/// Freshness (+0.08·(1-age/30d)) lifts items added in the last 30 days when
/// <c>EnableFreshnessBoost</c> is on.
/// </summary>
internal static class RerankPipeline
{
    private const int DiversityWindow = 8;
    private const int MaxGenrePerWindow = 3;
    private const int MaxDecadePerWindow = 2;
    private const int MaxCreatorPerWindow = 1;

    // Affinity-boost weights: how strongly each creator/genre/tag dimension nudges a candidate.
    private const double WDirector = 0.30;
    private const double WComposer = 0.20;
    private const double WCast = 0.15;
    private const double WWriter = 0.08;
    private const double WGenre = 0.08;
    private const double WTag = 0.04;

    /// <summary>Explicit "more like this" rerank nudge applied to user-voted "more" items.</summary>
    private const double MoreLikeNudge = 0.12;

    /// <summary>Freshness lift for items added in the last N days.</summary>
    private const double FreshnessBoostMax = 0.08;

    private const double FreshnessWindowDays = 30.0;

    /// <summary>
    /// Materialize the final shelf items from a candidate set: score each by centroid affinity +
    /// affinity boost, apply diversity soft-caps, inject exploration, take <paramref name="perShelf"/>.
    /// Explicit feedback from <paramref name="feedback"/> suppresses "less"-voted items entirely and
    /// nudges "more"-voted items up by <see cref="MoreLikeNudge"/>.
    /// </summary>
    public static List<ShelfItemDto> Finish(
        IReadOnlyList<IndexedItem> candidates,
        Dictionary<Guid, float> affinity,
        AffinityTables tables,
        SeededRandom rng,
        int perShelf,
        bool skipDiversity = false,
        bool skipExploration = true,
        IReadOnlyDictionary<Guid, string>? feedback = null,
        ITier2Ranker? ranker = null,
        Guid? userId = null)
    {
        if (candidates.Count == 0)
        {
            return new List<ShelfItemDto>();
        }

        // Suppress "less" items entirely — the user said don't show me this. (Belt-and-suspenders:
        // callers also filter, but this is the single choke point every shelf reranks through.)
        var pool = feedback is { Count: > 0 }
            ? candidates.Where(it => !IsLess(feedback, it.Id)).ToList()
            : candidates.ToList();

        var enableFreshness = Plugin.Instance?.Configuration?.EnableFreshnessBoost is true;
        var nowTicks = DateTime.UtcNow.Ticks;

        // Phase-2: when a trained ranker is available for this user, order by its predicted engagement
        // probability (the "full model" tier) and add only the real-time more-like / freshness lifts on
        // top — they're UX controls not present at training time, so they must still nudge. If the model
        // can't score (not trained / load failed), Score returns null and we fall back to the heuristic.
        var useModel = ranker is not null && userId.HasValue;
        float[]? probs = null;
        if (useModel)
        {
            probs = ranker!.Score(userId!.Value, pool, affinity);
            if (probs is null || probs.Length != pool.Count)
            {
                useModel = false;
            }
        }

        IEnumerable<(IndexedItem Item, double Score)> raw;
        if (useModel)
        {
            var p = probs!;
            raw = pool.Select((it, i) => (it, (double)p[i] + MoreNudge(feedback, it.Id) + Freshness(it, nowTicks, enableFreshness)));
        }
        else
        {
            // Heuristic: centroid affinity + normalized affinity boost + moreLike nudge + freshness
            // lift (+ community rating as a tiny floor tiebreaker so cold shelves aren't arbitrary).
            raw = pool.Select(it => (it, affinity.GetValueOrDefault(it.Id) + Boost(it, tables) + MoreNudge(feedback, it.Id) + Freshness(it, nowTicks, enableFreshness)));
        }

        var scored = raw
            .OrderByDescending(t => t.Score)
            .ThenByDescending(t => t.Item.CommunityRating ?? 0)
            .ToList();

        var ordered = skipDiversity
            ? scored.Select(t => t.Item).ToList()
            : ApplyDiversity(scored);

        // Exploration: steal a couple of lower-ranked items and stagger them in (page 0 / discovery).
        if (!skipExploration && ordered.Count > perShelf)
        {
            ordered = InjectExploration(ordered, rng, perShelf);
        }

        return ordered
            .Take(perShelf)
            .Select(it => new ShelfItemDto(it.Id, it.Name, it.ItemType, affinity.GetValueOrDefault(it.Id), ReasonFor(it)))
            .ToList();
    }

    /// <summary>Per-item affinity boost = Σ(weightᵢ · meanNormalized(affinityᵢ)).</summary>
    private static double Boost(IndexedItem it, AffinityTables t)
    {
        return WDirector * MeanNorm(it.DirectorNames, t.Director)
             + WComposer * MeanNorm(it.ComposerNames, t.Composer)
             + WCast * MeanNorm(it.CastNames, t.Cast)
             + WWriter * MeanNorm(it.WriterNames, t.Writer)
             + WGenre * MeanNorm(it.Genres, t.Genre)
             + WTag * MeanNorm(it.Tags, t.Tag);
    }

    private static bool IsLess(IReadOnlyDictionary<Guid, string> feedback, Guid id)
        => feedback.TryGetValue(id, out var k) && k == "less";

    private static double MoreNudge(IReadOnlyDictionary<Guid, string>? feedback, Guid id)
        => feedback is not null && feedback.TryGetValue(id, out var k) && k == "more" ? MoreLikeNudge : 0;

    /// <summary>Freshness lift for recently-added items: +(FreshnessBoostMax)·(1 − ageDays/window).</summary>
    private static double Freshness(IndexedItem it, long nowTicks, bool enabled)
    {
        if (!enabled || !it.DateCreatedTicks.HasValue)
        {
            return 0;
        }

        var ageDays = (nowTicks - it.DateCreatedTicks.Value) / (double)TimeSpan.TicksPerDay;
        if (ageDays >= FreshnessWindowDays || ageDays < 0)
        {
            return 0;
        }

        return FreshnessBoostMax * (1.0 - ageDays / FreshnessWindowDays);
    }

    private static double MeanNorm(string[] terms, Dictionary<string, double> table)
    {
        if (terms.Length == 0 || table.Count == 0)
        {
            return 0;
        }

        var max = table.Values.Max();
        if (max <= 0)
        {
            return 0;
        }

        var sum = 0.0;
        var found = 0;
        foreach (var term in terms)
        {
            if (table.TryGetValue(term, out var w))
            {
                sum += w / max;
                found++;
            }
        }

        return found == 0 ? 0 : sum / found;
    }

    /// <summary>
    /// Diversity soft-caps: walk the ranked list, deferring any item that would exceed a per-window
    /// cap for genre / decade / creator. Deferred items append to the end. Chain order is
    /// genre → decade → creator, each window=8.
    /// </summary>
    private static List<IndexedItem> ApplyDiversity(List<(IndexedItem Item, double Score)> scored)
    {
        var kept = new List<IndexedItem>(scored.Count);
        var deferred = new List<IndexedItem>(scored.Count);
        var genreWindow = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var decadeWindow = new Dictionary<int, int>();
        var creatorWindow = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var (item, _) in scored)
        {
            var pos = kept.Count;
            var windowStart = Math.Max(0, pos - DiversityWindow + 1);

            // Reset counts outside the sliding window.
            if (pos >= DiversityWindow)
            {
                PruneWindow(genreWindow, kept, windowStart, it => it.Genres);
                PruneDecadeWindow(decadeWindow, kept, windowStart);
                PruneWindow(creatorWindow, kept, windowStart, CreatorKeys);
            }

            var genreOk = WithinCap(item.Genres, genreWindow, MaxGenrePerWindow);
            var decade = DecadeBin(item.Year);
            var decadeOk = decade < 0 || (decadeWindow.GetValueOrDefault(decade) < MaxDecadePerWindow);
            var creatorOk = WithinCap(CreatorKeys(item), creatorWindow, MaxCreatorPerWindow);

            if (genreOk && decadeOk && creatorOk)
            {
                kept.Add(item);
                Bump(genreWindow, item.Genres);
                if (decade >= 0)
                {
                    decadeWindow[decade] = decadeWindow.GetValueOrDefault(decade) + 1;
                }

                Bump(creatorWindow, CreatorKeys(item));
            }
            else
            {
                deferred.Add(item);
            }
        }

        kept.AddRange(deferred);
        return kept;
    }

    private static void PruneWindow(Dictionary<string, int> window, List<IndexedItem> kept, int windowStart, Func<IndexedItem, string[]> keys)
    {
        if (windowStart == 0)
        {
            return;
        }

        // Decrement keys for the item that just left the window.
        var leaver = kept[windowStart - 1];
        foreach (var k in keys(leaver))
        {
            if (window.TryGetValue(k, out var c))
            {
                if (c <= 1)
                {
                    window.Remove(k);
                }
                else
                {
                    window[k] = c - 1;
                }
            }
        }
    }

    private static void PruneDecadeWindow(Dictionary<int, int> window, List<IndexedItem> kept, int windowStart)
    {
        if (windowStart == 0)
        {
            return;
        }

        var leaver = kept[windowStart - 1];
        var d = DecadeBin(leaver.Year);
        if (d >= 0 && window.TryGetValue(d, out var c))
        {
            if (c <= 1)
            {
                window.Remove(d);
            }
            else
            {
                window[d] = c - 1;
            }
        }
    }

    private static bool WithinCap(string[] keys, Dictionary<string, int> window, int cap)
    {
        foreach (var k in keys)
        {
            if (window.GetValueOrDefault(k) >= cap)
            {
                return false;
            }
        }

        return true;
    }

    private static void Bump(Dictionary<string, int> window, string[] keys)
    {
        foreach (var k in keys)
        {
            window[k] = window.GetValueOrDefault(k) + 1;
        }
    }

    /// <summary>Creator/franchise diversity keys: "d:&lt;director&gt;" for each director.</summary>
    private static string[] CreatorKeys(IndexedItem it)
    {
        if (it.DirectorNames.Length == 0)
        {
            return Array.Empty<string>();
        }

        var keys = new string[it.DirectorNames.Length];
        for (var i = 0; i < keys.Length; i++)
        {
            keys[i] = "d:" + it.DirectorNames[i].ToLowerInvariant();
        }

        return keys;
    }

    /// <summary>Decade bin from year (8 bins across ~80 years); -1 if no year.</summary>
    private static int DecadeBin(int? year)
        => year.HasValue && year.Value >= 1950 && year.Value <= 2030
            ? (year.Value - 1950) / 10
            : -1;

    /// <summary>
    /// Exploration injection: draw max(perShelf/10, 2) items from the lower half of the ranked list,
    /// shuffle deterministically, and insert them at staggered positions.
    /// </summary>
    private static List<IndexedItem> InjectExploration(List<IndexedItem> ordered, SeededRandom rng, int perShelf)
    {
        var explorationCount = Math.Max(perShelf / 10, 2);
        var half = ordered.Count / 2;
        if (half <= 0 || explorationCount >= half)
        {
            return ordered;
        }

        var pool = ordered.Skip(half).ToList();
        var picks = new List<IndexedItem>(explorationCount);
        for (var i = 0; i < explorationCount && pool.Count > 0; i++)
        {
            var idx = rng.NextInt(pool.Count);
            picks.Add(pool[idx]);
            pool.RemoveAt(idx);
        }

        var result = ordered.Where(i => !picks.Contains(i)).ToList();
        foreach (var (pick, i) in picks.Select((p, i) => (p, i)))
        {
            var insertAt = Math.Min((i + 1) * (perShelf / (explorationCount + 1)), result.Count);
            result.Insert(insertAt, pick);
        }

        return result;
    }

    /// <summary>
    /// Flat-list diversity: the same sliding-window soft-caps shelves use (genre / decade / creator),
    /// applied to an already-ranked candidate list so the consecutive results don't clump. The score
    /// tuple is positional only — <see cref="ApplyDiversity"/> iterates in input order and discards it —
    /// so we feed the ranking index to preserve the caller's order. Deterministic.
    /// </summary>
    internal static List<IndexedItem> DiversifyRanking(IReadOnlyList<IndexedItem> ranked)
    {
        if (ranked.Count <= 1)
        {
            return ranked.ToList();
        }

        var scored = new List<(IndexedItem Item, double Score)>(ranked.Count);
        for (var i = 0; i < ranked.Count; i++)
        {
            scored.Add((ranked[i], i));
        }

        return ApplyDiversity(scored);
    }

    /// <summary>
    /// Flat-list exploration: keep the top <paramref name="resultLimit"/> − explorationCount strongest
    /// matches, then draw explorationCount items from deeper in the ranking (seeded-shuffled) and
    /// stagger them into the front window so a few discovery picks surface each pass. Brings in items
    /// that pure top-N would never show; the seed controls rotation. Returns ≤ resultLimit items in a
    /// new list (caller still Take(limit)s). No-op when there aren't enough candidates to explore.
    /// </summary>
    internal static List<IndexedItem> InjectExplorationFlat(List<IndexedItem> ranked, SeededRandom rng, int resultLimit, int explorationCount)
    {
        if (ranked.Count <= resultLimit || explorationCount <= 0)
        {
            return ranked;
        }

        explorationCount = Math.Min(explorationCount, Math.Max(1, resultLimit / 3));
        var frontCap = resultLimit - explorationCount;
        if (frontCap <= 0)
        {
            return ranked;
        }

        var front = ranked.Take(frontCap).ToList();
        var tailPool = ranked.Skip(frontCap).ToList();
        if (tailPool.Count == 0)
        {
            return ranked;
        }

        var picks = new List<IndexedItem>(explorationCount);
        for (var i = 0; i < explorationCount && tailPool.Count > 0; i++)
        {
            var idx = rng.NextInt(tailPool.Count);
            picks.Add(tailPool[idx]);
            tailPool.RemoveAt(idx);
        }

        // Stagger the discovery picks into the front at roughly even intervals (front and tail are
        // disjoint by construction, so no dedupe is needed).
        var result = new List<IndexedItem>(resultLimit);
        var step = Math.Max(1, front.Count / (picks.Count + 1));
        var pickIdx = 0;
        for (var i = 0; i < front.Count; i++)
        {
            result.Add(front[i]);
            if (pickIdx < picks.Count && (i + 1) % step == 0)
            {
                result.Add(picks[pickIdx++]);
            }
        }

        while (pickIdx < picks.Count)
        {
            result.Add(picks[pickIdx++]);
        }

        return result;
    }

    private static string ReasonFor(IndexedItem it)
        => it.Year is { } y ? $"{y}" : it.CommunityRating is { } r ? FormattableString.Invariant($"★ {r:F1}") : string.Empty;
}
