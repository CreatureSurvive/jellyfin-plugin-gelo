// SPDX-FileCopyrightText: 2026 Dana Buehre
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.Gelo.Constants;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using PersonKind = Jellyfin.Data.Enums.PersonKind;

namespace Jellyfin.Plugin.Gelo.Embedding;

/// <summary>
/// Composes the metadata text block an item is embedded from, following the reconstructed
/// template in <see cref="Tuning.MetadataBlockTemplate"/>.
/// </summary>
internal static class MetadataBlockBuilder
{
    public static string Build(BaseItem item, IReadOnlyList<PersonInfo> people)
    {
        var title = item.Name ?? string.Empty;
        var year = item.ProductionYear?.ToString(CultureInfo.InvariantCulture)
                   ?? item.PremiereDate?.Year.ToString(CultureInfo.InvariantCulture)
                   ?? string.Empty;

        var genres = item.Genres is { Length: > 0 } g ? string.Join(", ", g) : string.Empty;

        var directors = people
            .Where(p => p.Type == PersonKind.Director)
            .Select(p => p.Name)
            .Take(3);

        var cast = people
            .Where(p => p.Type == PersonKind.Actor)
            .OrderBy(p => p.SortOrder ?? int.MaxValue)
            .Select(p => p.Name)
            .Take(Tuning.MaxCastInBlock);

        var overview = item.Overview ?? string.Empty;

        return Tuning.MetadataBlockTemplate
            .Replace("{title}", title)
            .Replace("{year}", year)
            .Replace("{genres}", genres)
            .Replace("{directors}", string.Join(", ", directors))
            .Replace("{cast}", string.Join(", ", cast))
            .Replace("{overview}", overview)
            .Trim();
    }
}
