// SPDX-FileCopyrightText: 2026 Dana Buehre
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Threading.Channels;

namespace Jellyfin.Plugin.Gelo.Events;

/// <summary>Discriminated work items consumed by the single ONNX worker thread.</summary>
public abstract record IndexJob
{
    /// <summary>Embed (or re-embed) one item; episodes are resolved to their parent series first.</summary>
    public sealed record EmbedItem(Guid ItemId) : IndexJob;

    /// <summary>Signal a full-library reindex (e.g. after a model/template change).</summary>
    public sealed record EmbedAll : IndexJob;

    /// <summary>Embed only items not yet in the store — a cheap new-content catch-up (vs. <see cref="EmbedAll"/>).</summary>
    public sealed record EmbedMissing : IndexJob;

    /// <summary>Upsert a (user, item) interaction and recompute decay/relevance.</summary>
    public sealed record UpdateInteraction(Guid UserId, Guid ItemId) : IndexJob;

    /// <summary>Recompute a user's centroid from their current interactions.</summary>
    public sealed record RebuildCentroid(Guid UserId) : IndexJob;

    /// <summary>
    /// Seed interactions from each user's EXISTING Jellyfin UserData (played / play count / resume /
    /// favorite). <c>null</c> = all users. Fast (no embedding) — this is what makes a freshly-installed
    /// plugin immediately aware of a user's full watch history instead of only catching live events.
    /// </summary>
    public sealed record BackfillInteractions(Guid? UserId) : IndexJob;
}

/// <summary>
/// Bounded single-reader queue decoupling library/user-data events from the ONNX worker.
/// Writers are fire-and-forget (never block the server's event thread).
/// </summary>
public sealed class IndexQueue
{
    private readonly Channel<IndexJob> _channel = Channel.CreateBounded<IndexJob>(
        new BoundedChannelOptions(10000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    public ChannelReader<IndexJob> Reader => _channel.Reader;

    public bool TryWrite(IndexJob job) => _channel.Writer.TryWrite(job);
}
