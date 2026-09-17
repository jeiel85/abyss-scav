namespace AbyssScav.Domain;

/// <summary>
/// Deterministic 64-bit PRNG (SplitMix64) with explicit per-stream derivation.
/// <para>
/// World generation and the host simulation split one run seed into isolated
/// streams (terrain / poi / facility / loot / creature / event / pressure / ai /
/// sonar) so that reordering loot rolls can never shift creature placement.
/// The same seed plus the same <see cref="DomainConstants.CatalogVersion"/>
/// always reproduces the same layout and the same host-side event sequence for
/// identical inputs. Presentation-supplied inputs (ship pose, player actions)
/// intentionally remain outside determinism: they are gameplay, not generation.
/// </para>
/// </summary>
public struct DeterministicRandom
{
    private ulong _state;

    /// <summary>Creates a stream from an already-derived 64-bit seed.</summary>
    public DeterministicRandom(ulong seed)
    {
        _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;
    }

    /// <summary>
    /// Derives an isolated stream seed from a run seed and a stream tag.
    /// Tags are small constants defined at each call site (see
    /// <see cref="GenStreams"/>); changing a tag intentionally re-rolls that stream.
    /// </summary>
    public static ulong Derive(ulong runSeed, ulong streamTag)
    {
        ulong z = runSeed ^ (streamTag * 0x9E3779B97F4A7C15UL);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Next raw 64-bit value.</summary>
    public ulong NextU64()
    {
        _state += 0x9E3779B97F4A7C15UL;
        ulong z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform double in [0, 1).</summary>
    public double NextDouble() => (NextU64() >> 11) * (1.0 / 9007199254740992.0);

    /// <summary>Uniform float in [<paramref name="min"/>, <paramref name="max"/>).</summary>
    public float NextFloat(float min, float max)
    {
        if (max <= min) return min;
        return min + (float)NextDouble() * (max - min);
    }

    /// <summary>Uniform int in [<paramref name="minInclusive"/>, <paramref name="maxExclusive"/>).</summary>
    public int NextInt(int minInclusive, int maxExclusive)
    {
        if (maxExclusive <= minInclusive) return minInclusive;
        uint range = (uint)(maxExclusive - minInclusive);
        return minInclusive + (int)(NextU64() % range);
    }

    /// <summary>True with probability <paramref name="probability"/> (clamped to [0,1]).</summary>
    public bool Chance(double probability)
    {
        if (probability <= 0.0) return false;
        if (probability >= 1.0) return true;
        return NextDouble() < probability;
    }

    /// <summary>Uniform index into a non-empty span.</summary>
    public int PickIndex(int count)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        return (int)(NextU64() % (uint)count);
    }
}

/// <summary>Stream tags for run-seed derivation. Values are part of determinism: do not renumber.</summary>
public static class GenStreams
{
    /// <summary>Main trench route spline.</summary>
    public const ulong Terrain = 0x01;
    /// <summary>Branch routes and POI sockets.</summary>
    public const ulong Poi = 0x02;
    /// <summary>Facility/service node placement.</summary>
    public const ulong Facility = 0x03;
    /// <summary>Loot spawn table.</summary>
    public const ulong Loot = 0x04;
    /// <summary>Creature spawn table.</summary>
    public const ulong Creature = 0x05;
    /// <summary>Run-time random events (surges, brownouts, ghosts).</summary>
    public const ulong Event = 0x06;
    /// <summary>Pressure stress checks (host sim).</summary>
    public const ulong Pressure = 0x07;
    /// <summary>Creature FSM tie-breaks (host sim).</summary>
    public const ulong Ai = 0x08;
    /// <summary>Sonar jitter and ghost bearings (host sim).</summary>
    public const ulong Sonar = 0x09;
}
