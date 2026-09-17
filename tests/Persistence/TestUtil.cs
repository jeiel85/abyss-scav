namespace AbyssScav.Persistence.Tests;

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

    public static async Task ThrowsAsync<TException>(Func<Task> action, string message)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException)
        {
            return;
        }
        catch (Exception ex)
        {
            throw new Exception($"{message} Wrong exception: {ex.GetType().Name}: {ex.Message}");
        }

        throw new Exception($"{message} Expected {typeof(TException).Name} but no exception was thrown.");
    }
}

internal static class TestTemp
{
    public static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "abyss-a03-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    public static string SavesDir(string root) => Path.Combine(root, "saves");
}

internal static class TestData
{
    public static ProfileSave Sample(string seed = "sample") => ProfileSave.Default() with
    {
        Currencies = new SaveCurrencies(100, 20, 3),
        Unlocks = new SaveUnlocks(
            new List<string> { "module.sonar.whisper_pulse" },
            new List<string> { "frame.mule" }),
        Tutorial = new SaveTutorial(false, new List<string> { "tutorial.move" }),
        Codex = new SaveCodex(new List<string> { "codex.leech" }),
        Stats = new SaveStats(1, 0, 100, 3),
        AppliedSettlementIds = new List<string> { seed + ".applied" },
    };
}
