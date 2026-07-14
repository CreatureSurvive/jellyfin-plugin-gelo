# Changelog

All notable changes to Gelo Recommendations are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.1.0.0] — 2026-07-14

Client-facing API additions: a non-admin readiness probe, a flat ranked list, and shelf shaping.

### Added
- **`GET /Ping`** — non-admin readiness probe returning `{ Enabled, EmbeddingsReady, ItemCount, Ready }`
  for any authenticated user (no elevation), so regular clients can detect Gelo without hitting the
  admin-only `/Status`. `Ready` reflects servability (enabled + indexed items); `EmbeddingsReady` is
  reported separately as the lazily-loaded ONNX-session flag.
- **`GET /Users/{userId}/Recommendations`** — flat ranked "for you" list (the engine's primary
  surface): cosine similarity to the user's taste centroid, re-ranked by the trained model when
  enabled, with a community-rating cold-start fallback. Supports `?limit&unwatched&type` (`unwatched`
  defaults to true, excluding items the user has already engaged with).

### Changed
- **`GET /Users/{userId}/Shelves`** gains optional `?limit&unwatched&type` query params — a coarse
  post-filter applied after the diversity pipeline (`limit` caps items per shelf, `unwatched` drops
  played items, `type` filters by item type).

## [1.0.0] — 2026-07-14

First public release.

### Added
- Semantic content tier using **all-MiniLM-L6-v2** (384-dim) embeddings via ONNX Runtime, with a
  relevance-weighted, L2-normalized per-user taste centroid and SIMD cosine similarity.
- **Home shelves** — up to 10 rotating rails across 10 categories (genre, similar×2, collection,
  mood, period, micro-genre, director, actor, composer, wildcard), each seeded-rotated and
  semantic-reranked.
- **"Recommended" rail** on movie/series detail pages (item-to-item semantic recs).
- **More/less feedback** API and suppression/nudge in the rerank pipeline.
- Three-phase gating: ruleBased (cold-start) → contentSimilarity → fullModel.
- Post-processing: diversity soft-caps (genre/decade/creator), exploration injection, freshness boost.
- **Tier-2 trained ranker** — optional per-user ML.NET (`LbfgsLogisticRegression`, pure-managed)
  logistic model on the 384-d embedding + scalar metadata + heuristic affinity.
- Incremental indexing off live library/user-data events; weekly full re-embed catch-up; daily
  retrain task (centroids + optional ranker).
- **Web UI integration** — defensive in-process injection of the Gelo client script into the
  jellyfin-web home and detail pages with native-styled cards.
- Dashboard configuration page with every weight, decay, shelf count, and tier toggle exposed.
- REST API: `/Shelves`, `/Items/{id}/Similar`, `/Status`, `/Users/{id}/Feedback`.

### Notes
- Targets Jellyfin **10.11.x** / **net9.0**, **linux-x64** (ONNX natives bundled).
- Reuses Jellyfin core's SQLite stack (no competing native bundled).
- Licensed under GPL-3.0-or-later.
