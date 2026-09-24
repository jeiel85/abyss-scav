using AbyssScav.Foundation;
using Godot;

namespace AbyssScav.Gameplay;

/// <summary>
/// Visual stand-in for a teammate's submarine (docs/02 §9.5). Deliberately
/// <b>not</b> a physics body: it has no collider, so a remote hull can never
/// shove the local sub because of network jitter, interpolation delay or a
/// correction snap. Teammates pass through each other; the name label and
/// amber running lights make that readable. Pose is driven every frame by
/// <see cref="CoopRunLink"/> from the interpolation buffer.
/// </summary>
public partial class RemoteSubmarineProxy : Node3D
{
    private Label3D? _label;
    private string _name = string.Empty;
    private bool _linkLost;

    public override void _Ready()
    {
        var bodyMat = new StandardMaterial3D
        {
            AlbedoColor = new Color("#3b4f58"),
            Metallic = 0.5f,
            Roughness = 0.55f,
        };
        var lampMat = new StandardMaterial3D
        {
            AlbedoColor = new Color("#dfa44d"),
            EmissionEnabled = true,
            Emission = new Color("#dfa44d"),
            EmissionEnergyMultiplier = 0.9f,
        };
        var hull = new MeshInstance3D
        {
            Name = "HullVisual",
            Mesh = new CapsuleMesh { Radius = 2.55f, Height = 9.4f },
            MaterialOverride = bodyMat,
            RotationDegrees = new Vector3(90f, 0f, 0f),
        };
        AddChild(hull);
        AddChild(new MeshInstance3D
        {
            Name = "Tower",
            Mesh = new BoxMesh { Size = new Vector3(2.2f, 1.6f, 3.4f) },
            MaterialOverride = bodyMat,
            Position = new Vector3(0f, 2.6f, 0.6f),
        });
        AddChild(new MeshInstance3D
        {
            Name = "Stripe",
            Mesh = new BoxMesh { Size = new Vector3(5.3f, 0.22f, 0.22f) },
            MaterialOverride = lampMat,
            Position = new Vector3(0f, 0.4f, -4.2f),
        });
        AddChild(new OmniLight3D
        {
            Name = "RunningLight",
            LightColor = new Color("#dfa44d"),
            LightEnergy = 1.2f,
            OmniRange = 14f,
            ShadowEnabled = false,
            Position = new Vector3(0f, 3.6f, 0.6f),
        });
        _label = new Label3D
        {
            Name = "NameTag",
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true,
            FixedSize = true,
            PixelSize = 0.0022f,
            FontSize = 26,
            OutlineSize = 6,
            Modulate = new Color("#dae4df"),
            Position = new Vector3(0f, 6.2f, 0f),
        };
        AddChild(_label);
        RefreshLabel();
    }

    /// <summary>Sets the teammate's display name shown above the hull.</summary>
    public void SetDisplayName(string name)
    {
        if (string.Equals(_name, name, StringComparison.Ordinal))
        {
            return;
        }

        _name = name;
        RefreshLabel();
    }

    /// <summary>Marks the teammate's link as dropped (reconnect grace running).</summary>
    public void SetLinkLost(bool lost)
    {
        if (_linkLost == lost)
        {
            return;
        }

        _linkLost = lost;
        RefreshLabel();
    }

    private void RefreshLabel()
    {
        if (_label is null)
        {
            return;
        }

        _label.Text = _linkLost ? _name + " " + Localization.T("(link lost)") : _name;
        _label.Modulate = _linkLost ? new Color("#dfa44d") : new Color("#dae4df");
    }
}
