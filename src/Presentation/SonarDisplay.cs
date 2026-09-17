using AbyssScav.Domain;
using Godot;
using SysVec = System.Numerics.Vector3;

namespace AbyssScav.Presentation;

/// <summary>
/// Signature sonar scope: circular sweep only (no generic cards). Glyphs for
/// terrain/loot/threat, compact compass strip on top, fading contact history.
/// Slow continuous sweep; no harsh flicker (alpha fades, nothing blinks).
/// </summary>
public partial class SonarDisplay : Control
{
    private float _sweep;
    private readonly List<Blip> _history = new();
    private string _objectiveText = "--";
    private float _objectiveBearing;
    private float _extractBearing;
    private float _nearestThreatRange = -1f;
    private bool _quiet;
    private readonly List<Vector3> _trailWorld = new();
    private Vector3 _trailShip;
    private float _trailHeading;
    private float _trailRange = 450f;

    private sealed record Blip(SonarClass Class, Vector2 Pos, float Age, float MaxRange);

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(240, 280);
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Process(double delta)
    {
        _sweep += (float)delta * 0.9f;
        if (_sweep > MathF.PI * 2f) _sweep -= MathF.PI * 2f;
        for (var i = _history.Count - 1; i >= 0; i--)
        {
            var b = _history[i] with { Age = _history[i].Age + (float)delta };
            _history[i] = b;
            if (b.Age > 14f) _history.RemoveAt(i);
        }
        QueueRedraw();
    }

    /// <summary>Feeds fresh contacts (world-relative to ship, rotated by heading).</summary>
    public void UpdateContacts(IReadOnlyList<ContactSnapshot> contacts, float pulseRange, Vector3 shipPos, float headingRad, SysVec objectivePos, SysVec extractPos, float nearestThreat, bool quiet)
    {
        _quiet = quiet;
        foreach (var c in contacts)
        {
            var approx = new Vector3(c.ApproxPosition.X, c.ApproxPosition.Y, c.ApproxPosition.Z);
            var d = ToLocal(approx, shipPos, headingRad, pulseRange);
            _history.Add(new Blip(c.Class, d, 0f, pulseRange));
        }
        // Compass bearings relative to heading.
        _objectiveBearing = BearingTo(shipPos, headingRad, objectivePos);
        _extractBearing = BearingTo(shipPos, headingRad, extractPos);
        _nearestThreatRange = nearestThreat;
        _trailShip = shipPos;
        _trailHeading = headingRad;
        _trailRange = Math.Max(pulseRange, 1f);
        if (_history.Count > 220)
        {
            _history.RemoveRange(0, _history.Count - 220);
        }
    }

    public void SetObjectiveText(string text) => _objectiveText = text;

    /// <summary>
    /// Sonar-buddy breadcrumb: recent ship fixes in world space, drawn as small
    /// cyan dots with the same ship-relative mapping as contacts. Defensive copy,
    /// capped well above the buddy ring buffer.
    /// </summary>
    public void SetTrail(IReadOnlyList<Vector3> worldPoints)
    {
        _trailWorld.Clear();
        if (worldPoints is null) return;
        var n = Math.Min(worldPoints.Count, 64);
        for (var i = 0; i < n; i++) _trailWorld.Add(worldPoints[i]);
    }

    private static Vector2 ToLocal(Vector3 contact, Vector3 ship, float heading, float range)
    {
        var dx = contact.X - ship.X;
        var dz = contact.Z - ship.Z;
        // Rotate so ship-forward (-Z rotated by heading) is scope-up.
        var cos = MathF.Cos(-heading);
        var sin = MathF.Sin(-heading);
        var lx = dx * cos - dz * sin;
        var lz = dx * sin + dz * cos;
        var r = 96f; // scope radius px
        return new Vector2(lx / Math.Max(range, 1f) * r, lz / Math.Max(range, 1f) * r);
    }

    private static float BearingTo(Vector3 ship, float heading, SysVec target)
    {
        var dx = target.X - ship.X;
        var dz = target.Z - ship.Z;
        var world = MathF.Atan2(dx, -dz);
        var rel = world - heading;
        while (rel > MathF.PI) rel -= MathF.PI * 2f;
        while (rel < -MathF.PI) rel += MathF.PI * 2f;
        return rel;
    }

