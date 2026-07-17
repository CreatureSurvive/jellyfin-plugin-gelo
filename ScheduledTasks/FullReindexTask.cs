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
/// Full-library reindex. Enqueues an <see cref="IndexJob.EmbedAll"/> onto the single-worker queue so
/// ONNX stays single-threaded (the worker does the actual embedding). Incremental event hooks handle
/// day-to-day library churn; this scheduled pass catches anything missed and re-embeds after a model
/// or text-template change. Appears under Dashboard → Scheduled Tasks → "Gelo Recommendations".
/// </summary>
public sealed class FullReindexTask : IScheduledTask
{
    private readonly IndexQueue _queue;
    private readonly ILogger<FullReindexTask> _logger;

    public FullReindexTask(IndexQueue queue, ILogger<FullReindexTask> logger)
    {
        _queue = queue;
        _logger = logger;
    }

    public string Name => "Full library reindex";

    public string Key => "GeloFullReindex";

    public string Description => "Re-embed every movie and show into the Gelo vector store from scratch.";

    public string Category => "Gelo Recommendations";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration is { EnableIndexing: false })
        {
            _logger.LogInformation("Full reindex skipped: indexing disabled (safe mode).");
            progress.Report(100);
            return Task.CompletedTask;
        }

        _logger.LogInformation("Full reindex requested; enqueuing EmbedAll job.");
        _queue.TryWrite(new IndexJob.EmbedAll());
        progress.Report(100);
        return Task.CompletedTask;
    }

    /// <summary>Weekly, Sunday 02:00 local — a heavier catch-up that re-embeds after a model/template
    /// change (day-to-day new content is handled by the every-3h "Scan for new content" task). Editable
    /// in the dashboard.</summary>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.WeeklyTrigger,
            TimeOfDayTicks = new TimeSpan(2, 0, 0).Ticks,
            DayOfWeek = DayOfWeek.Sunday,
            MaxRuntimeTicks = TimeSpan.FromHours(1).Ticks
        };
    }
}

