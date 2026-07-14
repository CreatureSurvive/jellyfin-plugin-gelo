// SPDX-FileCopyrightText: 2026 Dana Buehre
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.Gelo.Constants;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Jellyfin.Plugin.Gelo.Embedding;

/// <summary>
/// Tier 1: runs all-MiniLM-L6-v2 ONNX inference to produce L2-normalised 384-dim embeddings.
/// Initialisation is lazy and the session is only ever touched from the single background
/// worker thread (no cross-thread ONNX calls). Output handling adapts to whether the export
/// ships token-level <c>last_hidden_state</c> (mean-pooled here) or an already-pooled vector.
/// </summary>
public sealed class EmbeddingService : IDisposable
{
    private readonly ILogger<EmbeddingService> _logger;
    private readonly int _dim;
    private readonly int _maxLen;
    private readonly int _batchSize;

    private readonly object _initLock = new();
    private BertTokenizer? _tokenizer;
    private InferenceSession? _session;
    private int _initState; // 0 uninit, 1 initializing, 2 ready, 3 failed

    // Resolved model I/O.
    private string _inputIds = "input_ids";
    private string _attentionMask = "attention_mask";
    private string _tokenTypeIds = "token_type_ids";
    private string _outputName = "last_hidden_state";
    private bool _outputIsPooled;
    private int _outputDim = Tuning.EmbeddingDim;

    public EmbeddingService(ILogger<EmbeddingService> logger)
    {
        _logger = logger;
        var cfg = Plugin.Instance?.Configuration;
        _dim = cfg?.EmbeddingDim ?? Tuning.EmbeddingDim;
        _maxLen = cfg?.MaxSequenceLength ?? Tuning.MaxSequenceLength;
        _batchSize = Math.Max(1, cfg?.BatchSize ?? 16);
    }

    public int Dimension => _dim;

    public bool IsReady => Volatile.Read(ref _initState) == 2;

    public bool EnsureInitialized()
    {
        var state = Volatile.Read(ref _initState);
        if (state == 2)
        {
            return true;
        }

        if (state == 3)
        {
            return false;
        }

        lock (_initLock)
        {
            if (_initState == 2)
            {
                return true;
            }

            if (_initState == 3)
            {
                return false;
            }

            _initState = 1;
            try
            {
                NativeProbing.RegisterKnown();
                NativeProbing.Register(typeof(InferenceSession).Assembly);
                Init();
                _initState = 2;
                _logger.LogInformation(
                    "EmbeddingService ready (dim={Dim}, vocab={Vocab}, output={Output}, pooled={Pooled})",
                    _dim, _tokenizer!.VocabSize, _outputName, _outputIsPooled);
                return true;
            }
            catch (Exception ex)
            {
                _initState = 3;
                _logger.LogError(ex, "EmbeddingService initialisation failed; embeddings unavailable.");
                return false;
            }
        }
    }

    private void Init()
    {
        var cfg = Plugin.Instance?.Configuration;
        var pluginDir = Path.GetDirectoryName(GetType().Assembly.Location)
                        ?? throw new InvalidOperationException("Cannot resolve plugin directory.");
        var assetsDir = Path.Combine(pluginDir, "Assets");

        var modelPath = !string.IsNullOrWhiteSpace(cfg?.ModelPath)
            ? cfg!.ModelPath
            : Path.Combine(assetsDir, "model.onnx");
        var vocabPath = !string.IsNullOrWhiteSpace(cfg?.VocabPath)
            ? cfg!.VocabPath
            : Path.Combine(assetsDir, "vocab.txt");

        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("Embedding model not found.", modelPath);
        }

        if (!File.Exists(vocabPath))
        {
            throw new FileNotFoundException("Tokenizer vocab not found.", vocabPath);
        }

        _tokenizer = BertTokenizer.Load(vocabPath);

