// SPDX-FileCopyrightText: 2026 Dana Buehre
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Jellyfin.Plugin.Gelo.Constants;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Gelo.Persistence;

/// <summary>
/// SQLite-backed vector store with a warm in-memory cache (the API's sub-ms read path).
/// Single-writer (the ONNX worker + scheduled tasks) serialised through <see cref="_dbLock"/>;
/// readers hit the concurrent in-memory dictionaries.
/// </summary>
public sealed class VectorStore
{
    private const string SchemaSql = """
        PRAGMA journal_mode=WAL;
        CREATE TABLE IF NOT EXISTS items (
            item_id TEXT PRIMARY KEY,
            item_type TEXT NOT NULL,
            parent_id TEXT,
            vector BLOB NOT NULL,
            name TEXT,
            year INTEGER,
            runtime_ticks INTEGER,
            genres TEXT,
            tags TEXT,
            studios TEXT,
            community_rating REAL,
            official_rating TEXT,
            tmdb_id TEXT,
            director_names TEXT,
            cast_names TEXT,
            composer_names TEXT,
            writer_names TEXT,
            date_created_ticks INTEGER,
            updated_at INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS interactions (
            user_id TEXT NOT NULL,
            item_id TEXT NOT NULL,
            play_count INTEGER NOT NULL,
            last_played_ticks INTEGER,
            played INTEGER NOT NULL,
            is_favorite INTEGER NOT NULL,
            completion_pct REAL NOT NULL,
            dwell_norm REAL NOT NULL,
            temporal_decay REAL NOT NULL,
            relevance_score REAL NOT NULL,
            updated_at INTEGER NOT NULL,
            PRIMARY KEY (user_id, item_id));
        CREATE TABLE IF NOT EXISTS user_centroids (
            user_id TEXT PRIMARY KEY,
            vector BLOB NOT NULL,
            item_count INTEGER NOT NULL,
            updated_at INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS model_state (
            k TEXT PRIMARY KEY,
            v TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS feedback (
            user_id TEXT NOT NULL,
            item_id TEXT NOT NULL,
            kind TEXT NOT NULL,            -- 'more' | 'less'
            created_at INTEGER NOT NULL,
            PRIMARY KEY (user_id, item_id));
        """;

    private readonly ILogger<VectorStore> _logger;
    private readonly IApplicationPaths _appPaths;
    private readonly int _dim;
    private readonly SemaphoreSlim _dbLock = new(1, 1);
    private readonly ConcurrentDictionary<Guid, IndexedItem> _items = new();
    private readonly ConcurrentDictionary<Guid, List<InteractionRecord>> _interactionsByUser = new();
    private readonly ConcurrentDictionary<Guid, UserCentroid> _centroids = new();
    private readonly ConcurrentDictionary<Guid, Dictionary<Guid, string>> _feedbackByUser = new();

    private SqliteConnection? _connection;
    private int _openState; // 0 closed, 1 open

    public VectorStore(IApplicationPaths appPaths, ILogger<VectorStore> logger)
    {
        _appPaths = appPaths;
        _logger = logger;
        _dim = Plugin.Instance?.Configuration.EmbeddingDim ?? Tuning.EmbeddingDim;
    }

    public int ItemCount => _items.Count;

