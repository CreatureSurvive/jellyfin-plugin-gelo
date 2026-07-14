// SPDX-FileCopyrightText: 2026 Dana Buehre
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Gelo.Embedding;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Gelo.Events;

/// <summary>
/// Warms up the ONNX embedding session shortly after startup so <see cref="EmbeddingService.IsReady"/>
/// reflects reality. The session is otherwise loaded lazily inside the first embed call, which on a
/// stable library may never come between restarts — leaving <c>IsReady</c> (and the <c>EmbeddingsReady</c>
/// field surfaced by <c>/Ping</c>) permanently false even though serving works from cached vectors.
/// The init runs on a background thread so it never blocks server startup, and is skipped while
/// indexing is disabled (the safe-mode switch that avoids touching native libs).
/// </summary>
public sealed class EmbeddingWarmupService : IHostedService
{
    private readonly EmbeddingService _embeddings;
    private readonly ILogger<EmbeddingWarmupService> _logger;

    public EmbeddingWarmupService(EmbeddingService embeddings, ILogger<EmbeddingWarmupService> logger)
    {
        _embeddings = embeddings;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.EnablePlugin || !cfg.EnableIndexing)
        {
            return Task.CompletedTask;
        }

        // Fire-and-forget: load the model off the startup path. IsReady flips true within ~1-2s of boot.
        _ = Task.Run(
            () =>
            {
                try
                {
                    _embeddings.EnsureInitialized();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Embedding warmup failed; it will retry on the first embed.");
                }
            },
            cancellationToken);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
