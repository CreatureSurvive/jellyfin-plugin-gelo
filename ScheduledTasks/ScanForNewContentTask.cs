// SPDX-FileCopyrightText: 2026 Dana Buehre
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Gelo.Events;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Gelo.ScheduledTasks;

/// <summary>
/// Frequent, cheap new-content catch-up. Enqueues an <see cref="IndexJob.EmbedMissing"/> onto the
/// single-worker queue, which embeds ONLY movies/shows not already in the store. This is the safety
/// net for items the real-time library event hooks can miss — media added while the server was down,
/// bulk library operations, or items present before the plugin was installed — and runs far more often
/// than the full reindex while doing a fraction of the work when the library is stable. Appears under
/// Dashboard → Scheduled Tasks → "Gelo Recommendations".
/// </summary>
public sealed class ScanForNewContentTask : IScheduledTask
{
    private readonly IndexQueue _queue;
    private readonly ILogger<ScanForNewContentTask> _logger;

    public ScanForNewContentTask(IndexQueue queue, ILogger<ScanForNewContentTask> logger)
    {
        _queue = queue;
        _logger = logger;
    }

    public string Name => "Scan for new content";

    public string Key => "GeloScanNewContent";

    public string Description =>
        "Embed movies and shows that aren't in the Gelo store yet — catches items added while the " +
        "server was down or that bypassed the real-time hooks. Cheap (only missing items are embedded).";

    public string Category => "Gelo Recommendations";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration is { EnableIndexing: false })
        {
            _logger.LogInformation("New-content scan skipped: indexing disabled (safe mode).");
            progress.Report(100);
            return Task.CompletedTask;
        }

        _logger.LogInformation("New-content scan requested; enqueuing EmbedMissing job.");
        _queue.TryWrite(new IndexJob.EmbedMissing());
        progress.Report(100);
        return Task.CompletedTask;
    }

    /// <summary>Every 3 hours — frequent new-content discovery without the cost of a full re-embed.
    /// Editable in the dashboard.</summary>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(3).Ticks,
            MaxRuntimeTicks = TimeSpan.FromMinutes(30).Ticks
        };
    }
}
