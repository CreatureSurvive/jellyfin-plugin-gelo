// SPDX-FileCopyrightText: 2026 Dana Buehre
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Gelo.Events;

/// <summary>
/// Subscribes to playback/user-rating saves and enqueues interaction updates. Filters to
/// meaningful reasons so routine metadata tweaks don't trigger recomputation.
/// </summary>
public sealed class UserDataService : IHostedService
{
    private readonly IUserDataManager _userData;
    private readonly IndexQueue _queue;
    private readonly ILogger<UserDataService> _logger;

    public UserDataService(IUserDataManager userData, IndexQueue queue, ILogger<UserDataService> logger)
    {
        _userData = userData;
        _queue = queue;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!(Plugin.Instance?.Configuration.EnableIndexing ?? false))
        {
            _logger.LogWarning("User-data event hooks NOT attached — EnableIndexing is false (safe mode).");
            return Task.CompletedTask;
        }

        _userData.UserDataSaved += OnSaved;
        _logger.LogInformation("User-data event hooks attached.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _userData.UserDataSaved -= OnSaved;
        return Task.CompletedTask;
    }

    private void OnSaved(object? sender, UserDataSaveEventArgs e)
    {
        var reason = e.SaveReason;
        // Skip PlaybackProgress (fires every few seconds) — capture completions, manual marks, ratings.
        if (reason != UserDataSaveReason.PlaybackFinished
            && reason != UserDataSaveReason.TogglePlayed
            && reason != UserDataSaveReason.UpdateUserRating)
        {
            return;
        }

        if (e.UserId == Guid.Empty || e.Item is null)
        {
            return;
        }

        // Key interactions at the embeddable level (series for TV) so they align with vectors.
        var targetId = ResolveEmbeddableId(e.Item);
        if (targetId == Guid.Empty)
        {
            return;
        }

        _queue.TryWrite(new IndexJob.UpdateInteraction(e.UserId, targetId));
    }

    private static Guid ResolveEmbeddableId(BaseItem item)
        => item switch
        {
            Episode ep => ep.SeriesId,
            Season s when s.SeriesId != Guid.Empty => s.SeriesId,
            _ => item.Id
        };
}
