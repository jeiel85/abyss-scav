using System.Numerics;

namespace AbyssScav.Domain;

/// <summary>One timestamped ship transform (docs/02 §9.5). Time is receiver-local seconds.</summary>
public readonly record struct ShipPoseSample(double Time, Vector3 Position, Quaternion Rotation, Vector3 Velocity);

/// <summary>
/// Snapshot interpolation for a remote submarine (docs/02 §9): samples are
/// buffered on the receiver's own clock and rendered
/// <see cref="DelaySeconds"/> in the past, so the proxy moves between two real
/// fixes instead of snapping to each packet. When the buffer starves the pose
/// is extrapolated along the last velocity for at most
/// <see cref="MaxExtrapolationSeconds"/>, then held. A jump larger than
/// <see cref="TeleportMeters"/> between consecutive fixes (emergency winch,
/// reconnect) resets the buffer so the proxy snaps instead of sliding through walls.
/// </summary>
public sealed class PoseInterpolationBuffer
{
    /// <summary>Render delay: inside the documented 100–150 ms band (≈ two 15 Hz fixes).</summary>
    public const double DelaySeconds = 0.12;

    public const double MaxExtrapolationSeconds = 0.25;

    public const float TeleportMeters = 40f;

    public const int Capacity = 32;

    private readonly List<ShipPoseSample> _samples = new(Capacity);

    public int Count => _samples.Count;

    /// <summary>Receiver time of the newest accepted fix (NaN when empty).</summary>
    public double LastReceiveTime => _samples.Count == 0 ? double.NaN : _samples[^1].Time;

    /// <summary>Set when the last push was a teleport; cleared by <see cref="ConsumeSnap"/>.</summary>
    public bool SnapPending { get; private set; }

    /// <summary>
    /// Adds a fix. Non-finite or out-of-order samples are ignored (returns
    /// false); a teleport-sized jump clears older history.
    /// </summary>
    public bool Push(ShipPoseSample sample)
    {
        if (!double.IsFinite(sample.Time) || !IsFinite(sample.Position) || !IsFinite(sample.Velocity) ||
            !IsFinite(sample.Rotation) || sample.Rotation.LengthSquared() < 1e-6f)
        {
            return false;
        }

        var normalized = sample with { Rotation = Quaternion.Normalize(sample.Rotation) };
        if (_samples.Count > 0)
        {
            var last = _samples[^1];
            if (normalized.Time <= last.Time)
            {
                return false;
            }

            if (Vector3.Distance(last.Position, normalized.Position) > TeleportMeters)
            {
                _samples.Clear();
                SnapPending = true;
            }
        }

        _samples.Add(normalized);
        if (_samples.Count > Capacity)
        {
            _samples.RemoveAt(0);
        }

        return true;
    }

    /// <summary>Returns and clears the teleport flag.</summary>
    public bool ConsumeSnap()
    {
        var snap = SnapPending;
        SnapPending = false;
        return snap;
    }

    public void Clear()
    {
        _samples.Clear();
        SnapPending = false;
    }

    /// <summary>True when nothing arrived for <paramref name="staleSeconds"/>.</summary>
    public bool IsStale(double now, double staleSeconds) =>
        _samples.Count == 0 || now - _samples[^1].Time > staleSeconds;

    /// <summary>Interpolated pose at <c>now - DelaySeconds</c>; false when empty.</summary>
    public bool TrySample(double now, out ShipPoseSample pose)
    {
        pose = default;
        if (_samples.Count == 0 || !double.IsFinite(now))
        {
            return false;
        }

        var renderTime = now - DelaySeconds;
        var first = _samples[0];
        if (renderTime <= first.Time)
        {
            pose = first with { Time = renderTime };
            return true;
        }

        for (var i = 1; i < _samples.Count; i++)
        {
            var b = _samples[i];
            if (renderTime > b.Time)
            {
                continue;
            }

            var a = _samples[i - 1];
            var t = (float)((renderTime - a.Time) / Math.Max(1e-6, b.Time - a.Time));
            pose = new ShipPoseSample(
                renderTime,
                Vector3.Lerp(a.Position, b.Position, t),
                Quaternion.Slerp(a.Rotation, b.Rotation, t),
                Vector3.Lerp(a.Velocity, b.Velocity, t));
            return true;
        }

        // Starved: extrapolate briefly along the newest velocity, then hold.
        var newest = _samples[^1];
        var ahead = (float)Math.Min(renderTime - newest.Time, MaxExtrapolationSeconds);
        pose = newest with { Time = renderTime, Position = newest.Position + newest.Velocity * ahead };
        return true;
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static bool IsFinite(Quaternion q) =>
        float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W);
}

/// <summary>
/// Large-correction blend for a displayed proxy position (docs/02 §9): small
/// errors follow the interpolated target exactly, errors above
/// <see cref="BlendThresholdMeters"/> ease in exponentially over roughly
/// <see cref="BlendTimeConstantSeconds"/>, and errors above
/// <see cref="PoseInterpolationBuffer.TeleportMeters"/> snap.
/// </summary>
public static class PoseCorrection
{
    public const float BlendThresholdMeters = 1.5f;
    public const float BlendTimeConstantSeconds = 0.15f;

    public static Vector3 Step(Vector3 displayed, Vector3 target, float dtSeconds)
    {
        if (!float.IsFinite(displayed.X) || !float.IsFinite(displayed.Y) || !float.IsFinite(displayed.Z))
        {
            return target;
        }

        var error = Vector3.Distance(displayed, target);
        if (error <= BlendThresholdMeters || error > PoseInterpolationBuffer.TeleportMeters)
        {
            return target;
        }

        var dt = Math.Clamp(dtSeconds, 0f, 0.5f);
        var alpha = 1f - MathF.Exp(-dt / BlendTimeConstantSeconds);
        return displayed + (target - displayed) * alpha;
    }
}

