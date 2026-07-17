// SPDX-FileCopyrightText: 2026 Dana Buehre
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Gelo.Persistence;
using Jellyfin.Plugin.Gelo.Recs;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Gelo.Api;

/// <summary>
/// Custom recommendation endpoints. Controllers in plugin assemblies are auto-discovered by
/// the server's ApplicationPartManager; the unique prefix keeps us off Jellyfin's own routes.
/// </summary>
[ApiController]
[Authorize] // any authenticated user; Status additionally requires admin
[Route("CustomRecommendations")]
public sealed class RecommendationsController : ControllerBase
{
    private readonly RecommendationService _recommendations;
    private readonly VectorStore _store;

    public RecommendationsController(RecommendationService recommendations, VectorStore store)
    {
        _recommendations = recommendations;
        _store = store;
    }

    /// <summary>GET /CustomRecommendations/Items/{itemId}/Similar</summary>
    [HttpGet("Items/{itemId}/Similar")]
    public ActionResult<List<SimilarItemDto>> Similar(
        [FromRoute] Guid itemId,
        [FromQuery] int limit = 12,
        [FromQuery] string? type = null,
        [FromQuery] bool unwatched = false,
        [FromQuery] Guid? userId = null,
        [FromQuery] string? collection = null)
    {
        if (itemId == Guid.Empty)
        {
            return BadRequest("Invalid itemId.");
        }

        return Ok(_recommendations.Similar(itemId, limit, type, unwatched, userId, collection));
    }

    /// <summary>GET /CustomRecommendations/Users/{userId}/Shelves</summary>
    [HttpGet("Users/{userId}/Shelves")]
    public ActionResult<List<ShelfDto>> Shelves(
        [FromRoute] Guid userId,
        [FromQuery] int? limit = null,
        [FromQuery] bool unwatched = false,
        [FromQuery] string? type = null)
    {
        if (userId == Guid.Empty)
        {
            return BadRequest("Invalid userId.");
        }

        return Ok(_recommendations.Shelves(userId, limit, unwatched, type));
    }

    /// <summary>GET /CustomRecommendations/Users/{userId}/Recommendations — flat ranked "for you" list.</summary>
    [HttpGet("Users/{userId}/Recommendations")]
    public ActionResult<List<SimilarItemDto>> Recommendations(
        [FromRoute] Guid userId,
        [FromQuery] int limit = 24,
        [FromQuery] bool unwatched = true,
        [FromQuery] string? type = null,
        [FromQuery] string? variety = null,
        [FromQuery] int? seed = null)
    {
        if (userId == Guid.Empty)
        {
            return BadRequest("Invalid userId.");
        }

        return Ok(_recommendations.Recommendations(userId, limit, unwatched, type, variety, seed));
    }

    /// <summary>GET /CustomRecommendations/Status (admin only)</summary>
    [HttpGet("Status")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<StatusDto> Status()
    {
        return Ok(_recommendations.Status());
    }

    /// <summary>GET /CustomRecommendations/Ping — non-admin readiness probe (any authenticated user).</summary>
    [HttpGet("Ping")]
    public ActionResult<PingDto> Ping()
    {
        return Ok(_recommendations.Ping());
    }

    // ───────────────────────────────── Feedback (more/less) ─────────────────────────────────

    /// <summary>POST /CustomRecommendations/Users/{userId}/Feedback — record a more/less vote.</summary>
    [HttpPost("Users/{userId}/Feedback")]
    public IActionResult RecordFeedback([FromRoute] Guid userId, [FromBody] FeedbackRequest body)
    {
        if (userId == Guid.Empty || body.ItemId == Guid.Empty)
        {
            return BadRequest("Invalid userId or itemId.");
        }

        var kind = body.Kind?.Trim().ToLowerInvariant();
        if (kind != "more" && kind != "less")
        {
            return BadRequest("Kind must be 'more' or 'less'.");
        }

        _store.RecordFeedback(userId, body.ItemId, kind);
        return NoContent();
    }

    /// <summary>GET /CustomRecommendations/Users/{userId}/Feedback — list this user's votes.</summary>
    [HttpGet("Users/{userId}/Feedback")]
    public ActionResult<List<FeedbackEntryDto>> GetFeedback([FromRoute] Guid userId)
    {
        if (userId == Guid.Empty)
        {
            return BadRequest("Invalid userId.");
        }

        return Ok(_store.GetFeedbackForUser(userId)
            .Select(kv => new FeedbackEntryDto(kv.Key, kv.Value))
            .ToList());
    }

    /// <summary>DELETE /CustomRecommendations/Users/{userId}/Feedback/{itemId} — undo a vote.</summary>
    [HttpDelete("Users/{userId}/Feedback/{itemId}")]
    public IActionResult RemoveFeedback([FromRoute] Guid userId, [FromRoute] Guid itemId)
    {
        if (userId == Guid.Empty || itemId == Guid.Empty)
        {
            return BadRequest("Invalid userId or itemId.");
        }

        _store.RemoveFeedback(userId, itemId);
        return NoContent();
    }
}

