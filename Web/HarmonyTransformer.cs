// SPDX-FileCopyrightText: 2026 Dana Buehre
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.Gelo.Web;

/// <summary>
/// Applies a single, surgical runtime patch that lets Gelo rewrite jellyfin-web's served
/// <c>index.html</c> in-process (the web dir is root-owned and unwritable on disk, so this is the only
/// way to land a client script).
/// <para>
/// Instead of replacing Jellyfin's whole <c>Startup.Configure</c> pipeline (the File-Transformation
/// approach — large blast radius, one-version-fragile), we Prefix-patch the ASP.NET Core extension
/// <c>UseStaticFiles(IApplicationBuilder, StaticFileOptions)</c>. When Jellyfin registers the <c>/web</c>
/// static-file mount (RequestPath == "/web") during <c>Startup.Configure</c>, the Prefix swaps that one
/// <see cref="StaticFileOptions.FileProvider"/> for an <see cref="IndexHtmlInjectingFileProvider"/>.
/// Everything else in the pipeline runs untouched.
/// </para>
/// <para>
/// Harmony handling: <c>0Harmony.dll</c> is embedded as a resource (never copy-local — Jellyfin's loader
/// globs every top-level *.dll into a collectible context, which clashes with the resolver and throws
/// FileLoadException). It is loaded at runtime into a dedicated non-collectible
/// <see cref="AssemblyLoadContext"/>, and the patch is driven entirely by reflection — there is no
/// compile-time <c>HarmonyLib</c> reference, so no cross-context type resolution is required.
/// </para>
/// <para>
/// Timing: applied from the plugin constructor, which (via <c>PluginManager.CreatePlugins</c> →
/// <c>ApplicationHost.InitializeServices</c>) runs before <c>Startup.Configure</c>, so the Prefix is in
/// place when <c>UseStaticFiles</c> executes. Safety: every step is defensive — any failure leaves the
/// vanilla web client intact (worst case = no Gelo shelves, never a broken server). Disable via
/// <c>EnableWebUI</c> (request-time gate, no unpatch) or by disabling the plugin.
/// </para>
/// </summary>
internal static class HarmonyTransformer
{
    /// <summary>Unique Harmony owner id; used to patch idempotently and to unpatch on teardown.</summary>
    private const string OwnerId = "creaturecloud.gelo";

    private const string HarmonyResource = "Jellyfin.Plugin.Gelo.Harmony";
    private const string StaticFilesAssemblyName = "Microsoft.AspNetCore.StaticFiles";

    // The static-files extension class was renamed between ASP.NET Core versions — try both.
    private static readonly string[] ExtensionsTypeNames =
    {
        "Microsoft.AspNetCore.Builder.StaticFileExtensions",
        "Microsoft.AspNetCore.Builder.StaticFilesAppBuilderExtensions"
    };

    private static int _state; // 0 = not applied, 1 = applied, 2 = skipped/failed

    /// <summary>True once the patch has been successfully applied this process.</summary>
    public static bool IsApplied => _state == 1;

    /// <summary>Apply the patch once. Idempotent and exception-safe — never throws.</summary>
    public static void Apply()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
        {
            return;
        }

