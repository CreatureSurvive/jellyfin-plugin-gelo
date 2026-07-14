// SPDX-FileCopyrightText: 2026 Dana Buehre
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Gelo.Api;

public sealed record SimilarItemDto(Guid Id, string? Name, string? Type, float Score);

public sealed record ShelfItemDto(Guid Id, string? Name, string? Type, float Score, string? Reason);

public sealed record ShelfDto(string Title, string Paradigm, List<ShelfItemDto> Items);

public sealed record StatusDto(
    int ItemCount,
    int Dimension,
    bool EmbeddingsReady,
    string? LastFullReindex,
    string? ModelId);

/// <summary>
/// Lightweight readiness probe for ANY authenticated user (no admin elevation). Lets a regular
/// client detect that Gelo is installed, enabled, and has finished indexing, without hitting the
/// admin-only <c>/Status</c> endpoint. <see cref="Ready"/> is true only when the engine is enabled,
/// embeddings are loaded, and at least one item is indexed.
/// </summary>
public sealed record PingDto(bool Enabled, bool EmbeddingsReady, int ItemCount, bool Ready);

/// <summary>Body for POST .../Feedback. <c>Kind</c> is "more" or "less".</summary>
public sealed record FeedbackRequest(Guid ItemId, string Kind);

/// <summary>One feedback vote, returned by GET .../Feedback.</summary>
public sealed record FeedbackEntryDto(Guid ItemId, string Kind);
