namespace AbyssScav.Protocol;

/// <summary>Client-side view of the in-run host link (docs/02 §12).</summary>
public enum HostLinkState
{
    /// <summary>Host heard recently; normal play.</summary>
    Connected,

    /// <summary>Host lost; the 8 s reconnect window is running.</summary>
    Reconnecting,

    /// <summary>The window expired without a restored link. Terminal: settle now.</summary>
    Lost,
}

/// <summary>
/// Engine-independent host-loss timer (docs/02 §12). A transport disconnect or
/// <see cref="NetLimits.HostSilenceSeconds"/> of silence (after the host was
/// heard at least once this run) opens a <see cref="NetLimits.HostLossWindowSeconds"/>
/// reconnect window; a restored link inside the window returns to
/// <see cref="HostLinkState.Connected"/>, otherwise the monitor latches
/// <see cref="HostLinkState.Lost"/> and the caller runs host-loss settlement.
/// Time is caller-supplied seconds (monotonic), so tests stay deterministic.
/// </summary>
public sealed class HostLinkMonitor
{
    private readonly double _windowSeconds;
    private readonly double _silenceSeconds;
    private double _lastHeard;
    private double _windowStart;
    private bool _heardOnce;

    public HostLinkMonitor(
        double windowSeconds = NetLimits.HostLossWindowSeconds,
        double silenceSeconds = NetLimits.HostSilenceSeconds)
    {
        if (!(windowSeconds > 0) || !(silenceSeconds > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(windowSeconds), "Window and silence must be positive.");
        }

        _windowSeconds = windowSeconds;
        _silenceSeconds = silenceSeconds;
    }

    public HostLinkState State { get; private set; } = HostLinkState.Connected;

    /// <summary>Why the window opened (diagnostics): "disconnect" or "silence".</summary>
    public string LossCause { get; private set; } = string.Empty;

    /// <summary>A snapshot or event from the host arrived.</summary>
    public void OnHostHeard(double now)
    {
        if (State == HostLinkState.Lost)
        {
            return;
        }

        _lastHeard = now;
        _heardOnce = true;
    }

    /// <summary>The transport reported the host gone. Opens the window once.</summary>
    public void OnHostDisconnected(double now)
    {
        if (State == HostLinkState.Connected)
        {
            OpenWindow(now, "disconnect");
        }
    }

    /// <summary>
    /// The session re-joined the same run inside the window. Returns false when
    /// the monitor already latched <see cref="HostLinkState.Lost"/> (settlement
    /// has begun; the caller must not resume play).
    /// </summary>
    public bool OnReconnected(double now)
    {
        if (State == HostLinkState.Lost)
        {
            return false;
        }

        State = HostLinkState.Connected;
        LossCause = string.Empty;
        _lastHeard = now;
        return true;
    }

    /// <summary>
    /// Ends the window immediately (e.g. the host re-sent a different run's
    /// manifest): the link can no longer be restored for this run.
    /// </summary>
    public void Abandon(string cause)
    {
        if (State == HostLinkState.Lost)
        {
            return;
        }

        State = HostLinkState.Lost;
        LossCause = string.IsNullOrWhiteSpace(cause) ? "abandoned" : cause;
    }

    /// <summary>Advances the timers and returns the current state.</summary>
    public HostLinkState Update(double now)
    {
        switch (State)
        {
            case HostLinkState.Connected:
                if (_heardOnce && now - _lastHeard >= _silenceSeconds)
                {
                    OpenWindow(now, "silence");
                }

                break;
            case HostLinkState.Reconnecting:
                if (now - _windowStart >= _windowSeconds)
                {
                    State = HostLinkState.Lost;
                }

                break;
        }

        return State;
    }

    /// <summary>Seconds left in the reconnect window (0 when not reconnecting).</summary>
    public double WindowRemaining(double now) =>
        State == HostLinkState.Reconnecting ? Math.Max(0.0, _windowSeconds - (now - _windowStart)) : 0.0;

    private void OpenWindow(double now, string cause)
    {
        State = HostLinkState.Reconnecting;
        LossCause = cause;
        _windowStart = now;
    }
}
