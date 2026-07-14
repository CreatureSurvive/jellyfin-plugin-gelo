// SPDX-FileCopyrightText: 2026 Dana Buehre
// SPDX-License-Identifier: GPL-3.0-or-later

using System;

namespace Jellyfin.Plugin.Gelo.Persistence;

/// <summary>A library item and its L2-normalised embedding, held in the warm cache.</summary>
public sealed class IndexedItem
{
    public Guid Id { get; set; }
    public string ItemType { get; set; } = string.Empty; // Movie | Series | Season
    public Guid? ParentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int? Year { get; set; }
    public long? RunTimeTicks { get; set; }
    public string[] Genres { get; set; } = Array.Empty<string>();
    public string[] Tags { get; set; } = Array.Empty<string>();
    public string[] Studios { get; set; } = Array.Empty<string>();
    public double? CommunityRating { get; set; }
    public string? OfficialRating { get; set; }
    public string? TmdbId { get; set; }
    public string[] DirectorNames { get; set; } = Array.Empty<string>();
    public string[] CastNames { get; set; } = Array.Empty<string>();
    public string[] ComposerNames { get; set; } = Array.Empty<string>();
    public string[] WriterNames { get; set; } = Array.Empty<string>();
    public float[] Vector { get; set; } = Array.Empty<float>();
    public long UpdatedAtTicks { get; set; }

    /// <summary>BaseItem.DateCreated — drives the freshness boost on home shelves.</summary>
    public long? DateCreatedTicks { get; set; }
}

/// <summary>A user's interaction with an item, with precomputed decay + relevance.</summary>
public sealed class InteractionRecord
{
    public Guid UserId { get; set; }
    public Guid ItemId { get; set; }
    public int PlayCount { get; set; }
    public long? LastPlayedTicks { get; set; }
    public bool Played { get; set; }
    public bool IsFavorite { get; set; }
    public double CompletionPct { get; set; }
    public double DwellNorm { get; set; }
    public double TemporalDecay { get; set; }
    public double RelevanceScore { get; set; }
    public long UpdatedAtTicks { get; set; }
}

/// <summary>A user's relevance-weighted centroid over their interacted items' vectors.</summary>
public sealed class UserCentroid
{
    public Guid UserId { get; set; }
    public float[] Vector { get; set; } = Array.Empty<float>();
    public int ItemCount { get; set; }
    public long UpdatedAtTicks { get; set; }
}
