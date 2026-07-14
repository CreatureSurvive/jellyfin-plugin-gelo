// SPDX-FileCopyrightText: 2026 Dana Buehre
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Gelo.Events;

/// <summary>
/// Subscribes to library item add/update events and enqueues embedding jobs. Returns
/// immediately (never blocks the server's event thread) — work runs on <see cref="IndexWorker"/>.
/// </summary>
public sealed class LibraryEventService : IHostedService
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(60);

    private readonly ILibraryManager _library;
    private readonly IndexQueue _queue;
    private readonly ILogger<LibraryEventService> _logger;
    private readonly ConcurrentDictionary<Guid, long> _lastEnqueue = new();

    public LibraryEventService(ILibraryManager library, IndexQueue queue, ILogger<LibraryEventService> logger)
    {
        _library = library;
        _queue = queue;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!(Plugin.Instance?.Configuration.EnableIndexing ?? false))
        {
            _logger.LogWarning("Library event hooks NOT attached — EnableIndexing is false (safe mode).");
            return Task.CompletedTask;
        }

        _library.ItemAdded += OnChange;
        _library.ItemUpdated += OnChange;
        _logger.LogInformation("Library event hooks attached.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _library.ItemAdded -= OnChange;
        _library.ItemUpdated -= OnChange;
        return Task.CompletedTask;
    }

    private void OnChange(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is null)
        {
            return;
        }

        if (!ShouldEmbed(e.Item, out var targetId))
        {
            return;
        }

        var now = DateTime.UtcNow.Ticks;
        if (_lastEnqueue.TryGetValue(targetId, out var last) && now - last < Cooldown.Ticks)
        {
            return; // collapse repeated updates for the same item within the cooldown window
        }

        _lastEnqueue[targetId] = now;
        _queue.TryWrite(new IndexJob.EmbedItem(targetId));
    }

    /// <summary>Only top-level embeddable items trigger inference; episodes/seasons resolve to their Series.</summary>
    internal static bool ShouldEmbed(BaseItem item, out Guid targetId)
    {
        switch (item)
        {
            case Episode ep:
                targetId = ep.SeriesId;
                return targetId != Guid.Empty;
            case Season season when season.SeriesId != Guid.Empty:
                targetId = season.SeriesId;
                return true;
            case Movie:
            case Series:
                targetId = item.Id;
                return true;
            default:
                targetId = Guid.Empty;
                return false;
        }
    }
}
