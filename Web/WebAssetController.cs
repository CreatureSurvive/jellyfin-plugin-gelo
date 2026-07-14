// SPDX-FileCopyrightText: 2026 Dana Buehre
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Gelo.Web;

/// <summary>
/// Serves the Gelo client assets that are injected into jellyfin-web's <c>index.html</c>. Anonymous
/// (the script carries no secrets; its data calls ride the user's own <c>ApiClient</c> token).
/// <para>
/// <c>gelo.js</c> is config-baked (the active shelf-count/position are spliced into a placeholder so
/// the script is self-contained) and served <c>no-cache</c> so Web UI settings apply on the next load.
/// <c>gelo.css</c> is purely static and long-cached.
/// </para>
/// </summary>
[ApiController]
[AllowAnonymous]
public sealed class WebAssetController : ControllerBase
{
    private const string BasePath = "/CustomRecommendations/web/";

    /// <summary>Serves the config-baked client script.</summary>
    [HttpGet(BasePath + "gelo.js")]
    public IActionResult GetGeloJs()
    {
        var js = ReadText("Jellyfin.Plugin.Gelo.Web.assets.gelo.js");
        if (js is null)
        {
            return NotFound();
        }

        // Splice the active Web UI settings into the placeholder. The placeholder is wrapped in a JS
        // object literal in the source ({/*GELO_CONFIG*/}) so an un-replaced serve stays valid JS
        // (empty object) and the script falls back to its own defaults.
        js = js.Replace("/*GELO_CONFIG*/", BuildConfigInline(), StringComparison.Ordinal);

        Response.Headers.CacheControl = "no-cache";
        return Content(js, "text/javascript; charset=utf-8");
    }

    /// <summary>Serves the client stylesheet (static, long-cached).</summary>
    [HttpGet(BasePath + "gelo.css")]
    public IActionResult GetGeloCss()
    {
        var css = ReadText("Jellyfin.Plugin.Gelo.Web.assets.gelo.css");
        if (css is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "public, max-age=604800";
        return Content(css, "text/css; charset=utf-8");
    }

    /// <summary>Inline JSON key/value pairs (no braces) for the placeholder splice.</summary>
    private static string BuildConfigInline()
    {
        var cfg = Plugin.Instance?.Configuration;
        var sc = cfg?.WebUIShelfCount ?? 0;
        var shelfCount = (sc > 0 && sc <= 10) ? sc : 4;
        var position = cfg?.WebUIPosition switch
        {
            "top" => "top",
            "bottom" => "bottom",
            _ => "afterFirst"
        };

        return "\"base\":\"/CustomRecommendations\""
            + ",\"shelfCount\":" + shelfCount
            + ",\"position\":\"" + position + "\""
            + ",\"detailLimit\":12";
    }

    private static string? ReadText(string resourceName)
    {
        try
        {
            using var stream = typeof(WebAssetController).Assembly
                .GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                return null;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }
}
