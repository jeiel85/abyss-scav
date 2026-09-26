using Godot;

namespace AbyssScav.Presentation;

/// <summary>
/// Teammate markers on the sonar scope (docs/02 §9.5). Added as a full-rect
/// child of <see cref="SonarDisplay"/> so it draws on top of the scope without
/// touching the scope's own code or palette. Teammates are drawn as a hollow
/// ring with a centre dot plus the name's first letter — distinguishable by
/// shape, not colour alone. Uses the scope's ship-relative mapping (scope
/// centre at x = width/2, y = 144, radius 96 px; forward = scope-up); keep
/// these constants in sync with <see cref="SonarDisplay"/>'s _Draw layout.
/// </summary>
public partial class TeammateSonarOverlay : Control
{
    private const float ScopeCenterY = 144f;
    private const float ScopeRadius = 96f;

    private readonly List<(Vector3 Position, string Name, bool LinkLost)> _mates = new();
    private Vector3 _ship;
    private float _heading;
    private float _range = 450f;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
    }

    /// <summary>Replaces the teammate set for the next redraw.</summary>
    public void SetTeammates(IReadOnlyList<(Vector3 Position, string Name, bool LinkLost)> mates, Vector3 shipPos, float headingRad, float range)
    {
        _mates.Clear();
        if (mates is not null)
        {
            for (var i = 0; i < mates.Count && i < 8; i++)
            {
                _mates.Add(mates[i]);
            }
        }

        _ship = shipPos;
        _heading = headingRad;
        _range = Math.Max(range, 1f);
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_mates.Count == 0)
        {
            return;
        }

        var center = new Vector2(Size.X * 0.5f, ScopeCenterY);
        var parchment = new Color("#dae4df");
        var amber = new Color("#dfa44d");
        var font = ThemeDB.FallbackFont;
        foreach (var (position, name, linkLost) in _mates)
        {
            var local = ToLocal(position);
            var clamped = local.Length() > ScopeRadius - 4f;
            if (clamped)
            {
                // Out of scope range: pin to the rim so the bearing stays readable.
                local = local.Normalized() * (ScopeRadius - 4f);
            }

            var p = center + local;
            var color = linkLost ? amber : parchment;
            DrawArc(p, 5.5f, 0f, MathF.PI * 2f, 16, color, 1.5f);
            if (!clamped)
            {
                DrawCircle(p, 1.8f, color);
            }

            var initial = string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1).ToUpperInvariant();
            DrawString(font, p + new Vector2(7f, 4f), initial, HorizontalAlignment.Left, -1, 11, color);
        }
    }

    private Vector2 ToLocal(Vector3 target)
    {
        var dx = target.X - _ship.X;
        var dz = target.Z - _ship.Z;
        var cos = MathF.Cos(-_heading);
        var sin = MathF.Sin(-_heading);
        var lx = dx * cos - dz * sin;
        var lz = dx * sin + dz * cos;
        return new Vector2(lx / _range * ScopeRadius, lz / _range * ScopeRadius);
    }
}
