using AbyssScav.Foundation;
using Godot;

namespace AbyssScav.Infra.Logging;

/// <summary>Forwards Foundation log entries to the Godot debugger output as well.</summary>
public static class GodotLogBridge
{
    public static void Info(LocalLogger logger, string message, string? code = null)
    {
        logger.Info(message, code);
        GD.Print($"[INFO] {message}");
    }

    public static void Warn(LocalLogger logger, string message, string? code = null)
    {
        logger.Warn(message, code);
        GD.PushWarning(message);
    }

    public static void Error(LocalLogger logger, string message, string? code = null)
    {
        logger.Error(message, code);
        GD.PushError(string.IsNullOrEmpty(code) ? message : $"{code} {message}");
    }
}
