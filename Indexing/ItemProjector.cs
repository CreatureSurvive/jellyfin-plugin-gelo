// SPDX-FileCopyrightText: 2026 Dana Buehre
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Gelo.Persistence;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using PersonKind = Jellyfin.Data.Enums.PersonKind;

namespace Jellyfin.Plugin.Gelo.Indexing;

/// <summary>Projects a Jellyfin <see cref="BaseItem"/> + people into our <see cref="IndexedItem"/>.</summary>
internal static class ItemProjector
{
    public static IndexedItem Project(BaseItem item, IReadOnlyList<PersonInfo> people, float[] vector)
    {
        Guid? parentId = null;
        if (item is Episode ep)
        {
            parentId = ep.SeriesId;
        }
        else if (item is Season season && season.SeriesId != Guid.Empty)
        {
            parentId = season.SeriesId;
        }

        return new IndexedItem
        {
            Id = item.Id,
            ItemType = item.GetType().Name,
            ParentId = parentId,
            Name = item.Name ?? string.Empty,
            Year = item.ProductionYear,
            RunTimeTicks = item.RunTimeTicks,
            Genres = item.Genres ?? Array.Empty<string>(),
            Tags = item.Tags ?? Array.Empty<string>(),
            Studios = item.Studios ?? Array.Empty<string>(),
            CommunityRating = item.CommunityRating,
            OfficialRating = item.OfficialRating,
            TmdbId = item.ProviderIds.TryGetValue("Tmdb", out var tmdb) ? tmdb : null,
            DirectorNames = people
                .Where(p => p.Type == PersonKind.Director)
                .Select(p => p.Name)
                .ToArray(),
            CastNames = people
                .Where(p => p.Type == PersonKind.Actor)
                .OrderBy(p => p.SortOrder ?? int.MaxValue)
                .Select(p => p.Name)
                .Take(20)
                .ToArray(),
            ComposerNames = people
                .Where(p => p.Type == PersonKind.Composer)
                .Select(p => p.Name)
                .ToArray(),
            WriterNames = people
                .Where(p => p.Type == PersonKind.Writer)
                .Select(p => p.Name)
                .ToArray(),
            Vector = vector,
            UpdatedAtTicks = DateTime.UtcNow.Ticks,
            DateCreatedTicks = item.DateCreated.Ticks
        };
    }
}