    public override void _Draw()
    {
        var ink = new Color("#071622");
        var cyan = new Color("#71d9d1");
        var parchment = new Color("#dae4df");
        var amber = new Color("#dfa44d");
        var metal = new Color("#29414b");
        var font = ThemeDB.FallbackFont;

        var w = Size.X;
        var cx = w * 0.5f;
        var cy = 44f + 100f;
        var radius = 96f;

        // Compass strip.
        DrawRect(new Rect2(0, 0, w, 30), new Color(0.02f, 0.07f, 0.10f, 0.95f));
        DrawString(font, new Vector2(8, 20), "OBJ " + _objectiveText + "  " + RadToMark(_objectiveBearing), HorizontalAlignment.Left, -1, 13, parchment);
        var extMark = "EXT " + RadToMark(_extractBearing);
        DrawString(font, new Vector2(w - 8 - font.GetStringSize(extMark, HorizontalAlignment.Left, -1, 13).X, 20), extMark, HorizontalAlignment.Left, -1, 13, cyan);
        if (_nearestThreatRange >= 0f)
        {
            var warn = $"THREAT {_nearestThreatRange:F0}m" + (_quiet ? " - QUIET" : "");
            DrawString(font, new Vector2(8, 30 + 12), warn, HorizontalAlignment.Left, -1, 12, amber);
        }
        else if (_quiet)
        {
            DrawString(font, new Vector2(8, 42), "QUIET RUNNING", HorizontalAlignment.Left, -1, 12, cyan);
        }

        // Scope body.
        DrawCircle(new Vector2(cx, cy), radius + 6f, metal);
        DrawCircle(new Vector2(cx, cy), radius, ink);
        DrawArc(new Vector2(cx, cy), radius * 0.33f, 0f, MathF.PI * 2f, 48, new Color(cyan, 0.35f), 1f);
        DrawArc(new Vector2(cx, cy), radius * 0.66f, 0f, MathF.PI * 2f, 48, new Color(cyan, 0.35f), 1f);
        DrawArc(new Vector2(cx, cy), radius, 0f, MathF.PI * 2f, 64, cyan, 1.5f);
        DrawLine(new Vector2(cx - radius, cy), new Vector2(cx + radius, cy), new Color(cyan, 0.25f), 1f);
        DrawLine(new Vector2(cx, cy - radius), new Vector2(cx, cy + radius), new Color(cyan, 0.25f), 1f);

        // Sweep: soft wedge + leading line, slow, no strobe.
        var dir = new Vector2(MathF.Sin(_sweep), -MathF.Cos(_sweep));
        var trail = 0.7f;
        var pts = new Vector2[] { new Vector2(cx, cy) };
        var plist = new System.Collections.Generic.List<Vector2>(pts);
        for (var i = 0; i <= 12; i++)
        {
            var a = _sweep - trail * i / 12f;
            plist.Add(new Vector2(cx, cy) + new Vector2(MathF.Sin(a), -MathF.Cos(a)) * radius);
        }
        DrawColoredPolygon(plist.ToArray(), new Color(cyan, 0.10f));
        DrawLine(new Vector2(cx, cy), new Vector2(cx, cy) + dir * radius, new Color(cyan, 0.8f), 1.5f);

        // Ship wedge at center, pointing scope-up.
        var nose = new Vector2(cx, cy - 7f);
        var bl = new Vector2(cx - 5f, cy + 5f);
        var br = new Vector2(cx + 5f, cy + 5f);
        DrawColoredPolygon(new Vector2[] { nose, bl, br }, parchment);

        foreach (var b in _history)
        {
            var fade = 1f - b.Age / 14f;
            var p = new Vector2(cx, cy) + b.Pos;
            if ((p - new Vector2(cx, cy)).Length() > radius) continue;
            var col = b.Class switch
            {
                SonarClass.Salvage => parchment,
                SonarClass.Biological => amber,
                SonarClass.Structure => cyan,
                SonarClass.Unknown => new Color(0.6f, 0.6f, 0.65f),
                _ => new Color(0.45f, 0.55f, 0.55f),
            };
            DrawBlip(p, b.Class, new Color(col, 0.25f + 0.75f * fade));
        }

        // Buddy breadcrumb: small steady cyan dots, no motion or flashing.
        foreach (var fix in _trailWorld)
        {
            var p = new Vector2(cx, cy) + ToLocal(fix, _trailShip, _trailHeading, _trailRange);
            if ((p - new Vector2(cx, cy)).Length() > radius) continue;
            DrawCircle(p, 2f, new Color(cyan, 0.75f));
        }
    }

    private void DrawBlip(Vector2 p, SonarClass cls, Color col)
    {
        switch (cls)
        {
            case SonarClass.Salvage: // diamond
                DrawColoredPolygon(new Vector2[] { p + new Vector2(0, -5), p + new Vector2(5, 0), p + new Vector2(0, 5), p + new Vector2(-5, 0) }, col);
                break;
            case SonarClass.Biological: // triangle
                DrawColoredPolygon(new Vector2[] { p + new Vector2(0, -6), p + new Vector2(5, 4), p + new Vector2(-5, 4) }, col);
                break;
            case SonarClass.Structure: // square
                DrawRect(new Rect2(p - new Vector2(4, 4), new Vector2(8, 8)), col);
                break;
            case SonarClass.Unknown: // hollow
                DrawArc(p, 4f, 0f, MathF.PI * 2f, 16, col, 1.5f);
                break;
            default: // terrain dot
                DrawCircle(p, 2.5f, col);
                break;
        }
    }

    private static string RadToMark(float rel)
    {
        // Compact 12-point compass mark relative to bow.
        var deg = rel * 180f / MathF.PI;
        var arrow = Math.Abs(deg) < 15f ? "^" : deg > 0 ? ">" : "<";
        return $"{arrow} {Math.Abs(deg):F0}°";
    }
}
