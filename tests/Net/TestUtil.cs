namespace AbyssScav.Net.Tests;

internal static class TestAssert
{
    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception("Expected true: " + message);
        }
    }

    public static void False(bool condition, string message)
    {
        if (condition)
        {
            throw new Exception("Expected false: " + message);
        }
    }

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!Equals(expected, actual))
        {
            throw new Exception($"{message} Expected=<{expected}> Actual=<{actual}>.");
        }
    }

    public static void NotNull(object? value, string message)
    {
        if (value is null)
        {
            throw new Exception("Expected non-null: " + message);
        }
    }
}

internal sealed class ManualClock
{
    private DateTimeOffset _now = new(2026, 9, 17, 0, 0, 0, TimeSpan.Zero);

    public DateTimeOffset Now => _now;

    public void Advance(TimeSpan step) => _now += step;
}
