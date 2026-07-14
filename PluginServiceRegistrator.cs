// SPDX-FileCopyrightText: 2026 Dana Buehre
// SPDX-License-Identifier: GPL-3.0-or-later

using Jellyfin.Plugin.Gelo.Embedding;
using Jellyfin.Plugin.Gelo.Events;
using Jellyfin.Plugin.Gelo.Persistence;
using Jellyfin.Plugin.Gelo.Recs;
using Jellyfin.Plugin.Gelo.Recs.Tier2;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Gelo;

/// <summary>
/// Registers plugin services with the Jellyfin DI container. Discovered by the host at startup.
/// Controllers in this assembly are auto-discovered separately (no registration here).
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Shared stateful singletons. VectorStore takes IApplicationPaths via constructor injection
        // (registered by core) so the DB lands at /config/data/gelo/, outside the plugins dir.
        serviceCollection.AddSingleton<IndexQueue>();
        serviceCollection.AddSingleton<EmbeddingService>();
        serviceCollection.AddSingleton<VectorStore>();
        serviceCollection.AddSingleton<CentroidBuilder>();
        serviceCollection.AddSingleton<TasteProfileBuilder>();
        serviceCollection.AddSingleton<RecommendationService>();
        serviceCollection.AddSingleton<ITier2Ranker, Tier2Ranker>();

        // Background lifecycle: event subscribers + the single ONNX consumer. Each one no-ops
        // cleanly while EnableIndexing is false (the safe-mode default), so a fresh deploy boots
        // without touching any native lib.
        serviceCollection.AddHostedService<IndexWorker>();
        serviceCollection.AddHostedService<LibraryEventService>();
        serviceCollection.AddHostedService<UserDataService>();

        // Scheduled tasks (Dashboard → Scheduled Tasks). Discovered as IEnumerable<IScheduledTask>.
        serviceCollection.AddSingleton<MediaBrowser.Model.Tasks.IScheduledTask, ScheduledTasks.FullReindexTask>();
        serviceCollection.AddSingleton<MediaBrowser.Model.Tasks.IScheduledTask, ScheduledTasks.RetrainTask>();
    }
}
