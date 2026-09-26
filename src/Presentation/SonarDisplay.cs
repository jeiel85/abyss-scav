using AbyssScav.Domain;
using AbyssScav.Foundation;
using Godot;
using SysVec = System.Numerics.Vector3;

namespace AbyssScav.Presentation;

/// <summary>
/// Signature sonar scope: circular sweep only (no generic cards). Glyphs for
/// terrain/loot/threat, compact compass strip on top, fading contact history.
/// Slow continuous sweep; no harsh flicker (alpha fades, nothing blinks).
/// Colour is never the only cue (docs/07 §3): every contact kind has its own
/// glyph (salvage diamond, biological triangle, structure square, unknown "?"
/// ring, terrain dash) and a glyph legend sits under the scope. Colours come
/// from the selected colour-vision palette; high contrast adds opaque strips,
/// outlined text, larger outlined glyphs and a heavier rim.
/// </summary>
public partial class SonarDisplay : Control
{
    private HudTheme _theme = new(SonarPalette.Standard, false);
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
        CustomMinimumSize = new Vector2(240, 300);
        MouseFilter = MouseFilterEnum.Ignore;
    }

    /// <summary>Palette + contrast mode; call before the first draw (RunHud does on build).</summary>
    public void ApplyHudTheme(HudTheme theme)
    {
        _theme = theme ?? new HudTheme(SonarPalette.Standard, false);
        QueueRedraw();
    }

    private static SonarContactKind KindOf(SonarClass cls) => cls switch
    {
        SonarClass.Salvage => SonarContactKind.Salvage,
        SonarClass.Biological => SonarContactKind.Biological,
        SonarClass.Structure => SonarContactKind.Structure,
        SonarClass.Unknown => SonarContactKind.Unknown,
        _ => SonarContactKind.Terrain,
    };

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
        var pal = _theme.Palette;
        var hc = _theme.HighContrast;
        var ink = HudTheme.ToColor(pal.ScopeBackground);
        var accent = _theme.Accent;
        var text = _theme.Text;
        var alert = _theme.Alert;
        var rim = hc ? _theme.PanelBorder : HudTheme.ToColor(pal.Rim);
        var font = ThemeDB.FallbackFont;
        var bump = hc ? 1 : 0;

        var w = Size.X;
        var cx = w * 0.5f;
        var cy = 44f + 100f;
        var radius = 96f;
        var center = new Vector2(cx, cy);

        // Compass strip (opaque in high contrast so text never sits on the 3D view).
        DrawRect(new Rect2(0, 0, w, hc ? 48 : 30), hc ? Colors.Black : new Color(0.02f, 0.07f, 0.10f, 0.95f));
        HudText(font, new Vector2(8, 20), Localization.T("OBJ") + " " + _objectiveText + "  " + RadToMark(_objectiveBearing), 13 + bump, text);
        var extMark = Localization.T("EXT") + " " + RadToMark(_extractBearing);
        HudText(font, new Vector2(w - 8 - font.GetStringSize(extMark, HorizontalAlignment.Left, -1, 13 + bump).X, 20), extMark, 13 + bump, accent);
        if (_nearestThreatRange >= 0f)
        {
            var warn = Localization.T("THREAT {0}m", (object)$"{_nearestThreatRange:F0}") + (_quiet ? Localization.T(" - QUIET") : "");
            HudText(font, new Vector2(8, 42), warn, 12 + bump, alert);
        }
        else if (_quiet)
        {
            HudText(font, new Vector2(8, 42), Localization.T("QUIET RUNNING"), 12 + bump, accent);
        }

        // Scope body.
        DrawCircle(center, radius + 6f, rim);
        DrawCircle(center, radius, ink);
        var ringAlpha = hc ? 0.6f : 0.35f;
        DrawArc(center, radius * 0.33f, 0f, MathF.PI * 2f, 48, new Color(accent, ringAlpha), 1f);
        DrawArc(center, radius * 0.66f, 0f, MathF.PI * 2f, 48, new Color(accent, ringAlpha), 1f);
        DrawArc(center, radius, 0f, MathF.PI * 2f, 64, accent, hc ? 3f : 1.5f);
        DrawLine(new Vector2(cx - radius, cy), new Vector2(cx + radius, cy), new Color(accent, hc ? 0.45f : 0.25f), 1f);
        DrawLine(new Vector2(cx, cy - radius), new Vector2(cx, cy + radius), new Color(accent, hc ? 0.45f : 0.25f), 1f);

        // Sweep: soft wedge + leading line, slow, no strobe.
        var dir = new Vector2(MathF.Sin(_sweep), -MathF.Cos(_sweep));
        const float trail = 0.7f;
        var plist = new List<Vector2> { center };
        for (var i = 0; i <= 12; i++)
        {
            var a = _sweep - trail * i / 12f;
            plist.Add(center + new Vector2(MathF.Sin(a), -MathF.Cos(a)) * radius);
        }
        DrawColoredPolygon(plist.ToArray(), new Color(accent, 0.10f));
        DrawLine(center, center + dir * radius, new Color(accent, 0.8f), 1.5f);

        // Ship wedge at center, pointing scope-up.
        var nose = new Vector2(cx, cy - 7f);
        var bl = new Vector2(cx - 5f, cy + 5f);
        var br = new Vector2(cx + 5f, cy + 5f);
        DrawColoredPolygon(new Vector2[] { nose, bl, br }, text);

        var minAlpha = hc ? 0.45f : 0.25f;
        foreach (var b in _history)
        {
            var fade = 1f - b.Age / 14f;
            var p = center + b.Pos;
            if ((p - center).Length() > radius) continue;
            var kind = KindOf(b.Class);
            var col = HudTheme.ToColor(pal.ContactColor(kind));
            DrawBlip(font, p, kind, new Color(col, minAlpha + (1f - minAlpha) * fade));
        }

        // Buddy breadcrumb: small steady dots, no motion or flashing.
        foreach (var fix in _trailWorld)
        {
            var p = center + ToLocal(fix, _trailShip, _trailHeading, _trailRange);
            if ((p - center).Length() > radius) continue;
            DrawCircle(p, hc ? 2.5f : 2f, new Color(accent, hc ? 1f : 0.75f));
        }

        DrawLegend(font, w, cy + radius + 6f);    }

    /// <summary>Glyph key under the scope: shape + palette colour + localized kind name.</summary>
    private void DrawLegend(Font font, float w, float top)
    {
        var size = 10 + (_theme.HighContrast ? 1 : 0);
        var rows = new[]
        {
            new[] { (SonarContactKind.Salvage, Localization.T("Salvage")), (SonarContactKind.Biological, Localization.T("Biological")), (SonarContactKind.Structure, Localization.T("Structure")) },
            new[] { (SonarContactKind.Unknown, Localization.T("Unknown")), (SonarContactKind.Terrain, Localization.T("Terrain")) },
        };
        var colWidth = w / 3f;
        for (var r = 0; r < rows.Length; r++)
        {
            var baseline = top + 16f + r * 17f;
            for (var c = 0; c < rows[r].Length; c++)
            {
                var (kind, label) = rows[r][c];
                var x = c * colWidth + 10f;
                DrawBlip(font, new Vector2(x, baseline - 4f), kind, HudTheme.ToColor(_theme.Palette.ContactColor(kind)));
                HudText(font, new Vector2(x + 10f, baseline), label, size, _theme.Text);
            }
        }
    }

    /// <summary>HUD string; high contrast adds a dark outline behind the glyphs.</summary>
    private void HudText(Font font, Vector2 pos, string value, int size, Color color)
    {
        if (_theme.OutlineSize > 0)
        {
            DrawStringOutline(font, pos, value, HorizontalAlignment.Left, -1, size, _theme.OutlineSize, Colors.Black);
        }
        DrawString(font, pos, value, HorizontalAlignment.Left, -1, size, color);
    }

    private void DrawBlip(Font font, Vector2 p, SonarContactKind kind, Color col)
    {
        // High contrast: larger glyphs on a dark halo so they read against the sweep.
        var hc = _theme.HighContrast;
        var s = hc ? 1.35f : 1f;
        if (hc)
        {
            DrawGlyph(font, p, kind, new Color(0f, 0f, 0f, col.A), s * 1.45f, 3.5f);
        }
        DrawGlyph(font, p, kind, col, s, hc ? 2f : 1.5f);
    }

    private void DrawGlyph(Font font, Vector2 p, SonarContactKind kind, Color col, float s, float stroke)
    {
        switch (kind)
        {
            case SonarContactKind.Salvage: // diamond
                DrawColoredPolygon(new Vector2[] { p + new Vector2(0, -5) * s, p + new Vector2(5, 0) * s, p + new Vector2(0, 5) * s, p + new Vector2(-5, 0) * s }, col);
                break;
            case SonarContactKind.Biological: // triangle
                DrawColoredPolygon(new Vector2[] { p + new Vector2(0, -6) * s, p + new Vector2(5, 4) * s, p + new Vector2(-5, 4) * s }, col);
                break;
            case SonarContactKind.Structure: // square
                DrawRect(new Rect2(p - new Vector2(4, 4) * s, new Vector2(8, 8) * s), col);
                break;
            case SonarContactKind.Unknown: // question ring
                DrawArc(p, 5f * s, 0f, MathF.PI * 2f, 16, col, stroke);
                DrawString(font, p + new Vector2(-2.5f, 3.5f) * s, "?", HorizontalAlignment.Left, -1, (int)MathF.Round(9 * s), col);
                break;
            default: // terrain: short dash (line pattern)
                DrawLine(p - new Vector2(3.5f, 0) * s, p + new Vector2(3.5f, 0) * s, col, stroke + 0.5f);
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
