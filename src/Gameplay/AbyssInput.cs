using Godot;

namespace AbyssScav.Gameplay;

/// <summary>
/// Input actions registered from code (keyboard + controller, no editor config
/// dependency). Called once per run-scene boot; safe to call repeatedly.
/// </summary>
public static class AbyssInput
{
    private static bool _registered;

    public static void EnsureRegistered()
    {
        if (_registered)
        {
            return;
        }
        _registered = true;
        Add("abyss_fwd", Key.W);
        Add("abyss_back", Key.S);
        Add("abyss_left", Key.A);
        Add("abyss_right", Key.D);
        Add("abyss_up", Key.Space);
        Add("abyss_down", Key.Ctrl, Key.C);
        Add("abyss_yaw_left", Key.Left);
        Add("abyss_yaw_right", Key.Right);
        Add("abyss_pitch_up", Key.Up);
        Add("abyss_pitch_down", Key.Down);
        Add("abyss_boost", Key.Shift);
        Add("abyss_ping", Key.F);
        Add("abyss_interact", Key.E);
        Add("abyss_survey", Key.V);
        Add("abyss_service", Key.G);
        Add("abyss_repair", Key.R);
        Add("abyss_dock", Key.J);
        Add("abyss_drill", Key.H);
        Add("abyss_winch", Key.X);
        Add("abyss_extract", Key.T);
        Add("abyss_silent", Key.Z);
        Add("abyss_pause", Key.Escape);
        BindPad();
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
