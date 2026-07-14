// SPDX-FileCopyrightText: 2026 Dana Buehre
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics.Tensors;
using Jellyfin.Plugin.Gelo.Constants;
using Jellyfin.Plugin.Gelo.Persistence;
using Jellyfin.Plugin.Gelo.Recs;
using Microsoft.Extensions.Logging;
using Microsoft.ML;
using Microsoft.ML.Data;

namespace Jellyfin.Plugin.Gelo.Recs.Tier2;

/// <summary>
/// Per-user trained logistic ranker (ML.NET, pure-managed — no native libs). Trains an L2-regularised
/// logistic model predicting P(engage | item) from the item's MiniLM embedding + scalar metadata + the
/// Tier-1 heuristic affinity as a feature. Positives = engaged interactions (weighted by relevance);
/// negatives = a deterministic per-user sample of non-interacted items (implicit no-engagement).
/// Because the model learns a per-feature weight vector over the embedding, it can out-rank a pure
/// centroid+cosine baseline. When on, RerankPipeline orders shelves by this probability; until a model
/// is trained it transparently falls back to the heuristic.
/// </summary>
internal sealed class Tier2Ranker : ITier2Ranker
{
    private const int NegativeRatio = 5;

    // IO shapes shared by train + predict. Features layout is defined by RankerFeatures.
    private sealed class RankerExample
    {
        [VectorType(RankerFeatures.FeatureDim)]
        public float[] Features { get; set; } = Array.Empty<float>();

        public bool Label { get; set; }

        public float Weight { get; set; } = 1f;
    }

    private sealed class RankerInput
    {
        [VectorType(RankerFeatures.FeatureDim)]
        public float[] Features { get; set; } = Array.Empty<float>();
    }

    private sealed class RankerOutput
    {
        public bool Predicted { get; set; }

        public float Probability { get; set; }
    }

    private sealed class LoadedModel
    {
        public readonly PredictionEngine<RankerInput, RankerOutput> Engine;
        public readonly object Lock = new();

        public LoadedModel(PredictionEngine<RankerInput, RankerOutput> engine) => Engine = engine;
    }

    private readonly VectorStore _store;
    private readonly CentroidBuilder _centroids;
    private readonly ILogger<Tier2Ranker> _logger;
    private readonly MLContext _ml;
    private readonly ConcurrentDictionary<Guid, LoadedModel> _cache = new();

    public Tier2Ranker(VectorStore store, CentroidBuilder centroids, ILogger<Tier2Ranker> logger)
    {
        _store = store;
        _centroids = centroids;
        _logger = logger;
        // Fixed seed → reproducible models for a given interaction set.
        _ml = new MLContext(seed: 1);
    }

    private string ModelDir => Path.Combine(_store.DataDirectory, "ranker");

    private string ModelPath(Guid userId) => Path.Combine(ModelDir, userId + ".zip");

    /// <inheritdoc />
    public bool IsAvailable(Guid userId) => GetOrLoad(userId) is not null;

    /// <inheritdoc />
    public float[]? Score(Guid userId, IReadOnlyList<IndexedItem> items, Dictionary<Guid, float> affinity)
    {
        if (items.Count == 0)
        {
            return Array.Empty<float>();
        }

        var model = GetOrLoad(userId);
        if (model is null)
        {
            return null;
        }

        var result = new float[items.Count];
        lock (model.Lock)
        {
            for (var i = 0; i < items.Count; i++)
            {
                var it = items[i];
                var aff = affinity.TryGetValue(it.Id, out var a) ? a : 0f;
                result[i] = model.Engine.Predict(new RankerInput { Features = RankerFeatures.Build(it, aff) }).Probability;
            }
        }

        return result;
    }