        var options = new SessionOptions();
        ConfigureExecutionProviders(options, cfg?.ExecutionProviderOrder ?? "CPU");
        _session = new InferenceSession(modelPath, options);
        ResolveIo(_session);
    }

    private void ConfigureExecutionProviders(SessionOptions options, string order)
    {
        var eps = order.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var added = false;
        foreach (var ep in eps)
        {
            try
            {
                switch (ep.ToUpperInvariant())
                {
                    case "CPU":
                        added = true;
                        break;
                    case "CUDA":
                        options.AppendExecutionProvider_CUDA(0);
                        added = true;
                        _logger.LogInformation("ONNX CUDA execution provider appended.");
                        break;
                    case "OPENVINO":
                        options.AppendExecutionProvider("OpenVINO");
                        added = true;
                        _logger.LogInformation("ONNX OpenVINO execution provider appended.");
                        break;
                    case "DIRECTML":
                        options.AppendExecutionProvider_DML(0);
                        added = true;
                        _logger.LogInformation("ONNX DirectML execution provider appended.");
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Execution provider {Provider} unavailable; continuing.", ep);
            }
        }

        if (!added)
        {
            _logger.LogInformation("Using CPU ONNX execution provider (default).");
        }
    }

    private void ResolveIo(InferenceSession session)
    {
        var inputs = session.InputMetadata;
        _inputIds = PickName(inputs.Keys, "input_ids", "inputIds", "input");
        _attentionMask = PickName(inputs.Keys, "attention_mask", "attentionMask", "mask");
        _tokenTypeIds = inputs.Keys.Contains("token_type_ids") ? "token_type_ids" : string.Empty;

        var outputs = session.OutputMetadata;
        _outputName = outputs.Keys.FirstOrDefault()
                      ?? throw new InvalidOperationException("Model exposes no outputs.");

        var meta = outputs[_outputName];
        var dims = meta.Dimensions;
        _outputIsPooled = dims.Length == 2;
        if (dims.Length >= 1 && dims[^1] > 0)
        {
            _outputDim = dims[^1];
        }
    }

    private static string PickName(IEnumerable<string> keys, params string[] preferred)
    {
        var list = keys as IList<string> ?? keys.ToList();
        foreach (var p in preferred)
        {
            if (list.Contains(p))
            {
                return p;
            }
        }

        return list[0];
    }

    public float[] Embed(string text)
    {
        if (!EnsureInitialized())
        {
            return Array.Empty<float>();
        }

        var batch = EmbedBatch(new[] { text });
        return batch.Count > 0 ? batch[0] : Array.Empty<float>();
    }

    public IReadOnlyList<float[]> EmbedBatch(IReadOnlyList<string> texts)
    {
        if (!EnsureInitialized() || _session is null || _tokenizer is null)
        {
            return Array.Empty<float[]>();
        }

        if (texts.Count == 0)
        {
            return Array.Empty<float[]>();
        }

        var count = texts.Count;
        var tokenLists = new List<int>[count];
        var maskLists = new List<int>[count];
        var maxLen = 0;
        for (var i = 0; i < count; i++)
        {
            var ids = new List<int>(_maxLen);
            var mask = new List<int>(_maxLen);
            _tokenizer.Encode(texts[i], _maxLen, ids, mask);
            tokenLists[i] = ids;
            maskLists[i] = mask;
            if (ids.Count > maxLen)
            {
                maxLen = ids.Count;
            }
        }

        var idsArr = new long[count * maxLen];
        var maskArr = new long[count * maxLen];
        for (var i = 0; i < count; i++)
        {
            var row = i * maxLen;
            var ids = tokenLists[i];
            var mask = maskLists[i];
            for (var j = 0; j < ids.Count; j++)
            {
                idsArr[row + j] = ids[j];
                maskArr[row + j] = mask[j];
            }
        }

        var idsTensor = new DenseTensor<long>(idsArr, new[] { count, maxLen }, false);
        var maskTensor = new DenseTensor<long>(maskArr, new[] { count, maxLen }, false);

        var inputs = new List<NamedOnnxValue>(3)
        {
            NamedOnnxValue.CreateFromTensor(_inputIds, idsTensor),
            NamedOnnxValue.CreateFromTensor(_attentionMask, maskTensor)
        };
        if (!string.IsNullOrEmpty(_tokenTypeIds))
        {
            var typeTensor = new DenseTensor<long>(new long[count * maxLen], new[] { count, maxLen }, false);
            inputs.Add(NamedOnnxValue.CreateFromTensor(_tokenTypeIds, typeTensor));
        }

        using var results = _session.Run(inputs);
        var outTensor = results.First(o => o.Name == _outputName).AsTensor<float>();
        var arr = outTensor.ToArray();

        var vectors = new float[count][];
        var dim = _outputDim;
        if (_outputIsPooled)
        {
            for (var i = 0; i < count; i++)
            {
                var v = new float[dim];
                Array.Copy(arr, i * dim, v, 0, dim);
                Normalize(v);
                vectors[i] = v;
            }
        }
        else
        {
            var seq = arr.Length / (count * dim);
            for (var i = 0; i < count; i++)
            {
                var v = new float[dim];
                double denom = 0;
                var mask = maskLists[i];
                for (var s = 0; s < seq; s++)
                {
                    var m = s < mask.Count ? mask[s] : 0;
                    if (m == 0)
                    {
                        continue;
                    }

                    denom += m;
                    var baseIdx = (i * seq + s) * dim;
                    for (var d = 0; d < dim; d++)
                    {
                        v[d] += arr[baseIdx + d];
                    }
                }

                var inv = denom > 0 ? 1.0 / denom : 0;
                for (var d = 0; d < dim; d++)
                {
                    v[d] = (float)(v[d] * inv);
                }

                Normalize(v);
                vectors[i] = v;
            }
        }

        return vectors;
    }

    private static void Normalize(float[] v)
    {
        double sum = 0;
        foreach (var f in v)
        {
            sum += (double)f * f;
        }

        var norm = Math.Sqrt(sum);
        if (norm <= 0)
        {
            return;
        }

        var inv = 1.0 / norm;
        for (var i = 0; i < v.Length; i++)
        {
            v[i] = (float)(v[i] * inv);
        }
    }

    public void Dispose()
    {
        _session?.Dispose();
    }
}
