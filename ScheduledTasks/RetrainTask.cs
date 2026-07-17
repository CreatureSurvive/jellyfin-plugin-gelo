// SPDX-FileCopyrightText: 2026 Dana Buehre
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Gelo.Persistence;
using Jellyfin.Plugin.Gelo.Recs;
using Jellyfin.Plugin.Gelo.Recs.Tier2;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Gelo.ScheduledTasks;

/// <summary>
/// Periodic "retrain" pass, kept as its own scheduled task (separate from the full reindex) so model
/// refresh runs on a faster cadence than re-embedding. It rebuilds every user's taste vector (centroid)
/// from scratch — this is what makes a relevance-weight or decay-half-life change take effect for all
/// users immediately, and pre-warms centroids so the first home load is instant — and, when the trained
/// ranker is enabled, retrains each user's ML.NET model here too (gated on EnableTier2Ranker).
/// </summary>
public sealed class RetrainTask : IScheduledTask
{
    private readonly VectorStore _store;
    private readonly CentroidBuilder _centroids;
    private readonly ITier2Ranker _ranker;
    private readonly ILogger<RetrainTask> _logger;

    public RetrainTask(VectorStore store, CentroidBuilder centroids, ITier2Ranker ranker, ILogger<RetrainTask> logger)
    {
        _store = store;
        _centroids = centroids;
        _ranker = ranker;
        _logger = logger;
    }

    public string Name => "Retrain user models";

    public string Key => "GeloRetrain";

    public string Description =>
        "Rebuilds every user's taste vector (centroid) from current history so relevance/decay " +
        "changes apply to everyone immediately. Phase 2 will also train the recommendation ranker here.";

    public string Category => "Gelo Recommendations";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration is { EnableIndexing: false })
        {
            _logger.LogInformation("Retrain skipped: indexing disabled (safe mode).");
            progress.Report(100);
            return;
        }

        var userIds = _store.GetAllUserIds();
        _logger.LogInformation("Retrain: rebuilding centroids for {Count} user(s).", userIds.Count);

        var rebuilt = 0;
        var rankers = 0;
        var trainRanker = Plugin.Instance?.Configuration?.EnableTier2Ranker ?? false;
        for (var i = 0; i < userIds.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var userId = userIds[i];
            try
            {
                // Build() recomputes relevance from the CURRENT configured weights + half-life, so a
                // config change is reflected for this user once the centroid is persisted + cached.
                var centroid = _centroids.Build(userId);
                _store.SetCentroid(centroid);
                rebuilt++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Retrain: failed to rebuild centroid for user {UserId}.", userId);
            }

            // Phase-2: (re)train the per-user logistic ranker from this user's current interactions.
            if (trainRanker)
            {
                try
                {
                    _ranker.TrainForUser(userId);
                    rankers++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Retrain: Phase-2 ranker training failed for user {UserId}.", userId);
                }
            }

            progress.Report(100.0 * (i + 1) / Math.Max(1, userIds.Count));

            // Yield periodically so the scheduled-task scheduler stays responsive on big libraries.
            if (i > 0 && i % 64 == 0)
            {
                await Task.Yield();
            }
        }

        if (trainRanker)
        {
            _logger.LogInformation("Retrain complete: rebuilt {Rebuilt}/{Total} centroid(s); trained {Rankers} Phase-2 ranker(s).", rebuilt, userIds.Count, rankers);
        }
        else
        {
            _logger.LogInformation("Retrain complete: rebuilt {Rebuilt}/{Total} centroid(s).", rebuilt, userIds.Count);
        }

        progress.Report(100);
    }

    /// <summary>Daily, 03:00 local — off-peak model refresh (after the new-content scan/reindex have
    /// picked up the day's additions). Editable in the dashboard.</summary>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = new TimeSpan(3, 0, 0).Ticks,
            MaxRuntimeTicks = TimeSpan.FromMinutes(30).Ticks
        };
    }
}