    private void EnsureOpen()
    {
        if (Volatile.Read(ref _openState) == 1)
        {
            return;
        }

        _dbLock.Wait();
        try
        {
            if (_openState == 1)
            {
                return;
            }

            // ONNX natives (the only natives we bundle) — make sure the resolver is attached before
            // any P/Invoke. SQLite is owned by Jellyfin core; do NOT register a resolver for it.
            NativeProbing.RegisterKnown();

            // IMPORTANT: keep the DB OUTSIDE the plugins directory. Writing under plugins/ risked
            // confusing the PluginManager's folder scan and polluting a watched path. Use the host
            // data root (/config/data) instead: /config/data/gelo/gelo.db.
            var dataDir = Path.Combine(_appPaths.DataPath, "gelo");
            Directory.CreateDirectory(dataDir);
            var dbPath = Path.Combine(dataDir, "gelo.db");

            var conn = new SqliteConnection($"Data Source={dbPath};Cache=Shared");
            conn.Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = SchemaSql;
                cmd.ExecuteNonQuery();
            }

            Migrate(conn);

            _connection = conn;
            WarmLoad(conn);
            Volatile.Write(ref _openState, 1);
            _logger.LogInformation("VectorStore opened at {Path} with {Count} items.", dbPath, _items.Count);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    /// <summary>
    /// Idempotent additive migrations for pre-existing DBs. <c>CREATE TABLE IF NOT EXISTS</c> won't
    /// add columns to an existing table, so each new column is ALTERed in and the "duplicate column
    /// name" error is swallowed. Safe to re-run.
    /// </summary>
    private void Migrate(SqliteConnection conn)
    {
        var migrations = new[]
        {
            "ALTER TABLE items ADD COLUMN composer_names TEXT",
            "ALTER TABLE items ADD COLUMN writer_names TEXT",
            "ALTER TABLE items ADD COLUMN date_created_ticks INTEGER"
        };

        foreach (var sql in migrations)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            try { cmd.ExecuteNonQuery(); }
            catch (Microsoft.Data.Sqlite.SqliteException) { /* column already exists */ }
        }
    }

