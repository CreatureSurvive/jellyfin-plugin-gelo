// SPDX-FileCopyrightText: 2026 Dana Buehre
// SPDX-License-Identifier: GPL-3.0-or-later

using System;

namespace Jellyfin.Plugin.Gelo.Recs;

/// <summary>
/// Deterministic PRNG utilities: a SplitMix64 generator for per-refresh rotation and an FNV-1a hash
/// for stable per-key salts. Reseeded once per shelf build so picks vary across refreshes but stay
/// consistent within one.
/// </summary>
internal sealed class SeededRandom
{
    private ulong _state;

    public SeededRandom(ulong seed) => _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;

    /// <summary>SplitMix64 next.</summary>
    public ulong Next64()
    {
        _state += 0x9E3779B97F4A7C15UL;
        ulong z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform double in [0,1).</summary>
    public double NextDouble() => (Next64() >> 11) * (1.0 / (1UL << 53));

    /// <summary>Uniform int in [0, maxExclusive).</summary>
    public int NextInt(int maxExclusive)
        => maxExclusive <= 0 ? 0 : (int)(Next64() % (ulong)maxExclusive);

    /// <summary>FNV-1a 64-bit hash of a string (stable salt per key).</summary>
    public static ulong StableSeed(string key)
    {
        ulong hash = 0xcbf29ce484222325UL;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(key))
        {
            hash ^= b;
            hash *= 0x100000001b3UL;
        }

        return hash;
    }
}
