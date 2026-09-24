using AbyssScav.App;
using Godot;

namespace AbyssScav.Gameplay;

/// <summary>
/// Input actions registered from code (keyboard + controller, no editor config
/// dependency). Called once per run-scene boot; safe to call repeatedly.
/// Keyboard keys come from the player's saved bindings (Settings → key bindings,
/// see <see cref="InputBindings"/>) and are re-applied on every call, so a rebind
/// made in the menu takes effect on the next dive. Pause stays on Escape (also the
/// rebind-cancel key); gamepad buttons are fixed and registered once.
/// </summary>
public static class AbyssInput
{
    private static bool _fixedRegistered;

    public static void EnsureRegistered()
    {
        if (!_fixedRegistered)
        {
            _fixedRegistered = true;
            Add("abyss_pause", Key.Escape);
            BindPad();
        }

        InputBindings.ApplyToInputMap(InputBindings.Current);
    }

    private static void Add(string action, params Key[] keys)
    {
        if (!InputMap.HasAction(action))
        {
            InputMap.AddAction(action);
        }
        foreach (var k in keys)
        {
            var ev = new InputEventKey { PhysicalKeycode = k };
            if (!InputMap.ActionHasEvent(action, ev))
            {
                InputMap.ActionAddEvent(action, ev);
            }
        }
    }

    private static void Add(string action, Key key, bool unused)
    {
        Add(action, key);
    }

    private static void BindJoy(string action, JoyButton button)
    {
        if (!InputMap.HasAction(action))
        {
            InputMap.AddAction(action);
        }
        var ev = new InputEventJoypadButton { ButtonIndex = button };
        InputMap.ActionAddEvent(action, ev);
    }

    private static void BindPad()
    {
        BindJoy("abyss_ping", JoyButton.RightShoulder);
        BindJoy("abyss_interact", JoyButton.A);
        BindJoy("abyss_survey", JoyButton.X);
        BindJoy("abyss_service", JoyButton.Y);
        BindJoy("abyss_repair", JoyButton.B);
        BindJoy("abyss_boost", JoyButton.LeftShoulder);
        BindJoy("abyss_pause", JoyButton.Start);
        BindJoy("abyss_winch", JoyButton.Back);
        BindJoy("abyss_dock", JoyButton.DpadLeft);
        BindJoy("abyss_drill", JoyButton.DpadRight);
        BindJoy("abyss_extract", JoyButton.RightStick);
        BindJoy("abyss_silent", JoyButton.LeftStick);
        BindJoy("abyss_up", JoyButton.DpadUp);
        BindJoy("abyss_down", JoyButton.DpadDown);
    }

    /// <summary>Analog stick fallback in [-1,1] per axis, deadzoned.</summary>
    public static Vector2 LeftStick()
    {
        var x = Input.GetJoyAxis(0, JoyAxis.LeftX);
        var y = Input.GetJoyAxis(0, JoyAxis.LeftY);
        if (Math.Abs(x) < 0.18) x = 0f;
        if (Math.Abs(y) < 0.18) y = 0f;
        return new Vector2(x, y);
    }

    public static Vector2 RightStick()
    {
        var x = Input.GetJoyAxis(0, JoyAxis.RightX);
        var y = Input.GetJoyAxis(0, JoyAxis.RightY);
        if (Math.Abs(x) < 0.18) x = 0f;
        if (Math.Abs(y) < 0.18) y = 0f;
        return new Vector2(x, y);
    }

    public static float PadHeave()
    {
        var up = Input.IsActionPressed("abyss_up") ? 1f : 0f;
        var down = Input.IsActionPressed("abyss_down") ? 1f : 0f;
        return up - down > 0f ? 0f : 0f;
    }
}
