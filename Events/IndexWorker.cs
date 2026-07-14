// SPDX-FileCopyrightText: 2026 Dana Buehre
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Gelo.Constants;
using Jellyfin.Plugin.Gelo.Embedding;
using Jellyfin.Plugin.Gelo.Indexing;
using Jellyfin.Plugin.Gelo.Persistence;
using Jellyfin.Plugin.Gelo.Recs;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using BaseItemKind = Jellyfin.Data.Enums.BaseItemKind;

namespace Jellyfin.Plugin.Gelo.Events;

/// <summary>
/// The single ONNX consumer. Reads jobs from <see cref="IndexQueue"/>, throttles to protect
/// CPU on low-power hosts, and persists vectors/interactions + updates the warm cache.
/// </summary>
public sealed class IndexWorker : BackgroundService
{
    private readonly IndexQueue _queue;
    private readonly EmbeddingService _embeddings;
    private readonly VectorStore _store;
    private readonly ILibraryManager _library;
    private readonly IUserDataManager _userData;
    private readonly IUserManager _userManager;
    private readonly ILogger<IndexWorker> _logger;
    private readonly int _maxPerSecond;

    public IndexWorker(
        IndexQueue queue,
        EmbeddingService embeddings,
        VectorStore store,
        ILibraryManager library,
        IUserDataManager userData,
        IUserManager userManager,
        ILogger<IndexWorker> logger)
    {
        _queue = queue;
        _embeddings = embeddings;
        _store = store;
        _library = library;
        _userData = userData;
        _userManager = userManager;
        _logger = logger;
        _maxPerSecond = Math.Max(1, Plugin.Instance?.Configuration.MaxEmbedsPerSecond ?? 4);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var indexingEnabled = Plugin.Instance?.Configuration.EnableIndexing ?? false;
        if (!indexingEnabled)
        {
            // Safe mode: register the queue reader but perform NO native/DB work. The plugin is
            // loaded and APIs respond, but nothing opens SQLite or runs ONNX. This keeps a fresh
            // deploy on a guaranteed-clean pure-managed baseline until EnableIndexing is flipped.
            _logger.LogWarning("IndexWorker idle — EnableIndexing is false (safe mode). No native/DB work will occur.");
            try
            {
                await foreach (var _ in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
                {
                    // drain; jobs are dropped while disabled
                }
            }
            catch (OperationCanceledException) { }
            return;
        }

        try
        {
            var existing = _store.GetAllItems();
            _logger.LogInformation("IndexWorker started; {Count} items currently indexed.", existing.Count);

            // One-time history backfill on first boot: ingest every user's EXISTING watch history so the
            // engine is immediately warm (not cold-start) instead of only seeing live events from now on.
            // Gated by a persisted flag so it doesn't repeat every restart; a FullReindex refreshes it again.
            try
            {
                if (string.IsNullOrEmpty(_store.GetMeta("interactions_backfilled")))
                {
                    await BackfillInteractionsAsync(null, stoppingToken).ConfigureAwait(false);
                    _store.SetMeta("interactions_backfilled", DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Startup interaction backfill failed (will retry on next boot).");
            }

            await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await ProcessAsync(job, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Index job failed: {Job}", job);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }
        finally
        {
            _logger.LogInformation("IndexWorker stopped.");
        }
    }

    private async Task ProcessAsync(IndexJob job, CancellationToken ct)
    {
        switch (job)
        {
            case IndexJob.EmbedItem(var id):
                await EmbedItemAsync(id, ct).ConfigureAwait(false);
                break;
            case IndexJob.EmbedAll:
                await EmbedAllAsync(ct).ConfigureAwait(false);
                break;
            case IndexJob.UpdateInteraction(var uid, var iid):
                ProcessInteraction(uid, iid);
                break;
            case IndexJob.BackfillInteractions(var uid):
                await BackfillInteractionsAsync(uid, ct).ConfigureAwait(false);
                break;
            case IndexJob.RebuildCentroid:
                // Centroids are rebuilt lazily on read (RecommendationService); no-op here.
                break;
        }
    }

    private async Task EmbedItemAsync(Guid id, CancellationToken ct)
    {
        if (id == Guid.Empty)
        {
            return;
        }

        var item = _library.GetItemById(id);
        if (item is null)
        {
            return;
        }

        var people = _library.GetPeople(item);
        var text = MetadataBlockBuilder.Build(item, people);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var vector = _embeddings.Embed(text);
        if (vector.Length == 0)
        {
            return;
        }

        _store.UpsertItem(ItemProjector.Project(item, people, vector));
        _logger.LogDebug("Indexed {Type} '{Name}'.", item.GetType().Name, item.Name);

        await Task.Delay(TimeSpan.FromSeconds(1.0 / _maxPerSecond), ct).ConfigureAwait(false);
    }

    private async Task EmbedAllAsync(CancellationToken ct)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
            Recursive = true,
            IsVirtualItem = false
        };
        var items = _library.GetItemList(query);
        _logger.LogInformation("Full reindex starting for {Count} items.", items.Count);

        var batchSize = Math.Max(1, Plugin.Instance?.Configuration.BatchSize ?? 16);
        var perItem = 1.0 / _maxPerSecond;

        for (var i = 0; i < items.Count; i += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = items.Skip(i).Take(batchSize).ToList();
            var texts = batch
                .Select(it => MetadataBlockBuilder.Build(it, _library.GetPeople(it)))
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();
            if (texts.Count == 0)
            {
                continue;
            }

            var vectors = _embeddings.EmbedBatch(texts);
            for (var j = 0; j < vectors.Count; j++)
            {
                if (vectors[j].Length == 0)
                {
                    continue;
                }

                var item = batch[j];
                _store.UpsertItem(ItemProjector.Project(item, _library.GetPeople(item), vectors[j]));
            }

            await Task.Delay(TimeSpan.FromSeconds(batch.Count * perItem), ct).ConfigureAwait(false);
        }

        _store.SetMeta("last_full_reindex", DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
        _logger.LogInformation("Full reindex complete; {Count} items processed.", items.Count);

        // Refresh interactions from current UserData too — picks up history that predates plugin
        // install or changes made outside playback events (manual marks, favorites, imports).
        await BackfillInteractionsAsync(null, ct).ConfigureAwait(false);
    }

    private void ProcessInteraction(Guid userId, Guid itemId)
    {
        if (userId == Guid.Empty || itemId == Guid.Empty)
        {
            return;
        }

        var item = _library.GetItemById(itemId);
        if (item is null)
        {
            return;
        }

        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return;
        }

        var data = _userData.GetUserData(user, item);
        if (data is null)
        {
            return;
        }

        var halfLife = Plugin.Instance?.Configuration.DecayHalfLifeDays ?? Tuning.DecayHalfLifeDays;
        // playCountNorm is per-user (normalized against the user's peak play count). The stored
        // RelevanceScore here is a best-effort snapshot; the read path (centroids/shelves) always
        // recomputes it via Relevance.Recompute with the live max, so it stays correct as new
        // interactions arrive even if this stored value goes briefly stale.
        var existing = _store.GetInteractionsForUser(userId);
        var maxPlayCount = Math.Max(data.PlayCount, existing.Count > 0 ? existing.Max(i => i.PlayCount) : 0);

        var record = BuildInteractionRecord(userId, item, data, halfLife, maxPlayCount);
        if (record is not null)
        {
            _store.UpsertInteraction(record);
        }
    }

    /// <summary>
    /// Builds an <see cref="InteractionRecord"/> from a single item's UserData. Returns null when the
    /// item carries no engagement signal so the interactions table stays lean (only meaningful rows).
    /// Shared by the live-event path (<see cref="ProcessInteraction"/>) and the history backfill
    /// (<see cref="BackfillInteractionsAsync"/>).
    /// </summary>
    private static InteractionRecord? BuildInteractionRecord(
        Guid userId,
        BaseItem item,
        UserItemData data,
        double halfLife,
        int maxPlayCount)
    {
        // Skip items with zero engagement signal — keeps the table to meaningful rows only.
        if (!data.Played && data.PlayCount <= 0 && data.PlaybackPositionTicks <= 0 && !data.IsFavorite)
        {
            return null;
        }

        var runtimeTicks = item.RunTimeTicks ?? 0;
        // Completion: Jellyfin zeroes the resume position once a movie finishes, so keying purely
        // off PlaybackPositionTicks would report a fully-watched film as 0%. Resolve it directly:
        // a Played item is 100%.
        double completion;
        if (data.Played)
        {
            completion = 1.0;
        }
        else if (runtimeTicks > 0 && data.PlaybackPositionTicks > 0)
        {
            completion = Math.Min(1.0, (double)data.PlaybackPositionTicks / runtimeTicks);
        }
        else
        {
            completion = 0.0;
        }

        var lastPlayed = data.LastPlayedDate;
        var decay = Relevance.TemporalDecay(lastPlayed, halfLife, completion);
        var playCountNorm = Relevance.PlayCountNorm(data.PlayCount, maxPlayCount);

        // Server-side we have no "detail-page dwell" signal (that is a client-only concept). Completion
        // is the closest engagement proxy available, so reuse it.
        var dwellNorm = completion;

        return new InteractionRecord
        {
            UserId = userId,
            ItemId = item.Id,
            PlayCount = data.PlayCount,
            LastPlayedTicks = lastPlayed?.Ticks,
            Played = data.Played,
            IsFavorite = data.IsFavorite,
            CompletionPct = completion,
            DwellNorm = dwellNorm,
            TemporalDecay = decay,
            RelevanceScore = Relevance.Score(completion, playCountNorm, data.IsFavorite, dwellNorm, decay),
            UpdatedAtTicks = DateTime.UtcNow.Ticks
        };
    }

    /// <summary>
    /// Seeds interactions for one user (or all users) from their EXISTING Jellyfin UserData. This is the
    /// bridge that makes a freshly-installed plugin aware of a user's full watch history instead of only
    /// catching live playback events. Fast (no embedding); idempotent (upsert). After it runs, lazy
    /// centroid rebuilds (RecommendationService.GetCentroid) pick up the new interactions automatically.
    /// </summary>
    private async Task BackfillInteractionsAsync(Guid? scopeUserId, CancellationToken ct)
    {
        // Target one user, or every user the server knows about.
        var users = _userManager.GetUsers().ToList();
        if (scopeUserId.HasValue && scopeUserId.Value != Guid.Empty)
        {
            users = users.Where(u => u.Id == scopeUserId.Value).ToList();
        }

        if (users.Count == 0)
        {
            _logger.LogInformation("Interaction backfill: no users to process.");
            return;
        }

        // Iterate the same Movie/Series set the indexer embeds (the embeddable level interactions key on).
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
            Recursive = true,
            IsVirtualItem = false
        };
        var items = _library.GetItemList(query);
        var halfLife = Plugin.Instance?.Configuration.DecayHalfLifeDays ?? Tuning.DecayHalfLifeDays;

        _logger.LogInformation("Interaction backfill starting for {UserCount} user(s) over {Count} items.", users.Count, items.Count);

        var totalStored = 0;
        foreach (var user in users)
        {
            ct.ThrowIfCancellationRequested();

            // First pass: find the user's peak play count so the stored relevance snapshot is reasonable
            // (the read path recomputes anyway, but a sane stored value avoids transient skew).
            var peakPlayCount = 0;
            foreach (var item in items)
            {
                var d = _userData.GetUserData(user, item);
                if (d is not null && d.PlayCount > peakPlayCount)
                {
                    peakPlayCount = d.PlayCount;
                }
            }

            // Second pass: store a row for every item with an engagement signal.
            var stored = 0;
            foreach (var item in items)
            {
                var data = _userData.GetUserData(user, item);
                if (data is null)
                {
                    continue;
                }

                var record = BuildInteractionRecord(user.Id, item, data, halfLife, peakPlayCount);
                if (record is null)
                {
                    continue;
                }

                _store.UpsertInteraction(record);
                stored++;
            }

            totalStored += stored;
            _logger.LogInformation("Interaction backfill: user '{User}' → {Count} interaction(s).", user.Username, stored);
            await Task.Yield();
        }

        _logger.LogInformation("Interaction backfill complete: stored/refreshed {Total} interaction(s) across {UserCount} user(s).", totalStored, users.Count);
    }
}
