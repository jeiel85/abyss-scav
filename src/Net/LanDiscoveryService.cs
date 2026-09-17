using AbyssScav.Protocol;
using Godot;

namespace AbyssScav.Net;

/// <summary>
/// LAN discovery over a dedicated UDP port (docs/02 §2B, docs/16 §2).
/// Beacon schema and TTL rules live in <see cref="LanBeaconCodec"/>; this class
/// only owns the <see cref="PacketPeerUdp"/> sockets. Drive <see cref="Poll"/>
/// from the main loop; malformed packets are dropped, never applied.
/// </summary>
public sealed class LanDiscoveryService : ILanDiscoveryService, IDisposable
{
    private const string BroadcastAddress = "255.255.255.255";

    private readonly LanSessionTable _table = new();
    private PacketPeerUdp? _transmit;
    private PacketPeerUdp? _receive;
    private LanSessionAdvertisement? _advertisement;
    private DateTimeOffset _lastBeacon = DateTimeOffset.MinValue;
    private bool _disposed;

    public void StartAdvertise(LanSessionAdvertisement info)
    {
        ThrowIfDisposed();
        if (!LanBeaconCodec.IsAdvertisable(info))
        {
            throw new ArgumentException($"[{NetErrors.DiscoveryMalformed}] Advertisement is invalid or exceeds beacon bounds.", nameof(info));
        }

        _transmit ??= new PacketPeerUdp();
        try
        {
            _transmit.SetBroadcastEnabled(true);
            _transmit.SetDestAddress(BroadcastAddress, NetLimits.DiscoveryPort);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"[{NetErrors.DiscoveryMalformed}] Broadcast setup failed: {ex.Message}.", ex);
        }

        _advertisement = info;
        _lastBeacon = DateTimeOffset.MinValue;
        Poll();
    }

    public void StopAdvertise()
    {
        _advertisement = null;
        Close(ref _transmit);
    }

    public void StartScan()
    {
        ThrowIfDisposed();
        if (_receive is not null)
        {
            return;
        }

        var socket = new PacketPeerUdp();
        var error = socket.Bind(NetLimits.DiscoveryPort);
        if (error != Error.Ok)
        {
            socket.Close();
            throw new InvalidOperationException(
                $"[{NetErrors.DiscoveryMalformed}] Cannot listen for LAN beacons on UDP {NetLimits.DiscoveryPort}: {error}. " +
                "Another scan on this machine may already hold the port.");
        }

        _receive = socket;
    }

    public IReadOnlyList<DiscoveredLanSession> Snapshot()
    {
        ThrowIfDisposed();
        return _table.Snapshot();
    }

    public void StopScan()
    {
        Close(ref _receive);
        _table.Clear();
    }

    /// <summary>Main-thread pump: emits one beacon per interval and drains inbound beacons.</summary>
    public void Poll()
    {
        if (_disposed)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (_advertisement is not null && _transmit is not null)
        {
            if ((now - _lastBeacon).TotalSeconds >= NetLimits.BeaconIntervalSeconds)
            {
                _lastBeacon = now;
                SendBeacon(_advertisement, now);
            }
        }

        var receiver = _receive;
        if (receiver is null)
        {
            return;
        }

        try
        {
            while (receiver.GetAvailablePacketCount() > 0)
            {
                byte[] packet;
                string sender;
                try
                {
                    packet = receiver.GetPacket();
                    sender = receiver.GetPacketIP();
                }
                catch
                {
                    break;
                }

                if (packet.Length == 0 || packet.Length > NetLimits.BeaconMaxBytes)
                {
                    continue;
                }

                if (LanBeaconCodec.TryDecode(packet, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), out var info) &&
                    info is not null)
                {
                    _table.Upsert(new DiscoveredLanSession(
                        info.LobbyName, info.GameVersion, info.ProtocolVersion,
                        info.Players, info.MaxPlayers, info.GamePort, info.SessionId,
                        sender, DateTimeOffset.UtcNow));
                }
            }
        }
        catch
        {
            // A sick socket must not take down the frame; next Poll retries.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _advertisement = null;
        Close(ref _transmit);
        Close(ref _receive);
        _table.Clear();
    }

    private void SendBeacon(LanSessionAdvertisement info, DateTimeOffset now)
    {
        Span<byte> buffer = stackalloc byte[NetLimits.BeaconMaxBytes];
        try
        {
            if (LanBeaconCodec.TryEncode(info, now.ToUnixTimeSeconds(), buffer, out var size))
            {
                _transmit?.PutPacket(buffer[..size].ToArray());
            }
        }
        catch
        {
            // Beacon loss is routine on LAN; the next interval retries.
        }
    }

    private static void Close(ref PacketPeerUdp? socket)
    {
        var owned = socket;
        socket = null;
        if (owned is null)
        {
            return;
        }

        try
        {
            owned.Close();
        }
        catch
        {
            // Close is best-effort and never throws.
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(LanDiscoveryService));
        }
    }
}
