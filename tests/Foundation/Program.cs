using AbyssScav.Foundation.Tests;

var suites = new (string Suite, Func<int> Run)[]
{
    ("Config", ConfigTests.Run),
    ("SessionLock", SessionLockTests.Run),
    ("Logging", LoggingTests.Run),
    ("AudioPolicy", AudioPolicyTests.Run),
    ("RegistryFlow", RegistryFlowTests.Run),
};

var totalFail = 0;
foreach (var (suite, run) in suites)
{
    try
    {
        var fail = run();
        Console.WriteLine(fail == 0 ? $"[PASS] {suite}" : $"[FAIL] {suite}: {fail} failing case(s)");
        totalFail += fail;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[ERROR] {suite} runner crashed: {ex}");
        totalFail++;
    }
}

Console.WriteLine(totalFail == 0 ? "ALL TESTS PASSED" : $"{totalFail} TEST(S) FAILED");
return totalFail == 0 ? 0 : 1;
