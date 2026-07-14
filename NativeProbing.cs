// SPDX-FileCopyrightText: 2026 Dana Buehre
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Jellyfin.Plugin.Gelo;

/// <summary>
/// Resolves native libraries (onnxruntime, e_sqlite3, ...) bundled inside this plugin's
/// folder when running under Jellyfin's collectible plugin AssemblyLoadContext, where the
/// default native search path does not include the plugin directory.
/// </summary>
/// <remarks>
/// Two-pronged, belt-and-suspenders strategy:
/// <list type="bullet">
///   <item><see cref="SetPluginDirectory"/> <b>eagerly preloads</b> every bundled .so by full
///   path via <see cref="NativeLibrary.TryLoad(string, out IntPtr)"/> and logs the outcome. This
///   runs in the Plugin ctor on every boot cycle, so even short-lived crash cycles leave evidence
///   of whether the natives load. A successful preload also warms the process's dlopen cache.</item>
///   <item><see cref="RegisterKnown"/> + <see cref="AppDomain.AssemblyLoad"/> attach a
///   <see cref="DllImportResolver"/> to every relevant assembly (loaded or lazily-arriving) so the
///   first P/Invoke resolves to the bundled handle regardless of the ALC's native search path.</item>
/// </list>
/// If a library genuinely cannot load, <see cref="Resolve"/> returns <see cref="IntPtr.Zero"/> so
/// the runtime throws a catchable <see cref="DllNotFoundException"/> (logged) instead of ever
/// handing back a bad pointer.
/// </remarks>
internal static class NativeProbing
{
    private static string? _pluginDir;
    private static int _assemblyLoadHookInstalled; // 0/1 gate

    // Assemblies that transitively declare [DllImport] into OUR bundled native (onnxruntime).
    // NOTE: SQLite is intentionally absent — Jellyfin CORE owns Microsoft.Data.Sqlite + SQLitePCLRaw
    // (we ExcludeAssets=runtime it). Registering a resolver on core's SQLite assemblies would hijack
    // core's sqlite resolution and re-introduce the crash, so we only ever touch ONNX here.
    private static readonly string[] _knownAssemblyPrefixes =
    {
        "OnnxRuntime",
        "Microsoft.ML"
    };

    // Every native we ship, in dependency order (providers_shared first; onnxruntime depends on it).
    private static readonly string[] _bundledNatives =
    {
        "libonnxruntime_providers_shared.so",
        "libonnxruntime.so"
    };

    public static void SetPluginDirectory(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
        {
            return;
        }

        _pluginDir = dir;

        // Install the lazy-load hook once so resolver attaches even if a dep loads after RegisterKnown.
        if (System.Threading.Interlocked.Exchange(ref _assemblyLoadHookInstalled, 1) == 0)
        {
            try { AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad; }
            catch (Exception) { /* unsupported — RegisterKnown snapshot still applies */ }
        }

        PreloadBundledNatives();
    }

    private static void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs e)
    {
        try
        {
            var name = e.LoadedAssembly.GetName().Name ?? string.Empty;
            foreach (var prefix in _knownAssemblyPrefixes)
            {
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    Register(e.LoadedAssembly);
                    break;
                }
            }
        }
        catch (Exception)
        {
            // never let an assembly-load hook throw
        }
    }

    /// <summary>Attach the resolver to one specific assembly (call before first P/Invoke).</summary>
    public static void Register(Assembly assembly)
    {
        try
        {
            NativeLibrary.SetDllImportResolver(assembly, Resolve);
        }
        catch (InvalidOperationException)
        {
            // Already registered — fine.
        }
        catch (Exception)
        {
            // Unsupported on this runtime — ignore; default probing will be used.
        }
    }

    /// <summary>Attach the resolver to every currently-loaded assembly we care about.</summary>
    public static void RegisterKnown()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = asm.GetName().Name ?? string.Empty;
            foreach (var prefix in _knownAssemblyPrefixes)
            {
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    Register(asm);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Eagerly dlopen every bundled native and log the outcome. Diagnostic gold: this runs in the
    /// Plugin ctor, so even crash-loop cycles that never reach hosted services will log whether the
    /// natives load. Failures are logged but do not throw (default resolution still gets a chance).
    /// </summary>
    private static void PreloadBundledNatives()
    {
        if (string.IsNullOrEmpty(_pluginDir))
        {
            return;
        }

        foreach (var fileName in _bundledNatives)
        {
            var path = Path.Combine(_pluginDir, fileName);
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"[Gelo] native preload: MISSING {path}");
                continue;
            }

            try
            {
                if (NativeLibrary.TryLoad(path, out _))
                {
                    Console.Error.WriteLine($"[Gelo] native preload: OK {fileName}");
                }
                else
                {
                    Console.Error.WriteLine($"[Gelo] native preload: FAILED (TryLoad=false) {path}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Gelo] native preload: THREW {fileName}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (string.IsNullOrEmpty(_pluginDir))
        {
            return IntPtr.Zero;
        }

        var rid = RuntimeInformation.RuntimeIdentifier;
        var dirs = new[]
        {
            Path.Combine(_pluginDir, "runtimes", rid, "native"),
            Path.Combine(_pluginDir, "runtimes", "linux-x64", "native"),
            Path.Combine(_pluginDir, "runtimes", "linux", "native"),
            Path.Combine(_pluginDir, "runtimes", "win-x64", "native"),
            _pluginDir
        };

        foreach (var fileName in CandidateFileNames(libraryName))
        {
            foreach (var dir in dirs)
            {
                var path = Path.Combine(dir, fileName);
                if (NativeLibrary.TryLoad(path, out var handle))
                {
                    return handle;
                }
            }
        }

        return IntPtr.Zero; // fall back to the default resolver (yields a catchable DllNotFoundException)
    }

    private static IEnumerable<string> CandidateFileNames(string libraryName)
    {
        var name = libraryName;
        var ext = Path.GetExtension(name);
        if (!string.IsNullOrEmpty(ext))
        {
            name = Path.GetFileNameWithoutExtension(name);
        }

        yield return "lib" + name + ".so";
        yield return name + ".so";
        yield return "lib" + name + ".dylib";
        yield return name + ".dylib";
        yield return name + ".dll";
    }
}
