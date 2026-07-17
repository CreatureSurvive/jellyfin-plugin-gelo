# Gelo Recommendations

A semantic, context-aware recommendation engine for [Jellyfin](https://jellyfin.org).

Gelo learns what you actually watch and turns it into three things:

- **Recommended For You** — a flat, ranked rail of the best next watches for each user, leading
  the home screen. Dial it from precise top matches to daily-rotating discovery.
- **Home shelves** — dynamic, rotating rails ("Because you watched…", mood runs, genre dives,
  collection picks, wildcard discoveries) that render directly on the Jellyfin home screen.
- **More-Like-This** — a "Recommended" rail on every movie/series detail page, built from
  real plot/genre/credit semantics rather than crude genre overlap.

It runs entirely on your server. No telemetry, no external API calls — embeddings are computed
locally with a bundled transformer model.

---

## Highlights

- **Two-tier ranking.** A semantic content tier (all-MiniLM-L6-v2 embeddings + a
  relevance-weighted taste centroid) handles cold-starts and sub-ms item-to-item lookups; an
  optional per-user trained ranker learns engagement probability from your watch history once
  there is enough of it.
- **Home-screen integration.** A "Recommended For You" rail plus dynamic shelves are injected
  straight into the Jellyfin web home, with a "More Like This" rail on detail pages — native-styled
  cards, no separate app or page.
- **Learns from real engagement.** Time-decayed relevance weights completion, play count,
  favorites, and dwell, and deliberately ignores watchlist noise, so the taste profile tracks
  what you actually enjoy over time.
- **Sub-millisecond similarity.** SIMD cosine scan over a warm in-memory vector cache.
- **Incremental.** Indexes off live library/user-data events; a weekly catch-up re-embed keeps
  new media covered.
- **Tunable.** Every weight, decay, shelf count, and tier toggle is exposed in the dashboard,
  with sensible defaults — including a "For You" variety dial from precise top matches to
  daily-rotating discovery.

---

## Requirements

- **Jellyfin 10.11.x** (the plugin targets `10.11.0.0` / `net9.0`). Other versions are not supported.
- **Linux x64** host. The ONNX Runtime native libraries are bundled for `linux-x64`; other
  platforms are not shipped (build your own for them — see [Building](#building)).
- **~120 MB** free for the deployed plugin (mostly the embedding model + ONNX + ML.NET managed assemblies).
- **CPU** is the execution provider. MiniLM-L6-v2 inference is light (a full library re-embed is
  throttled to a few embeddings/second by default to protect low-power hosts); raise the limit in
  settings on faster hardware.

---

## Installation

### From the plugin repository

Once a release exists, this is the recommended path — you get updates automatically.

1. In Jellyfin, go to **Dashboard → Plugins → Repositories** and add a new repository with the
   manifest URL:
   ```
   https://raw.githubusercontent.com/CreatureSurvive/jellyfin-plugin-gelo/main/manifest.json
   ```
2. Under **Dashboard → Plugins → Catalog**, find **Gelo Recommendations** and click **Install**.
3. Restart Jellyfin.
4. On first run, trigger **Dashboard → Scheduled Tasks → "Full library reindex"** once to embed
   your library (this runs automatically weekly thereafter).

### From a release

1. Copy the plugin folder (`Gelo Recommendations_1.0.0.0/`) into Jellyfin's plugins directory:
   - Linux: `/var/lib/jellyfin/plugins/` (or `/config/data/plugins/` in the linuxserver image).
2. Restart Jellyfin.
3. Open **Dashboard → Plugins → Gelo Recommendations** to confirm it loaded and to configure it.
4. On first run, trigger **Dashboard → Scheduled Tasks → "Full library reindex"** once to embed
   your library (this runs automatically weekly thereafter).

### From source

See [Building](#building) below, then run `./package.sh --deploy` and restart Jellyfin.

---

## How it works

### Tier 1 — semantic content

Each movie and show is turned into a text block — title, year, genres, directors, lead cast, and
the overview — and embedded with **all-MiniLM-L6-v2** (384-dim) via ONNX Runtime. The overview
dominates, which is what lets Gelo match "plot vibes" (Interstellar ↔ Arrival) that pure metadata
matching misses.

A user's **taste centroid** is a relevance-weighted, L2-normalized mean of the embeddings of items
they've engaged with. Relevance is:

```
relevanceScore = (0.40·completion + 0.30·playCountNorm + 0.20·isFavorite + 0.10·dwell) · temporalDecay
```

…with a **90-day half-life** temporal decay, per-user play-count normalization, and an engagement
gate that keeps requested-but-unwatched noise out of the profile. Item-to-item and item-to-user
similarity is plain cosine, computed as a SIMD scan over a warm cache — fast enough to serve every
shelf and More-Like-This in well under a millisecond.

### Shelf engine

Gelo produces up to 10 shelves per home load, round-robin across categories: genre, two "similar
to what you watched" rails, collection, mood, period, micro-genre, director, actor, composer, and a
wildcard/serendipity rail. Each candidate set is seeded-rotated (so shelves reshuffle across
refreshes but stay stable within one) and reranked by centroid affinity, with post-processing for
**diversity** (soft-caps on genre/decade/creator within a sliding window), **exploration**
(staggered lower-ranked picks on page 0), and **freshness** (a lift for recently-added items).

### Phase gating

The engine adapts to how much you've watched:

| Watched items | Phase | Behavior |
|---|---|---|
| 0 | ruleBased | Community-rating + mood-preset shelves so a fresh home is never empty |
| 1–9 | contentSimilarity | Centroid-affinity shelves |
| 10+ | fullModel | Trained ranker orders shelves (degrades to contentSimilarity until a model is trained) |

### Tier 2 — trained ranker (optional)

When enabled and enough watch history exists, a **per-user logistic ranker** (ML.NET
`LbfgsLogisticRegression`, pure-managed — no native dependencies) is trained on a 392-feature
vector per item: the 384-d embedding plus a small block of scalar metadata (affinity, year,
rating, runtime, recency, item-type one-hots). Positives are your engaged interactions (weighted
by relevance); negatives are a deterministic sample of un-watched items. When a model is available,
shelves are ordered by predicted engagement probability; until then it transparently falls back to
the Tier-1 heuristic. Models train on the daily off-peak schedule and can be rebuilt on demand.

---

## Configuration

Open **Dashboard → Plugins → Gelo Recommendations**. Key options:

- **Enable plugin** — master kill switch.
- **Enable indexing** — parks the plugin in a pure-managed state (no ONNX/SQLite/event hooks) for
  safe-mode bring-up.
- **Enable Tier-2 ranker** — turn on the trained ranker (off by default; enable after you have
  watch history, then run "Trigger model retrain").
- **Enable web UI** — inject the home/detail rails into the web client (on by default).
- **Relevance weights** (`completion / playCount / favorite / dwell`) — should sum to ~1.0.
- **Decay half-life (days)** — temporal decay (default 90).
- **Max shelves**, **items per shelf**, **diversity / exploration / freshness** toggles.
- **"For You" variety** — how much the flat Recommended-For-You list varies: `off` (exact top
  matches), `low` (spread across genres), `medium`/`high` (wider pool + daily-rotating discovery).
  The `?variety=` request param overrides this per call.
- **Web UI shelf count / position** — how many rails render and where (`top` / `afterFirst` / `bottom`).
- **Embedding batch size / max embeds per second / execution provider** — throughput vs. load.

Every setting overrides the engine default on the hot path and can be changed without editing files.

---

## REST API

All routes are under `/CustomRecommendations`. Every route is available to any authenticated
Jellyfin user (the web client attaches the token automatically) **except** `/Status`, which requires
an administrator. `/Ping` is the non-admin readiness probe for clients that need to detect Gelo
without elevation.

| Method | Path | Returns |
|---|---|---|
| `GET` | `/Ping` | Non-admin readiness probe: `{ Enabled, EmbeddingsReady, ItemCount, Ready }` |
| `GET` | `/Items/{itemId}/Similar` | Ranked similar items (`Id`, `Name`, `Type`, `Score`); `?limit&unwatched&type&userId&collection` |
| `GET` | `/Users/{userId}/Shelves` | Home shelves (`Title`, `Paradigm`, `Items[]`); optional `?limit&unwatched&type` |
| `GET` | `/Users/{userId}/Recommendations` | Flat ranked "for you" list (`Id`, `Name`, `Type`, `Score`); optional `?limit&unwatched&type&variety&seed` |
| `GET` | `/Status` | Admin-only engine status (`ItemCount`, `Dimension`, `EmbeddingsReady`, `LastFullReindex`, `ModelId`) |
| `POST` | `/Users/{userId}/Feedback` | Record `more` / `less` for an item (body: `{ "itemId", "kind" }`) |
| `GET` | `/Users/{userId}/Feedback` | List recorded feedback |
| `DELETE` | `/Users/{userId}/Feedback/{itemId}` | Remove feedback for an item |

- **`/Ping`** is how a regular client detects that Gelo is installed and ready. `Ready` is true when
  the plugin is enabled and at least one item is indexed. `EmbeddingsReady` is reported separately and
  only reflects the lazily-loaded ONNX session — it can be `false` right after a restart even though
  serving already works from the cached vectors.
- **`/Recommendations`** returns a single flat ranked list (the engine's primary surface — and what
  the home "Recommended For You" rail renders): cosine similarity to the user's taste centroid,
  re-ranked by the trained model when enabled, with a community-rating fallback for cold users.
  `unwatched` defaults to `true` (excludes items the user has already engaged with). `variety`
  (`off`/`low`/`medium`/`high`, default `off`) optionally applies diversity soft-caps and injects
  seeded exploration picks so the list isn't the same static set every call — exploration rotates
  per user per day; `seed` overrides that (to page or force a fresh shuffle).
- **`/Shelves`** params (`limit` caps items per shelf, `unwatched` drops played items, `type` filters)
  are a coarse post-filter applied after the diversity pipeline — prefer server config for structural
  control.

Feedback is sticky: `less` suppresses an item from shelves; `more` nudges it (and similar items) up.

The web assets are served (anonymously, cache-busted) at `/CustomRecommendations/web/gelo.js` and
`/gelo.css`.

---

## Scheduled tasks

Both live under **Dashboard → Scheduled Tasks**, category **Gelo Recommendations**, and are editable there.

| Task | Default schedule | What it does |
|---|---|---|
| **Full library reindex** | Weekly, Sunday 03:30 | Re-embed every movie and show from scratch (catch-up). Max 1h. |
| **Retrain user models** | Daily, 04:07 | Rebuild every user's taste centroid (so weight/decay changes apply instantly) and, when enabled, retrain the ranker. Max 30m. |

---

## Architecture

```
Library/UserData events ──▶ IndexWorker ──▶ EmbeddingService (MiniLM / ONNX)
                                                 │
                                                 ▼
                                    SQLite-backed VectorStore (items + interactions)
                                                 │
           ┌─────────────────────────────────────┼──────────────────────────────────┐
           ▼                                     ▼                                  ▼
   CentroidBuilder                    TasteProfileBuilder                   Tier2Ranker
   (relevance-weighted                (top genres/people/                   (per-user ML.NET
    taste vector)                      collections/decades)                 logistic model)
           │                                     │                                  │
           └──────────────▶ SmartShelfEngine ◀──┴──────────────────────────────────┘
                                 │  (10-category shelves + rerank pipeline)
                                 ▼
                      RecommendationService  ──▶  /Ping, /Shelves, /Recommendations, /Similar, /Status, /Feedback
                                 │
                                 ▼
                  gelo.js (injected into jellyfin-web)  ──▶  home "For You" + shelves + detail rail
```

**Native-loading care.** Gelo bundles the ONNX Runtime natives for `linux-x64` (flattened to the
plugin root and resolved via a custom probing hook) and reuses the SQLite stack already loaded by
Jellyfin core rather than shipping a competing copy. The ML.NET ranker is pure-managed. The
packaging script strips every other `runtimes/` native to keep the deploy lean.

**Web injection is non-destructive.** The web-client file provider is wrapped (Harmony) only to add
a single `<script>` tag to the served `index.html`. The wrap is fully defensive: on any error it
leaves the vanilla web client intact, and disabling the plugin (or the **Enable web UI** toggle)
removes the patch entirely.

---

## Building

You need Docker (the .NET 9 SDK image is used so the host doesn't need the SDK installed):

```bash
# 1. Publish (net9.0, linux-x64 to pull the ONNX natives)
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:9.0 \
  dotnet publish -c Release -r linux-x64 -o ./publish

# 2. Stage (+ optionally deploy) the plugin folder with a generated meta.json
./package.sh            # stage into dist/
./package.sh --deploy   # stage + copy into the Jellyfin plugins directory

# 3. Restart Jellyfin to load it
```

**The embedding model is tracked with Git LFS** (`Assets/model.onnx`, ~87 MB; see `.gitattributes`).
Run `git lfs install` once per machine, then an ordinary clone fetches the model and a from-source
build works out of the box. (`Assets/vocab.txt` is tracked normally.) The LFS object is only pulled
on source clones — release ZIPs contain the model as ordinary bytes, so plugin *installs* never
touch LFS bandwidth.

For a non-linux-x64 target, change the `-r` RID and adjust `package.sh`'s native-flattening step
to match the platform's ONNX `.so`/`.dylib`/`.dll` names.

---

## Troubleshooting

- **Home shows no Gelo rails.** Check **Enable plugin** and **Enable web UI** are on; confirm the
  plugin loaded in **Dashboard → Plugins**; hard-reload the web client (the injected script is
  cache-busted per build). If you just installed, run "Full library reindex" once.
- **`GET /Status` reports `EmbeddingsReady: false`.** The first reindex hasn't completed (or
  **Enable indexing** is off). Trigger the reindex task.
- **Slow first boot / high CPU after install.** That's the initial library embed — it's throttled
  by **Max embeds per second**. Raise it on faster hardware, or wait for the weekly reindex.
- **Web client won't load at all.** Almost certainly a failed file-provider wrap. The plugin logs
  `[Gelo] web-patch:` lines on boot; disable **Enable web UI** (or the plugin) and restart to
  restore the vanilla client while investigating.
- **Ranker doesn't change shelves.** It needs ≥ `Min items for training` engaged items and a model
  trained. Run "Retrain user models" and watch the log for the AUC line; below ~0.5 AUC means too
  little signal — watch more, then retrain.

Plugin boot diagnostics are written to stderr (`[Gelo] boot: …`) and appear in the Jellyfin /
container logs.

---

## License

Copyright © 2026 Dana Buehre. Licensed under the [GNU General Public License v3.0 or later](LICENSE)
(GPL-3.0-or-later).
