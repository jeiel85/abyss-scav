namespace AbyssScav.Protocol;

/// <summary>Per-ship state bits carried with a pose (docs/02 §9.5).</summary>
[Flags]
public enum ShipPoseFlags : byte
{
    None = 0,
    Quiet = 1 << 0,
    Docked = 1 << 1,
    Drilling = 1 << 2,
    Extracted = 1 << 3,
    Failed = 1 << 4,

    /// <summary>The ship relocated discontinuously (one-use emergency winch).</summary>
    Teleported = 1 << 5,
}

/// <summary>
/// One submarine transform on the wire (docs/02 §9.5): domain-space position,
/// Godot-basis orientation quaternion, linear velocity, hull percent and state
/// flags. 46 bytes. <see cref="PeerId"/> identifies the owning session peer; the
/// host overwrites it with the transport-reported sender, so a client can
/// never publish a pose for someone else.
/// </summary>
public sealed record ShipPoseWire(
    uint PeerId,
    float X,
    float Y,
    float Z,
    float Qx,
    float Qy,
    float Qz,
    float Qw,
    float Vx,
    float Vy,
    float Vz,
    byte HullPercent,
    ShipPoseFlags Flags);

/// <summary>
/// Binary pose codec. The client → host <see cref="MessageType.ShipPose"/>
/// payload is <c>session_id u64 + pose</c>; the host rebroadcasts every
/// player's latest validated pose inside <see cref="WorldSnapshot.Ships"/>.
/// Decoding rejects non-finite floats, unknown flag bits and hull &gt; 100 so
/// garbage never reaches interpolation or validation.
/// </summary>
public static class ShipPoseCodec
{
    public const int PoseBytes = 4 + 12 + 16 + 12 + 1 + 1;
    public const int MessageBytes = 8 + PoseBytes;

    private const ShipPoseFlags KnownFlags =
        ShipPoseFlags.Quiet | ShipPoseFlags.Docked | ShipPoseFlags.Drilling |
        ShipPoseFlags.Extracted | ShipPoseFlags.Failed | ShipPoseFlags.Teleported;

    public static bool TryEncodeMessage(ulong sessionId, ShipPoseWire pose, Span<byte> destination, out int written)
    {
        written = 0;
        if (sessionId == NetLimits.InvalidId || pose is null || !IsWellFormed(pose) ||
            destination.Length < MessageBytes || MessageBytes > MessageBounds.MaxPayload(MessageType.ShipPose))
        {
            return false;
        }

        BitConverter.TryWriteBytes(destination, sessionId);
        var at = 8;
        Write(destination, ref at, pose);
        written = at;
        return true;
    }

    public static bool TryDecodeMessage(ReadOnlySpan<byte> payload, out ulong sessionId, out ShipPoseWire? pose)
    {
        sessionId = NetLimits.InvalidId;
        pose = null;
        if (payload.Length != MessageBytes)
        {
            return false;
        }

        sessionId = BitConverter.ToUInt64(payload[..8]);
        var at = 8;
        if (sessionId == NetLimits.InvalidId || !TryRead(payload, ref at, out pose))
        {
            pose = null;
            return false;
        }

        return true;
    }

    /// <summary>Finite floats, known flags only, hull within 0-100.</summary>
    public static bool IsWellFormed(ShipPoseWire pose) =>
        float.IsFinite(pose.X) && float.IsFinite(pose.Y) && float.IsFinite(pose.Z) &&
        float.IsFinite(pose.Qx) && float.IsFinite(pose.Qy) && float.IsFinite(pose.Qz) && float.IsFinite(pose.Qw) &&
        float.IsFinite(pose.Vx) && float.IsFinite(pose.Vy) && float.IsFinite(pose.Vz) &&
        pose.HullPercent <= 100 && (pose.Flags & ~KnownFlags) == 0;

    internal static void Write(Span<byte> destination, ref int at, ShipPoseWire pose)
    {
        BitConverter.TryWriteBytes(destination[at..], pose.PeerId);
        at += 4;
        foreach (var value in new[] { pose.X, pose.Y, pose.Z, pose.Qx, pose.Qy, pose.Qz, pose.Qw, pose.Vx, pose.Vy, pose.Vz })
        {
            BitConverter.TryWriteBytes(destination[at..], value);
            at += 4;
        }

        destination[at++] = pose.HullPercent;
        destination[at++] = (byte)pose.Flags;
    }

    internal static bool TryRead(ReadOnlySpan<byte> source, ref int at, out ShipPoseWire? pose)
    {
        pose = null;
        if (at < 0 || at + PoseBytes > source.Length)
        {
            return false;
        }

        var peer = BitConverter.ToUInt32(source.Slice(at, 4));
        at += 4;
        var f = new float[10];
        for (var i = 0; i < f.Length; i++)
        {
            f[i] = BitConverter.ToSingle(source.Slice(at, 4));
            at += 4;
        }

        var hull = source[at++];
        var flags = (ShipPoseFlags)source[at++];
        var candidate = new ShipPoseWire(peer, f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], hull, flags);
        if (!IsWellFormed(candidate))
        {
            return false;
        }

        pose = candidate;
        return true;
    }
}
