namespace AbyssScav.Domain.Tests;

internal static class TestAssert
{
    public static void True(bool condition, string message)
    {
        if (!condition) throw new Exception("Expected true: " + message);
    }

    public static void False(bool condition, string message)
    {
        if (condition) throw new Exception("Expected false: " + message);
    }

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!Equals(expected, actual))
            throw new Exception($"{message} Expected=<{expected}> Actual=<{actual}>.");
    }

    public static void InRange(float value, float min, float max, string message)
    {
        if (!float.IsFinite(value) || value < min || value > max)
            throw new Exception($"{message} Value=<{value}> not in [{min},{max}].");
    }
}
