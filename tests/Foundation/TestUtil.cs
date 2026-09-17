namespace AbyssScav.Foundation.Tests;

internal static class TestAssert
{
    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception("Expected true: " + message);
        }
    }

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!Equals(expected, actual))
        {
            throw new Exception($"{message} Expected=<{expected}> Actual=<{actual}>.");
        }
    }
}

internal static class TestTemp
{
    public static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "abyss-a01-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