/// <summary>Why the host refused a client pose.</summary>
public enum PoseVerdict
{
    Accepted,
    NonFinite,
    BadRotation,
    TooFast,
    OutOfBounds,
    TooFrequent,
    ImpossibleJump,
}

/// <summary>
/// Host-side validation of client ship poses (docs/02 §9.5, §13). Values must be
/// finite, the orientation a near-unit quaternion, speed at most
/// <see cref="MaxSpeedMps"/>, the position inside the generated world's bounds,
/// arrivals no faster than <see cref="MinIntervalSeconds"/>, and the displacement
/// since the last accepted pose physically reachable. The one-use emergency
/// winch may relocate a ship once per player per run (flagged teleport).
/// A refused pose keeps the previous accepted one; it never desyncs the gate,
/// because each check is against the last <em>accepted</em> pose.
/// </summary>
public sealed class ShipPoseGate
{
    /// <summary>Well above the sub's boosted terminal speed (~33 m/s).</summary>
    public const float MaxSpeedMps = 60f;

    /// <summary>Arrival floor: twice the 15 Hz send rate absorbs jitter bunching.</summary>
    public const double MinIntervalSeconds = 1.0 / 30.0;

    /// <summary>Positional slack for jitter/rounding in the displacement check.</summary>
    public const float JumpSlackMeters = 12f;

    /// <summary>Margin beyond the outermost node/corridor clearance.</summary>
    public const float BoundsMarginMeters = 80f;

    private readonly Vector3 _min;
    private readonly Vector3 _max;
    private readonly Dictionary<ulong, (double Time, Vector3 Position)> _last = new();
    private readonly HashSet<ulong> _teleportUsed = new();

    public ShipPoseGate(GeneratedWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (world.Nodes.Count == 0)
        {
            throw new ArgumentException("World has no nodes.", nameof(world));
        }

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var node in world.Nodes)
        {
            min = Vector3.Min(min, node.Position);
            max = Vector3.Max(max, node.Position);
        }

        var clearance = world.Segments.Count == 0 ? 0f : world.Segments.Max(s => s.ClearanceRadiusMeters);
        var pad = new Vector3(clearance + BoundsMarginMeters);
        _min = min - pad;
        _max = max + pad;
    }

    public Vector3 BoundsMin => _min;

    public Vector3 BoundsMax => _max;

    /// <summary>
    /// Validates a pose from <paramref name="peerId"/> received at
    /// <paramref name="now"/> (host seconds). On acceptance the pose becomes the
    /// reference for the next check.
    /// </summary>
    public PoseVerdict Evaluate(ulong peerId, double now, Vector3 position, Quaternion rotation, Vector3 velocity, bool teleported)
    {
        if (!double.IsFinite(now) || !IsFinite(position) || !IsFinite(velocity) ||
            !float.IsFinite(rotation.X) || !float.IsFinite(rotation.Y) || !float.IsFinite(rotation.Z) || !float.IsFinite(rotation.W))
        {
            return PoseVerdict.NonFinite;
        }

        var qLen = rotation.Length();
        if (qLen < 0.9f || qLen > 1.1f)
        {
            return PoseVerdict.BadRotation;
        }

        if (velocity.Length() > MaxSpeedMps)
        {
            return PoseVerdict.TooFast;
        }

        if (position.X < _min.X || position.Y < _min.Y || position.Z < _min.Z ||
            position.X > _max.X || position.Y > _max.Y || position.Z > _max.Z)
        {
            return PoseVerdict.OutOfBounds;
        }

        if (_last.TryGetValue(peerId, out var last))
        {
            var dt = now - last.Time;
            if (dt < MinIntervalSeconds)
            {
                return PoseVerdict.TooFrequent;
            }

            var reach = MaxSpeedMps * (float)dt + JumpSlackMeters;
            if (Vector3.Distance(last.Position, position) > reach)
            {
                if (!teleported || _teleportUsed.Contains(peerId))
                {
                    return PoseVerdict.ImpossibleJump;
                }

                _teleportUsed.Add(peerId);
            }
        }

        _last[peerId] = (now, position);
        return PoseVerdict.Accepted;
    }

    /// <summary>
    /// The host's last <em>accepted</em> position for <paramref name="peerId"/>,
    /// if it is no older than <paramref name="maxAgeSeconds"/> at
    /// <paramref name="now"/>. Authority checks (e.g. extraction) use this
    /// instead of a position the client merely claims in an intent.
    /// </summary>
    public bool TryGetAccepted(ulong peerId, double now, double maxAgeSeconds, out Vector3 position)
    {
        position = default;
        if (!_last.TryGetValue(peerId, out var last) || !double.IsFinite(now) || now - last.Time > maxAgeSeconds)
        {
            return false;
        }

        position = last.Position;
        return true;
    }

    /// <summary>
    /// A reconnecting player keeps its history under the new peer id (the
    /// winch budget must not reset by reconnecting).
    /// </summary>
    public void Rekey(ulong oldPeer, ulong newPeer)
    {
        if (_last.Remove(oldPeer, out var last))
        {
            _last[newPeer] = last;
        }

        if (_teleportUsed.Remove(oldPeer))
        {
            _teleportUsed.Add(newPeer);
        }
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
