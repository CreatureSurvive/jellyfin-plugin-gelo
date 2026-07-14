// SPDX-FileCopyrightText: 2026 Dana Buehre
// SPDX-License-Identifier: GPL-3.0-or-later

using System;

namespace Jellyfin.Plugin.Gelo.Recs.Shelves;

/// <summary>
/// Shelf header-title template pools.
/// A title is picked deterministically from the pool using the per-build rotation seed combined
/// with a stable FNV hash of the shelf's anchor key (so the same anchor keeps its title within a
/// build cycle but reshuffles across refreshes).
/// </summary>
internal static class ShelfTitles
{
    public static readonly string[] Genre =
    {
        "{0} favorites", "Your kind of {1}", "More {1}", "Best of {0}",
        "{0} you'll like", "Worth a watch · {0}"
    };

    public static readonly string[] FavoriteAnchored =
    {
        "Because you love {0}", "Inspired by {0}", "Built around {0}",
        "For fans of {0}", "From the world of {0}", "Tuned to {0}"
    };

    public static readonly string[] RecentlyWatched =
    {
        "Since you watched {0}", "After {0}, try…", "Following {0}",
        "Next up from {0}", "More like {0}", "On the heels of {0}"
    };

    public static readonly string[] Collection =
    {
        "Continue: {0}", "Continuing {0}", "More of {0}",
        "{0} · keep going", "Where you left off · {0}"
    };

    public static readonly string[] Director =
    {
        "{0} picks", "Directed by {0}", "From {0}",
        "{0}'s lineup", "Films by {0}", "More from {0}"
    };

    public static readonly string[] Actor =
    {
        "Featuring {0}", "Starring {0}", "{0} on screen",
        "More with {0}", "Watch {0} in…", "{0}'s lineup"
    };

    public static readonly string[] Composer =
    {
        "Scored by {0}", "Music by {0}", "{0}'s soundtracks",
        "Set to {0}", "Sounds of {0}"
    };

    public static readonly string[] Wildcard =
    {
        "Outside your usual", "Try something different", "Off the beaten path",
        "Worth a shot · {0}", "Branch out · {0}", "A change of pace",
        "Step outside the box", "You might be surprised"
    };

    /// <summary>{0}=Display, {1}=lowercased anchor (genre/name).</summary>
    public static string Pick(string[] pool, ulong rotationSeed, string anchorKey, string display, string lower)
    {
        var idx = (int)((rotationSeed + SeededRandom.StableSeed(anchorKey)) % (ulong)pool.Length);
        var tmpl = pool[idx];
        return tmpl.Replace("{0}", display).Replace("{1}", lower);
    }
}
