using AbyssScav.Domain;
using AbyssScav.Foundation;
using Godot;
using SysVec = System.Numerics.Vector3;

namespace AbyssScav.Gameplay;

/// <summary>
/// Solo-only sonar buddy (docs/00 §8): reactive callouts, never auto-steering.
/// Consumes live <see cref="RunSimulation"/> state plus real forward physics
/// rays every 0.5 s: collision-risk range/bearing, first-time hostile
/// classification with confidence, pressure/noise threshold warnings with
/// safe-range re-arm, and a 5 s breadcrumb trail for the sonar scope. At most
/// one callout per 2.5 s; breach and threat&gt;85 preempt as critical.
/// Callouts are exposed via <see cref="CalloutRaised"/> (HUD line + blip) and
/// the <see cref="CalloutLog"/> test hook. No invented contacts: everything
/// derives from sim state or DirectSpaceState hits.
/// </summary>
public partial class SonarBuddy : Node
{
    public const float SenseIntervalSeconds = 0.5f;
    public const float CalloutCooldownSeconds = 2.5f;
    public const float CrumbIntervalSeconds = 5f;
    public const int MaxCrumbs = 40;
    public const float ObstacleRangeMeters = 60f;
    public const float ContactCloseMeters = 45f;

    /// <summary>Text + critical flag. The owner renders the HUD line and blip.</summary>
    public event Action<string, bool>? CalloutRaised;

    public bool Enabled { get; set; } = true;
    public string LastCallout { get; private set; } = "";
    public bool LastCalloutCritical { get; private set; }
    public readonly List<string> CalloutLog = new();
    public IReadOnlyList<Vector3> Trail => _trail;

    private readonly List<Vector3> _trail = new();
    private float _senseTimer;
    private float _crumbTimer;
    private float _sinceCallout = float.MaxValue;
    private string _lastText = "";
    private float _obstacleCooldown;
    private float _threatCooldown;
    private bool _pressureWarned;
    private bool _noiseWarned;
    private bool _breachWarned;
    private bool _winchSeen;
    private readonly HashSet<string> _announcedContacts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> _closeCooldowns = new(StringComparer.Ordinal);

    public override void _Ready()
    {
        _sinceCallout = float.MaxValue;
    }

    /// <summary>
    /// Feeds live state. Raycasts run against the real physics space; pass the
    /// hull body to exclude so the ship never reports itself as an obstacle.
    /// </summary>
    public void Tick(float dt, RunSimulation sim, Vector3 shipPos, Vector3 forward,
        PhysicsDirectSpaceState3D space, Rid excludeShip)
    {
        if (sim is null) return;
        _sinceCallout += dt;
        _obstacleCooldown = Math.Max(0f, _obstacleCooldown - dt);
        _threatCooldown = Math.Max(0f, _threatCooldown - dt);
        foreach (var key in _closeCooldowns.Keys.ToList())
        {
            _closeCooldowns[key] = Math.Max(0f, _closeCooldowns[key] - dt);
        }

        _crumbTimer += dt;
        if (_crumbTimer >= CrumbIntervalSeconds)
        {
            _crumbTimer = 0f;
            _trail.Add(shipPos);
            while (_trail.Count > MaxCrumbs) _trail.RemoveAt(0);
        }

        if (!Enabled) return;
        _senseTimer += dt;
        if (_senseTimer < SenseIntervalSeconds) return;
        _senseTimer = 0f;
        Evaluate(sim, shipPos, forward, space, excludeShip);
    }

    private void Evaluate(RunSimulation sim, Vector3 shipPos, Vector3 forward,
        PhysicsDirectSpaceState3D space, Rid excludeShip)
    {
        var fwd = forward.Length() > 0.01f ? forward.Normalized() : new Vector3(0f, 0f, -1f);

        // Critical first: breach preempts, then threat>85 (both ignore the 2.5 s gate).
        if (CheckBreach(sim)) return;
        if (sim.Threat > 85f && _threatCooldown <= 0f)
        {
            _threatCooldown = 20f;
            Emit(Localization.T("Threat critical at {0:F0} — go quiet (Z) or break contact.", sim.Threat), critical: true);
            return;
        }
        if (_sinceCallout < CalloutCooldownSeconds) return;

        if (CheckPressure(sim)) return;
        if (CheckNoise(sim)) return;
        if (CheckHostile(sim)) return;
        if (CheckProximity(sim, shipPos, fwd)) return;
        if (CheckObstacle(shipPos, fwd, space, excludeShip)) return;
        CheckWinch(sim);
    }

    private bool CheckBreach(RunSimulation sim)
    {
        var breached = sim.FloodZones.Any(z => z.Severity > 0);
        if (breached && !_breachWarned)
        {
            _breachWarned = true;
            Emit(Localization.T("Hull breach — repair (R) or winch (X) to safe water."), critical: true);
            return true;
        }
        if (!breached) _breachWarned = false;
        return false;
    }