    private void WarmLoad(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT item_id, item_type, parent_id, vector, name, year, runtime_ticks, genres, tags, studios, community_rating, official_rating, tmdb_id, director_names, cast_names, composer_names, writer_names, date_created_ticks, updated_at FROM items;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var item = ReadItem(reader);
            if (item is not null && item.Vector.Length == _dim)
            {
                _items[item.Id] = item;
            }
        }
    }

    public IndexedItem? GetItem(Guid id)
    {
        EnsureOpen();
        return _items.TryGetValue(id, out var item) ? item : null;
    }

    /// <summary>The host data dir holding gelo.db — also where per-user Phase-2 ranker models live
    /// (under a "ranker" subfolder). Kept OUTSIDE the plugins dir for the same reason as the DB.</summary>
    public string DataDirectory => Path.Combine(_appPaths.DataPath, "gelo");

    public IReadOnlyList<IndexedItem> GetAllItems()
    {
        EnsureOpen();
        return new List<IndexedItem>(_items.Values);
    }

    public void UpsertItem(IndexedItem item)
    {
        EnsureOpen();
        _dbLock.Wait();
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = """
                INSERT INTO items (item_id, item_type, parent_id, vector, name, year, runtime_ticks, genres, tags, studios,
                                   community_rating, official_rating, tmdb_id, director_names, cast_names,
                                   composer_names, writer_names, date_created_ticks, updated_at)
                VALUES (@id, @type, @parent, @vector, @name, @year, @runtime, @genres, @tags, @studios,
                        @rating, @official, @tmdb, @directors, @cast, @composers, @writers, @created, @updated)
                ON CONFLICT(item_id) DO UPDATE SET
                    item_type=excluded.item_type, parent_id=excluded.parent_id, vector=excluded.vector,
                    name=excluded.name, year=excluded.year, runtime_ticks=excluded.runtime_ticks,
                    genres=excluded.genres, tags=excluded.tags, studios=excluded.studios,
                    community_rating=excluded.community_rating, official_rating=excluded.official_rating,
                    tmdb_id=excluded.tmdb_id, director_names=excluded.director_names, cast_names=excluded.cast_names,
                    composer_names=excluded.composer_names, writer_names=excluded.writer_names,
                    date_created_ticks=excluded.date_created_ticks, updated_at=excluded.updated_at;
                """;
            cmd.Parameters.AddWithValue("@id", item.Id.ToString());
            cmd.Parameters.AddWithValue("@type", item.ItemType);
            cmd.Parameters.AddWithValue("@parent", (object?)item.ParentId?.ToString() ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@vector", VectorToBlob(item.Vector));
            cmd.Parameters.AddWithValue("@name", (object?)item.Name ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@year", (object?)item.Year ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@runtime", (object?)item.RunTimeTicks ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@genres", string.Join('|', item.Genres));
            cmd.Parameters.AddWithValue("@tags", string.Join('|', item.Tags));
            cmd.Parameters.AddWithValue("@studios", string.Join('|', item.Studios));
            cmd.Parameters.AddWithValue("@rating", (object?)item.CommunityRating ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@official", (object?)item.OfficialRating ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@tmdb", (object?)item.TmdbId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@directors", string.Join('|', item.DirectorNames));
            cmd.Parameters.AddWithValue("@cast", string.Join('|', item.CastNames));
            cmd.Parameters.AddWithValue("@composers", string.Join('|', item.ComposerNames));
            cmd.Parameters.AddWithValue("@writers", string.Join('|', item.WriterNames));
            cmd.Parameters.AddWithValue("@created", (object?)item.DateCreatedTicks ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@updated", item.UpdatedAtTicks);
            cmd.ExecuteNonQuery();

            _items[item.Id] = item;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public void UpsertInteraction(InteractionRecord r)
    {
        EnsureOpen();
        _dbLock.Wait();
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = """
                INSERT INTO interactions (user_id, item_id, play_count, last_played_ticks, played, is_favorite,
                                          completion_pct, dwell_norm, temporal_decay, relevance_score, updated_at)
                VALUES (@uid, @iid, @pc, @lp, @played, @fav, @comp, @dwell, @decay, @rel, @updated)
                ON CONFLICT(user_id, item_id) DO UPDATE SET
                    play_count=excluded.play_count, last_played_ticks=excluded.last_played_ticks,
                    played=excluded.played, is_favorite=excluded.is_favorite,
                    completion_pct=excluded.completion_pct, dwell_norm=excluded.dwell_norm,
                    temporal_decay=excluded.temporal_decay, relevance_score=excluded.relevance_score,
                    updated_at=excluded.updated_at;
                """;
            cmd.Parameters.AddWithValue("@uid", r.UserId.ToString());
            cmd.Parameters.AddWithValue("@iid", r.ItemId.ToString());
            cmd.Parameters.AddWithValue("@pc", r.PlayCount);
            cmd.Parameters.AddWithValue("@lp", (object?)r.LastPlayedTicks ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@played", r.Played ? 1 : 0);
            cmd.Parameters.AddWithValue("@fav", r.IsFavorite ? 1 : 0);
            cmd.Parameters.AddWithValue("@comp", r.CompletionPct);
            cmd.Parameters.AddWithValue("@dwell", r.DwellNorm);
            cmd.Parameters.AddWithValue("@decay", r.TemporalDecay);
            cmd.Parameters.AddWithValue("@rel", r.RelevanceScore);
            cmd.Parameters.AddWithValue("@updated", r.UpdatedAtTicks);
            cmd.ExecuteNonQuery();

            _interactionsByUser.TryRemove(r.UserId, out _); // invalidate per-user cache
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public IReadOnlyList<InteractionRecord> GetInteractionsForUser(Guid userId)
    {
        EnsureOpen();
        return _interactionsByUser.GetOrAdd(userId, LoadInteractionsForUser);
    }

    /// <summary>
    /// Every user with at least one interaction row. Used by the scheduled retrain task to rebuild
    /// all per-user models. DB-backed (the in-memory cache is lazy and only covers queried users).
    /// </summary>
    public IReadOnlyList<Guid> GetAllUserIds()
    {
        EnsureOpen();
        _dbLock.Wait();
        try
        {
            var ids = new List<Guid>();
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT user_id FROM interactions;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (Guid.TryParse(reader.GetString(0), out var id))
                {
                    ids.Add(id);
                }
            }

            return ids;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    // ───────────────────────────────────── Feedback (more/less) ─────────────────────────────────────

    /// <summary>
    /// Record or update explicit feedback for an item. <paramref name="kind"/> is "more" (boost) or
    /// "less" (suppress). Upserts on (user, item) so re-voting flips the kind. Invalidates the cache.
    /// </summary>
    public void RecordFeedback(Guid userId, Guid itemId, string kind)
    {
        EnsureOpen();
        _dbLock.Wait();
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = """
                INSERT INTO feedback (user_id, item_id, kind, created_at)
                VALUES (@uid, @iid, @kind, @ts)
                ON CONFLICT(user_id, item_id) DO UPDATE SET kind=excluded.kind, created_at=excluded.created_at;
                """;
            cmd.Parameters.AddWithValue("@uid", userId.ToString());
            cmd.Parameters.AddWithValue("@iid", itemId.ToString());
            cmd.Parameters.AddWithValue("@kind", kind);
            cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.Ticks);
            cmd.ExecuteNonQuery();

            _feedbackByUser.TryRemove(userId, out _);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    /// <summary>Remove feedback (undo a more/less vote). Returns true if a row was deleted.</summary>
    public bool RemoveFeedback(Guid userId, Guid itemId)
    {
        EnsureOpen();
        _dbLock.Wait();
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "DELETE FROM feedback WHERE user_id=@uid AND item_id=@iid;";
            cmd.Parameters.AddWithValue("@uid", userId.ToString());
            cmd.Parameters.AddWithValue("@iid", itemId.ToString());
            var rows = cmd.ExecuteNonQuery();
            if (rows > 0)
            {
                _feedbackByUser.TryRemove(userId, out _);
            }

            return rows > 0;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    /// <summary>The user's feedback map: item id → kind ("more"/"less"). Cached per user.</summary>
    public IReadOnlyDictionary<Guid, string> GetFeedbackForUser(Guid userId)
    {
        EnsureOpen();
        return _feedbackByUser.GetOrAdd(userId, LoadFeedbackForUser);
    }

    private Dictionary<Guid, string> LoadFeedbackForUser(Guid userId)
    {
        _dbLock.Wait();
        try
        {
            var dict = new Dictionary<Guid, string>();
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "SELECT item_id, kind FROM feedback WHERE user_id=@uid;";
            cmd.Parameters.AddWithValue("@uid", userId.ToString());
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (Guid.TryParse(reader.GetString(0), out var id))
                {
                    dict[id] = reader.GetString(1);
                }
            }

            return dict;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    private List<InteractionRecord> LoadInteractionsForUser(Guid userId)
    {
        _dbLock.Wait();
        try
        {
            var list = new List<InteractionRecord>();
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "SELECT user_id, item_id, play_count, last_played_ticks, played, is_favorite, completion_pct, dwell_norm, temporal_decay, relevance_score, updated_at FROM interactions WHERE user_id=@uid;";
            cmd.Parameters.AddWithValue("@uid", userId.ToString());
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(ReadInteraction(reader));
            }

            return list;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public void SetCentroid(UserCentroid centroid)
    {
        EnsureOpen();
        _centroids[centroid.UserId] = centroid;
        _dbLock.Wait();
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = """
                INSERT INTO user_centroids (user_id, vector, item_count, updated_at)
                VALUES (@uid, @vec, @cnt, @updated)
                ON CONFLICT(user_id) DO UPDATE SET
                    vector=excluded.vector, item_count=excluded.item_count, updated_at=excluded.updated_at;
                """;
            cmd.Parameters.AddWithValue("@uid", centroid.UserId.ToString());
            cmd.Parameters.AddWithValue("@vec", VectorToBlob(centroid.Vector));
            cmd.Parameters.AddWithValue("@cnt", centroid.ItemCount);
            cmd.Parameters.AddWithValue("@updated", centroid.UpdatedAtTicks);
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public UserCentroid? GetCentroid(Guid userId)
    {
        EnsureOpen();
        if (_centroids.TryGetValue(userId, out var cached))
        {
            return cached;
        }

        _dbLock.Wait();
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "SELECT user_id, vector, item_count, updated_at FROM user_centroids WHERE user_id=@uid;";
            cmd.Parameters.AddWithValue("@uid", userId.ToString());
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                var c = new UserCentroid
                {
                    UserId = Guid.Parse(reader.GetString(0)),
                    Vector = BlobToVector((byte[])reader["vector"]),
                    ItemCount = reader.GetInt32(2),
                    UpdatedAtTicks = reader.GetInt64(3)
                };
                _centroids[userId] = c;
                return c;
            }

            return null;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public string? GetMeta(string key)
    {
        EnsureOpen();
        _dbLock.Wait();
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "SELECT v FROM model_state WHERE k=@k;";
            cmd.Parameters.AddWithValue("@k", key);
            var v = cmd.ExecuteScalar();
            return v is null or DBNull ? null : (string)v;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public void SetMeta(string key, string value)
    {
        EnsureOpen();
        _dbLock.Wait();
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = """
                INSERT INTO model_state (k, v) VALUES (@k, @v)
                ON CONFLICT(k) DO UPDATE SET v=excluded.v;
                """;
            cmd.Parameters.AddWithValue("@k", key);
            cmd.Parameters.AddWithValue("@v", value);
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _dbLock.Release();
        }
    }

    private static IndexedItem? ReadItem(SqliteDataReader r)
    {
        var idStr = r.IsDBNull(0) ? null : r.GetString(0);
        if (idStr is null || !Guid.TryParse(idStr, out var id))
        {
            return null;
        }

        var parentId = r.IsDBNull(2) ? (Guid?)null : Guid.TryParse(r.GetString(2), out var pid) ? pid : null;
        var vector = BlobToVector((byte[])r["vector"]);
        return new IndexedItem
        {
            Id = id,
            ItemType = r.IsDBNull(1) ? string.Empty : r.GetString(1),
            ParentId = parentId,
            Vector = vector,
            Name = r.IsDBNull(4) ? string.Empty : r.GetString(4),
            Year = r.IsDBNull(5) ? null : r.GetInt32(5),
            RunTimeTicks = r.IsDBNull(6) ? null : r.GetInt64(6),
            Genres = Split(r, 7),
            Tags = Split(r, 8),
            Studios = Split(r, 9),
            CommunityRating = r.IsDBNull(10) ? null : r.GetDouble(10),
            OfficialRating = r.IsDBNull(11) ? null : r.GetString(11),
            TmdbId = r.IsDBNull(12) ? null : r.GetString(12),
            DirectorNames = Split(r, 13),
            CastNames = Split(r, 14),
            ComposerNames = Split(r, 15),
            WriterNames = Split(r, 16),
            DateCreatedTicks = r.IsDBNull(17) ? null : r.GetInt64(17),
            UpdatedAtTicks = r.GetInt64(18)
        };
    }

    private static InteractionRecord ReadInteraction(SqliteDataReader r) => new()
    {
        UserId = Guid.Parse(r.GetString(0)),
        ItemId = Guid.Parse(r.GetString(1)),
        PlayCount = r.GetInt32(2),
        LastPlayedTicks = r.IsDBNull(3) ? null : r.GetInt64(3),
        Played = r.GetInt32(4) != 0,
        IsFavorite = r.GetInt32(5) != 0,
        CompletionPct = r.GetDouble(6),
        DwellNorm = r.GetDouble(7),
        TemporalDecay = r.GetDouble(8),
        RelevanceScore = r.GetDouble(9),
        UpdatedAtTicks = r.GetInt64(10)
    };

    private static string[] Split(SqliteDataReader r, int ordinal)
        => r.IsDBNull(ordinal) || string.IsNullOrEmpty(r.GetString(ordinal))
            ? Array.Empty<string>()
            : r.GetString(ordinal).Split('|', StringSplitOptions.RemoveEmptyEntries);

    private static byte[] VectorToBlob(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] BlobToVector(byte[] blob)
    {
        var vector = new float[blob.Length / sizeof(float)];
        Buffer.BlockCopy(blob, 0, vector, 0, blob.Length);
        return vector;
    }
}
