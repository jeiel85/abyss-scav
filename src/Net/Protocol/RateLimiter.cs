namespace AbyssScav.Protocol;

/// <summary>Token-bucket limiter. Clocks are injected so tests stay deterministic.</summary>
public sealed class TokenBucket
{
    private readonly double _perSecond;
    private readonly double _capacity;
    private double _tokens;
    private DateTimeOffset _last;

    public TokenBucket(double perSecond, double burst, DateTimeOffset now)
    {
        _perSecond = Math.Max(0.0, perSecond);
        _capacity = Math.Max(1.0, burst);
        _tokens = _capacity;
        _last = now;
    }

    public bool TryConsume(DateTimeOffset now)
    {
        var elapsed = (now - _last).TotalSeconds;
        if (elapsed > 0)
        {
            _tokens = Math.Min(_capacity, _tokens + elapsed * _perSecond);
            _last = now;
        }

        if (_tokens < 1.0)
        {
            return false;
        }

        _tokens -= 1.0;
        return true;
    }
}

/// <summary>Per-peer RPC rate limiter (docs/02 §13). Bounded peer count.</summary>
public sealed class PeerRateLimiter
{
    private readonly Dictionary<ulong, (TokenBucket Reliable, TokenBucket Unreliable)> _buckets = new();
    private readonly int _maxEntries;

    public PeerRateLimiter(int maxEntries = NetLimits.MaxPeers * 2)
    {
        _maxEntries = Math.Max(NetLimits.MaxPeers, maxEntries);
    }

    public bool TryConsume(ulong peerId, MessageType type, DateTimeOffset now)
    {
        if (peerId == NetLimits.InvalidId || !MessageBounds.IsKnown(type))
        {
            return false;
        }

        if (!_buckets.TryGetValue(peerId, out var pair))
        {
            if (_buckets.Count >= _maxEntries)
            {
                return false;
            }

            pair = (new TokenBucket(NetLimits.ReliablePerSecond, NetLimits.ReliableBurst, now),
                    new TokenBucket(NetLimits.UnreliablePerSecond, NetLimits.UnreliableBurst, now));
            _buckets[peerId] = pair;
        }

        return MessageBounds.IsReliable(type)
            ? pair.Reliable.TryConsume(now)
            : pair.Unreliable.TryConsume(now);
    }

    public void Forget(ulong peerId) => _buckets.Remove(peerId);

    public void Clear() => _buckets.Clear();
}
