// SPDX-FileCopyrightText: 2026 Dana Buehre
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.Gelo.Configuration;
using Jellyfin.Plugin.Gelo.Web;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Gelo;

/// <summary>
/// Gelo Recommendations — a semantic, context-aware recommendation engine for Jellyfin.
/// </summary>
public class Plugin : BasePlugin<GeloPluginConfiguration>, IHasWebPages
{
    /// <summary>Stable plugin identity (kept in sync with build.yaml + meta.json).</summary>
    public const string PluginGuid = "27d91ff0-2eba-4112-9a58-823fe08db004";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        // The plugin's own assembly location is available immediately (unlike AssemblyFilePath,
        // which the host sets after construction) — use it to locate bundled native libs.
        var pluginDir = Path.GetDirectoryName(GetType().Assembly.Location);
        // Boot heartbeat + native preload happen FIRST, on every cycle, so even short-lived crash
        // loops leave evidence in the log/docker output of how far the boot reached.
        Console.Error.WriteLine($"[Gelo] boot: Plugin ctor (dir={pluginDir}, indexing={(Configuration.EnableIndexing ? "ON" : "OFF (safe mode)")})");
        NativeProbing.SetPluginDirectory(pluginDir);
        NativeProbing.RegisterKnown();
        Console.Error.WriteLine("[Gelo] boot: native probing registered");

        // Inject the Gelo client script into jellyfin-web by wrapping the /web file provider. This runs
        // pre-Startup.Configure (plugins construct during service registration), so the wrap is in place
        // when the static-file pipeline is built. Fully defensive: any failure leaves the vanilla web
        // client intact. The actual injection is live-gated on EnableWebUI at request time.
        try
        {
            HarmonyTransformer.Apply();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[Gelo] boot: web transformer init skipped (vanilla web): " + ex.Message);
        }
    }

    /// <summary>Singleton access for services that need paths/config without DI.</summary>
    public static Plugin? Instance { get; private set; }

    public override string Name => "Gelo Recommendations";

    public override Guid Id => Guid.Parse(PluginGuid);

    public override string Description =>
        "Semantic, context-aware home shelves and sub-ms item-to-item recommendations.";

    /// <summary>
    /// Surfaces the configuration page (Dashboard → Plugins → Gelo Recommendations). Served from
    /// the embedded resource at <see cref="PluginPageInfo.EmbeddedResourcePath"/>.
    /// </summary>
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = "Gelo",
            DisplayName = "Gelo Recommendations",
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.ConfigurationPage.html",
            EnableInMainMenu = false
        };
    }
}