    /// <inheritdoc />
    public void TrainForUser(Guid userId)
    {
        var interactions = _store.GetInteractionsForUser(userId);
        var items = _store.GetAllItems();
        if (items.Count == 0 || interactions.Count == 0)
        {
            Drop(userId);
            return;
        }

        // Heuristic affinity feature = cosine(centroid, itemVec) — the Phase-1 score this model improves.
        var centroid = _centroids.Build(userId).Vector;
        var byId = items.ToDictionary(i => i.Id);
        float Aff(Guid id)
        {
            if (byId.TryGetValue(id, out var it) && it.Vector.Length == centroid.Length)
            {
                return TensorPrimitives.Dot(centroid, it.Vector);
            }

            return 0f;
        }

        var maxPlay = interactions.Max(i => i.PlayCount);
        var interactedIds = new HashSet<Guid>();
        var positives = new List<RankerExample>();
        foreach (var ix in interactions)
        {
            interactedIds.Add(ix.ItemId);
            if (!byId.TryGetValue(ix.ItemId, out var it))
            {
                continue;
            }

            var pcn = Relevance.PlayCountNorm(ix.PlayCount, maxPlay);
            if (!Relevance.IsEngaged(ix.CompletionPct, pcn, ix.IsFavorite))
            {
                continue;
            }

            var rel = (float)Relevance.Recompute(ix, maxPlay);
            if (rel <= 0)
            {
                continue;
            }

            positives.Add(new RankerExample
            {
                Features = RankerFeatures.Build(it, Aff(it.Id)),
                Label = true,
                Weight = Math.Max(rel, 0.05f)
            });
        }

        if (positives.Count < Tuning.MinItemsForTraining)
        {
            _logger.LogInformation("Tier-2 ranker: {Pos} engaged items for {User} (< {Min}); not trained.", positives.Count, userId, Tuning.MinItemsForTraining);
            Drop(userId);
            return;
        }

        // Negatives: deterministic per-user sample of non-interacted items (implicit no-engagement).
        var pool = items.Where(i => !interactedIds.Contains(i.Id)).ToList();
        var negCount = Math.Min(pool.Count, positives.Count * NegativeRatio);
        var rng = new Random((int)((uint)userId.GetHashCode() ^ 0x9E3779B9u));
        var negatives = new List<RankerExample>(negCount);
        var chosen = new HashSet<int>();
        while (negatives.Count < negCount && chosen.Count < pool.Count)
        {
            var idx = rng.Next(pool.Count);
            if (chosen.Add(idx))
            {
                var it = pool[idx];
                negatives.Add(new RankerExample { Features = RankerFeatures.Build(it, Aff(it.Id)), Label = false, Weight = 1f });
            }
        }

        ITransformer model;
        try
        {
            var data = _ml.Data.LoadFromEnumerable(positives.Concat(negatives));
            var trainer = _ml.BinaryClassification.Trainers.LbfgsLogisticRegression(
                labelColumnName: nameof(RankerExample.Label),
                featureColumnName: nameof(RankerExample.Features),
                exampleWeightColumnName: nameof(RankerExample.Weight));
            model = trainer.Fit(data);

            try
            {
                var metrics = _ml.BinaryClassification.Evaluate(model.Transform(data), labelColumnName: nameof(RankerExample.Label));
                _logger.LogInformation("Tier-2 ranker trained for {User}: pos={Pos} neg={Neg} AUC={Auc:F3} acc={Acc:F3}.",
                    userId, positives.Count, negatives.Count, metrics.AreaUnderRocCurve, metrics.Accuracy);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Tier-2 ranker: evaluation skipped for {User}.", userId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tier-2 ranker: training failed for {User}; retaining heuristic.", userId);
            Drop(userId);
            return;
        }

        // Persist + warm the cache straight from the in-memory model (no reload round-trip).
        try
        {
            Directory.CreateDirectory(ModelDir);
            _ml.Model.Save(model, null, ModelPath(userId));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tier-2 ranker: failed to persist model for {User}.", userId);
        }

        try
        {
            var engine = _ml.Model.CreatePredictionEngine<RankerInput, RankerOutput>(model);
            _cache[userId] = new LoadedModel(engine);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tier-2 ranker: prediction engine build failed for {User}.", userId);
            _cache.TryRemove(userId, out _);
        }
    }

    private LoadedModel? GetOrLoad(Guid userId)
    {
        if (_cache.TryGetValue(userId, out var cached))
        {
            return cached;
        }

        var path = ModelPath(userId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var loaded = _ml.Model.Load(path, out _);
            var engine = _ml.Model.CreatePredictionEngine<RankerInput, RankerOutput>(loaded);
            var model = new LoadedModel(engine);
            _cache[userId] = model;
            return model;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Tier-2 ranker: could not load {Path}.", path);
            return null;
        }
    }

    private void Drop(Guid userId)
    {
        _cache.TryRemove(userId, out _);
        try
        {
            var path = ModelPath(userId);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best-effort cache invalidation
        }
    }
}
