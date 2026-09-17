namespace AbyssScav.Foundation;

public sealed record BootOptions(bool SafeMode)
{
    /// <summary>
    /// Parses both engine args and user args (after `--`). Only exact `--safe-mode` enables it.
    /// </summary>
    public static BootOptions Parse(IEnumerable<string> args)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, "--safe-mode", StringComparison.OrdinalIgnoreCase))
            {
                return new BootOptions(true);
            }
        }

        return new BootOptions(false);
    }
}
