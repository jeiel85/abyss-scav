using System.Security.Cryptography;

namespace AbyssScav.Protocol;

/// <summary>
/// Session-scoped reconnect tokens (docs/02 §11). Tokens never identify a peer by
/// IP address, expire after the grace window, and are discarded when the run ends.
/// </summary>
public sealed class ReconnectTokenStore
{
    private sealed class Entry
    {
        public ulong PeerId;
        public ulong SessionId;
        public string DisplayName = string.Empty;
        public DateTimeOffset Expires;
    }

    private readonly Dictionary<string, Entry> _tokens = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<string> _mint;

    public ReconnectTokenStore(Func<DateTimeOffset>? clock = null, Func<string>? mint = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _mint = mint ?? MintDefault;
    }

    /// <summary>Issues (or rotates) the token for a peer within one session.</summary>
    public string Issue(ulong peerId, ulong sessionId, string displayName = "")
    {
        if (peerId == NetLimits.InvalidId || sessionId == NetLimits.InvalidId)
        {
            throw new ArgumentException("Peer and session ids must be non-zero.");
        }

        RevokePeer(peerId, sessionId);
        var token = _mint();
        _tokens[token] = new Entry
        {
            PeerId = peerId,
            SessionId = sessionId,
            DisplayName = displayName,
            Expires = _clock().AddSeconds(NetLimits.ReconnectGraceSeconds),
        };
        return token;
    }

    /// <summary>Validates that a token belongs to this session and has not expired.</summary>
    public bool TryTakeover(string token, ulong sessionId, out ulong peerId, out string displayName)
    {
        peerId = NetLimits.InvalidId;
        displayName = string.Empty;
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        if (!_tokens.TryGetValue(token, out var entry))
        {
            return false;
        }

        if (entry.SessionId != sessionId || entry.SessionId == NetLimits.InvalidId)
        {
            return false;
        }

        if (_clock() > entry.Expires)
        {
            _tokens.Remove(token);
            return false;
        }

        peerId = entry.PeerId;
        displayName = entry.DisplayName;
        return true;
    }

    public void RevokePeer(ulong peerId, ulong sessionId)
    {
        string? found = null;
        foreach (var (token, entry) in _tokens)
        {
            if (entry.PeerId == peerId && entry.SessionId == sessionId)
            {
                found = token;
                break;
            }
        }

        if (found is not null)
        {
            _tokens.Remove(found);
        }
    }

    /// <summary>Discards every token when the run ends (docs/02 §11).</summary>
    public void EndRun() => _tokens.Clear();

    public int ActiveCount => _tokens.Count;

    private static string MintDefault()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }
}