        try
        {
            if (!TryLoadHarmony(out var api))
            {
                _state = 2;
                return;
            }

            var extType = ResolveStaticFilesExtensionsType();
            if (extType is null)
            {
                _state = 2;
                Console.Error.WriteLine("[Gelo] web-patch: StaticFiles extensions type not found — web UI injection disabled (vanilla web).");
                return;
            }

            var original = api.AccessToolsMethod(
                extType,
                "UseStaticFiles",
                new[] { typeof(IApplicationBuilder), typeof(StaticFileOptions) });

            if (original is null)
            {
                _state = 2;
                Console.Error.WriteLine("[Gelo] web-patch: UseStaticFiles target not found — web UI injection disabled (vanilla web).");
                return;
            }

            var prefix = typeof(HarmonyTransformer)
                .GetMethod(nameof(UseStaticFilesPrefix), BindingFlags.NonPublic | BindingFlags.Static);

            api.Patch(original, prefix);
            Console.Error.WriteLine("[Gelo] web-patch: applied Prefix on UseStaticFiles (owner=" + OwnerId + ").");
        }
        catch (Exception ex)
        {
            _state = 2;
            // Must not propagate: this runs during plugin construction. Vanilla web stays intact.
            Console.Error.WriteLine("[Gelo] web-patch: apply FAILED (vanilla web, injection disabled): " + ex);
        }
    }

    /// <summary>Remove the patch. Only meaningful across a plugin reload; harmless when not applied.</summary>
    public static void Revert()
    {
        try
        {
            if (TryLoadHarmony(out var api))
            {
                api.UnpatchAll(OwnerId);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[Gelo] web-patch: revert error (ignored): " + ex.Message);
        }
    }

    /// <summary>
    /// Harmony Prefix for <c>UseStaticFiles(IApplicationBuilder, StaticFileOptions)</c>. Wraps the
    /// <c>/web</c> mount's file provider; lets all other static-file registrations pass through. Returns
    /// normally so the original method runs with the (possibly swapped) provider. Fully wrapped so a
    /// fault can never escape into the pipeline build.
    /// </summary>
    // Parameter name MUST be "options" — Harmony binds injected parameters by name to the original.
    private static void UseStaticFilesPrefix(StaticFileOptions options)
    {
        try
        {
            if (options is null)
            {
                return;
            }

            var requestPath = options.RequestPath;
            if (!requestPath.HasValue || !string.Equals(requestPath.Value, "/web", StringComparison.Ordinal))
            {
                return; // not the web-client mount (e.g. the default api-docs static files)
            }

            if (options.FileProvider is null || options.FileProvider is IndexHtmlInjectingFileProvider)
            {
                return; // nothing to wrap, or already wrapped (idempotent)
            }

            options.FileProvider = new IndexHtmlInjectingFileProvider(options.FileProvider);
            Console.Error.WriteLine("[Gelo] web-patch: wrapped /web file provider for index.html injection.");
        }
        catch (Exception ex)
        {
            // Swallow: the original UseStaticFiles then runs with the original provider → vanilla web.
            Console.Error.WriteLine("[Gelo] web-patch: prefix error (ignored, original provider retained): " + ex.Message);
        }
    }

    // ───────────────────────── Harmony load + reflection shim ─────────────────────────

    private static readonly object _loadLock = new();
    private static HarmonyApi? _cachedApi;
    private static bool _harmonyUnavailable;

    /// <summary>Loads (once) the embedded Harmony assembly and returns a reflection shim. Never throws.</summary>
    private static bool TryLoadHarmony(out HarmonyApi api)
    {
        lock (_loadLock)
        {
            if (_cachedApi is not null)
            {
                api = _cachedApi;
                return true;
            }

            if (_harmonyUnavailable)
            {
                api = default!;
                return false;
            }

            try
            {
                _cachedApi = HarmonyApi.Load();
                api = _cachedApi;
                return true;
            }
            catch (Exception ex)
            {
                _harmonyUnavailable = true;
                Console.Error.WriteLine("[Gelo] web-patch: failed to load embedded Harmony (vanilla web): " + ex.Message);
                api = default!;
                return false;
            }
        }
    }

    /// <summary>Resolves the static-files extension type by reflecting the (force-loaded) assembly.</summary>
    private static Type? ResolveStaticFilesExtensionsType()
    {
        Assembly? asm = null;
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (a.GetName().Name == StaticFilesAssemblyName)
            {
                asm = a;
                break;
            }
        }

        asm ??= Assembly.Load(new AssemblyName(StaticFilesAssemblyName));

        foreach (var name in ExtensionsTypeNames)
        {
            var t = asm.GetType(name, throwOnError: false);
            if (t is not null)
            {
                return t;
            }
        }

        return null;
    }

    /// <summary>A tiny reflection shim over the few Harmony members Gelo needs.</summary>
    private sealed class HarmonyApi
    {
        private readonly object _harmony;
        private readonly MethodInfo _accessToolsMethod; // AccessTools.Method(Type, string, Type[])
        private readonly ConstructorInfo _harmonyMethodCtor; // new HarmonyMethod(MethodInfo)
        private readonly MethodInfo _patch; // Harmony.Patch(MethodInfo, HarmonyMethod?, HarmonyMethod?, HarmonyMethod?, HarmonyMethod?)
        private readonly MethodInfo _unpatchAll; // Harmony.UnpatchAll(string)

        private HarmonyApi(object harmony, MethodInfo accessToolsMethod, ConstructorInfo harmonyMethodCtor, MethodInfo patch, MethodInfo unpatchAll)
        {
            _harmony = harmony;
            _accessToolsMethod = accessToolsMethod;
            _harmonyMethodCtor = harmonyMethodCtor;
            _patch = patch;
            _unpatchAll = unpatchAll;
        }

        public MethodInfo? AccessToolsMethod(Type type, string name, Type[] parameters)
            => _accessToolsMethod.Invoke(null, new object?[] { type, name, parameters, null }) as MethodInfo;

        public void Patch(MethodInfo original, MethodInfo? prefix)
        {
            object? harmonyMethod = prefix is null ? null : _harmonyMethodCtor.Invoke(new object[] { prefix });
            _patch.Invoke(_harmony, new object?[] { original, harmonyMethod, null, null, null });
        }

        public void UnpatchAll(string id) => _unpatchAll.Invoke(_harmony, new object[] { id });

        public static HarmonyApi Load()
        {
            byte[]? bytes;
            using (var rs = typeof(HarmonyTransformer).Assembly
                .GetManifestResourceStream(HarmonyResource))
            {
                if (rs is null)
                {
                    throw new InvalidOperationException("Embedded Harmony resource missing: " + HarmonyResource);
                }

                using var ms = new MemoryStream();
                rs.CopyTo(ms);
                bytes = ms.ToArray();
            }

            var alc = new GeloHarmonyLoadContext();
            var harmonyAsm = alc.LoadFromStream(new MemoryStream(bytes));

            var harmonyT = harmonyAsm.GetType("HarmonyLib.Harmony", throwOnError: false)
                ?? throw new InvalidOperationException("HarmonyLib.Harmony type not found");
            var accessT = harmonyAsm.GetType("HarmonyLib.AccessTools", throwOnError: false)
                ?? throw new InvalidOperationException("HarmonyLib.AccessTools type not found");
            var harmonyMethodT = harmonyAsm.GetType("HarmonyLib.HarmonyMethod", throwOnError: false)
                ?? throw new InvalidOperationException("HarmonyLib.HarmonyMethod type not found");

            var harmony = Activator.CreateInstance(harmonyT, OwnerId)
                ?? throw new InvalidOperationException("Could not create Harmony instance");

            // AccessTools.Method(Type, string, Type[]? parameters = null, Type[]? generics = null)
            // is a single method with optional args, so look up the full 4-param signature.
            var accessToolsMethod = accessT.GetMethod(
                "Method",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(Type), typeof(string), typeof(Type[]), typeof(Type[]) },
                null)
                ?? throw new InvalidOperationException("AccessTools.Method(Type,string,Type[],Type[]) not found");

            var harmonyMethodCtor = harmonyMethodT.GetConstructor(new[] { typeof(MethodInfo) })
                ?? throw new InvalidOperationException("HarmonyMethod(MethodInfo) ctor not found");

            // Patch(MethodInfo, HarmonyMethod?, HarmonyMethod?, HarmonyMethod?, HarmonyMethod?)
            var patch = harmonyT.GetMethod("Patch", new[] { typeof(MethodInfo), harmonyMethodT, harmonyMethodT, harmonyMethodT, harmonyMethodT })
                ?? throw new InvalidOperationException("Harmony.Patch(...) not found");

            var unpatchAll = harmonyT.GetMethod("UnpatchAll", new[] { typeof(string) })
                ?? throw new InvalidOperationException("Harmony.UnpatchAll(string) not found");

            return new HarmonyApi(harmony, accessToolsMethod, harmonyMethodCtor, patch, unpatchAll);
        }
    }

    /// <summary>
    /// Owns the loaded <c>0Harmony</c> assembly. Non-collectible so the patch survives a plugin reload
    /// and references it installs stay valid. Framework dependencies (System.Reflection.Emit, …) defer
    /// to the default context via <see cref="Load"/> returning null.
    /// </summary>
    private sealed class GeloHarmonyLoadContext : AssemblyLoadContext
    {
        public GeloHarmonyLoadContext() : base("Gelo.Harmony", isCollectible: false)
        {
        }

        /// <inheritdoc />
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Only the 0Harmony assembly lives here (loaded via LoadFromStream); let everything else
            // resolve from the default/shared-framework context.
            return null;
        }
    }
}