    private bool CheckPressure(RunSimulation sim)
    {
        if (sim.PressureMargin < 0f && !_pressureWarned)
        {
            _pressureWarned = true;
            Emit(Localization.T("Pressure margin negative — ascend or ease the deeper load."), critical: false);
            return true;
        }
        if (sim.PressureMargin >= 1f) _pressureWarned = false; // re-arm in safe range.
        return false;
    }

    private bool CheckNoise(RunSimulation sim)
    {
        if (sim.Noise > 80f && !_noiseWarned)
        {
            _noiseWarned = true;
            Emit(Localization.T("Noise {0:F0} — loud hull, contacts closing. Ease thrust or go quiet.", sim.Noise), critical: false);
            return true;
        }
        if (sim.Noise <= 75f) _noiseWarned = false; // re-arm in safe range.
        return false;
    }

    private bool CheckHostile(RunSimulation sim)
    {
        foreach (var c in sim.Contacts)
        {
            if (c.Class != SonarClass.Biological || c.Confidence < 0.5f) continue;
            if (!_announcedContacts.Add(c.ContactId)) continue;
            Emit(Localization.T("New hostile contact, confidence {0} percent.", (int)Math.Round(c.Confidence * 100f)), critical: false);
            return true;
        }
        return false;
    }

    private bool CheckProximity(RunSimulation sim, Vector3 shipPos, Vector3 fwd)
    {
        CreatureSnapshot? nearest = null;
        var best = ContactCloseMeters;
        foreach (var c in sim.CreatureStates)
        {
            var d = (ToG(c.Position) - shipPos).Length();
            if (d >= best) continue;
            if (_closeCooldowns.TryGetValue(c.Id, out var cd) && cd > 0f) continue;
            best = d;
            nearest = c;
        }
        if (nearest is null) return false;
        _closeCooldowns[nearest.Id] = 30f;
        var dir = (ToG(nearest.Position) - shipPos).Normalized();
        Emit(Localization.T("Contact close aboard, {0:F0} meters, {1}.", best, Localization.T(BearingWord(fwd, dir))), critical: false);
        return true;
    }

    private bool CheckObstacle(Vector3 shipPos, Vector3 fwd, PhysicsDirectSpaceState3D space, Rid excludeShip)
    {
        if (_obstacleCooldown > 0f || space is null) return false;
        var right = fwd.Cross(Vector3.Up);
        if (right.Length() < 0.05f) right = Vector3.Right;
        right = right.Normalized();
        var best = ObstacleRangeMeters;
        var bestBearing = "";
        var dirs = new (Vector3 Dir, string Name)[]
        {
            (fwd, "dead ahead"),
            ((fwd + right * 0.45f).Normalized(), "bearing right"),
            ((fwd - right * 0.45f).Normalized(), "bearing left"),
        };
        foreach (var (dir, name) in dirs)
        {
            var query = PhysicsRayQueryParameters3D.Create(shipPos, shipPos + dir * ObstacleRangeMeters);
            query.CollideWithAreas = false;
            query.CollideWithBodies = true;
            query.Exclude = new Godot.Collections.Array<Rid> { excludeShip };
            var hit = space.IntersectRay(query);
            if (hit.Count == 0) continue;
            var at = (Vector3)hit["position"];
            var d = (at - shipPos).Length();
            if (d < best)
            {
                best = d;
                bestBearing = name;
            }
        }
        if (bestBearing == "") return false;
        _obstacleCooldown = 20f;
        Emit(Localization.T("Obstacle ahead, {0:F0} meters, {1}.", best, Localization.T(bestBearing)), critical: false);
        return true;
    }

    private bool CheckWinch(RunSimulation sim)
    {
        if (sim.WinchUsed && !_winchSeen)
        {
            _winchSeen = true;
            Emit(Localization.T("Winch fired — back at last safe water. Winch spent for this run."), critical: false);
            return true;
        }
        return false;
    }

    private void Emit(string text, bool critical)
    {
        if (!critical && string.Equals(text, _lastText, StringComparison.Ordinal)) return; // no same-line spam.
        _lastText = critical ? "" : text; // critical may repeat after the cooldown.
        LastCallout = text;
        LastCalloutCritical = critical;
        CalloutLog.Add((critical ? "[CRITICAL] " : "") + text);
        _sinceCallout = 0f;
        CalloutRaised?.Invoke(text, critical);
    }

    private static string BearingWord(Vector3 fwd, Vector3 dir)
    {
        var f = new Vector2(fwd.X, fwd.Z);
        var d = new Vector2(dir.X, dir.Z);
        if (f.Length() < 0.01f || d.Length() < 0.01f) return "dead ahead";
        f = f.Normalized();
        d = d.Normalized();
        var lateral = f.X * d.Y - f.Y * d.X; // signed: + means target right of bow.
        var along = f.Dot(d);
        if (along > 0.92f) return "dead ahead";
        return lateral > 0f ? "bearing right" : "bearing left";
    }

    private static Vector3 ToG(SysVec v) => new(v.X, v.Y, v.Z);
}
