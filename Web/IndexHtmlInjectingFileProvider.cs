// SPDX-FileCopyrightText: 2026 Dana Buehre
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Gelo.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace Jellyfin.Plugin.Gelo.Web;

/// <summary>
/// Wraps Jellyfin's real <c>/web</c> <see cref="PhysicalFileProvider"/> so that the served
/// <c>index.html</c> carries the Gelo client script + style tags. Every other file (webpack chunks,
/// images, fonts) passes through untouched.
/// <para>
/// The transformation is live-gated on <see cref="GeloPluginConfiguration.EnableWebUI"/> (and the
/// plugin kill-switch): <c>index.html</c> is served with <c>Cache-Control: no-cache</c>, so flipping
/// the setting takes effect on the next page load — no restart required. <see cref="IFileInfo.Length"/>
/// always reports the byte count that <see cref="IFileInfo.CreateReadStream"/> returns, so there is no
/// Content-Length desync against response compression (the static-file middleware derives all headers
/// from this <see cref="IFileInfo"/>).
/// </para>
/// </summary>
internal sealed class IndexHtmlInjectingFileProvider : IFileProvider
{
    private readonly IFileProvider _inner;

    public IndexHtmlInjectingFileProvider(IFileProvider inner)
    {
        _inner = inner;
    }

    /// <inheritdoc />
    public IFileInfo GetFileInfo(string subpath)
    {
        var info = _inner.GetFileInfo(subpath);
        if (info is null || !info.Exists || info.IsDirectory)
        {
            // Pass the (possibly not-found) marker straight through. PhysicalFileProvider never
            // returns null in practice, but the interface contract is nullable.
            return info!;
        }

        // Only the SPA entry point is rewritten — match by name so we never touch chunk files,
        // and never apply this to a non-home index.html under a nested folder by accident.
        if (!string.Equals(info.Name, "index.html", StringComparison.OrdinalIgnoreCase))
        {
            return info;
        }

        return new InjectedIndexHtmlFileInfo(info);
    }

    /// <inheritdoc />
    public IDirectoryContents GetDirectoryContents(string subpath) => _inner.GetDirectoryContents(subpath);

    /// <inheritdoc />
    public IChangeToken Watch(string pattern) => _inner.Watch(pattern);

    /// <summary>
    /// A stable, per-build cache-bust token derived from the embedded <c>gelo.js</c> content, so a
    /// plugin update always forces clients to re-fetch the assets even though <c>index.html</c> itself
    /// is no-cache. Falls back to the assembly version if the resource can't be read.
    /// </summary>
    private static string AssetVersion => LazyAssetVersion.Value;

    private static readonly Lazy<string> LazyAssetVersion = new(() =>
    {
        try
        {
            using var s = typeof(IndexHtmlInjectingFileProvider)
                .Assembly
                .GetManifestResourceStream("Jellyfin.Plugin.Gelo.Web.assets.gelo.js");
            if (s is not null)
            {
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                var hash = SHA1.HashData(ms.ToArray());
                return Convert.ToHexString(hash.AsSpan(0, 4)).ToLowerInvariant(); // 8 hex chars
            }
        }
        catch
        {
            // Fall through to the assembly-version fallback below.
        }

        return typeof(IndexHtmlInjectingFileProvider).Assembly.GetName().Version?.ToString() ?? "1";
    });

    /// <summary>
    /// An <see cref="IFileInfo"/> over <c>index.html</c> that returns transformed bytes when the Web UI
    /// is enabled and the original bytes otherwise. Both byte arrays are cached; the enable-gate is read
    /// fresh on each access so a settings change is reflected without a restart.
    /// </summary>
    private sealed class InjectedIndexHtmlFileInfo : IFileInfo
    {
        private readonly IFileInfo _inner;
        private byte[]? _originalBytes;
        private byte[]? _injectedBytes;
        private readonly object _lock = new();

        public InjectedIndexHtmlFileInfo(IFileInfo inner) => _inner = inner;

        /// <inheritdoc />
        public bool Exists => _inner.Exists;

        /// <inheritdoc />
        public bool IsDirectory => false;

        /// <inheritdoc />
        public string Name => _inner.Name;

        /// <summary>
        /// Deliberately null: a transformed file has no physical backing on disk. The static-file
        /// middleware otherwise zero-copies from <see cref="IFileInfo.PhysicalPath"/> (the original
        /// disk bytes) and trusts <see cref="Length"/> for the Content-Length header — which would
        /// desync against the injected stream. A null path forces it through
        /// <see cref="CreateReadStream"/>, where the bytes and length agree.
        /// </summary>
        public string? PhysicalPath => null;

        /// <inheritdoc />
        public DateTimeOffset LastModified => _inner.LastModified;

        /// <inheritdoc />
        /// <remarks>Picked to match <see cref="CreateReadStream"/> for the current gate state.</remarks>
        public long Length => IsInjectionEnabled() ? GetInjected().Length : GetOriginal().Length;

        /// <inheritdoc />
        public Stream CreateReadStream()
        {
            var bytes = IsInjectionEnabled() ? GetInjected() : GetOriginal();
            // Read-only MemoryStream over the shared cache so callers cannot mutate it.
            return new MemoryStream(bytes, 0, bytes.Length, writable: false, publiclyVisible: false);
        }

        private static bool IsInjectionEnabled()
            => Plugin.Instance?.Configuration is { EnableWebUI: true, EnablePlugin: true };

        private byte[] GetOriginal()
        {
            if (_originalBytes is not null)
            {
                return _originalBytes;
            }

            lock (_lock)
            {
                _originalBytes ??= ReadAll(_inner.CreateReadStream());
                return _originalBytes;
            }
        }

        private byte[] GetInjected()
        {
            if (_injectedBytes is not null)
            {
                return _injectedBytes;
            }

            lock (_lock)
            {
                if (_injectedBytes is null)
                {
                    var html = Encoding.UTF8.GetString(GetOriginal());
                    var ver = AssetVersion;
                    var inject =
                        "<link rel=\"stylesheet\" href=\"/CustomRecommendations/web/gelo.css?v=" + ver + "\">" +
                        "<script defer src=\"/CustomRecommendations/web/gelo.js?v=" + ver + "\"></script>";

                    var idx = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                    var outHtml = idx >= 0
                        ? string.Concat(html.AsSpan(0, idx), inject, html.AsSpan(idx))
                        : html + inject;

                    _injectedBytes = Encoding.UTF8.GetBytes(outHtml);
                }

                return _injectedBytes;
            }
        }

        private static byte[] ReadAll(Stream stream)
        {
            using (stream)
            using (var ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                return ms.ToArray();
            }
        }
    }
}
