using AbyssScav.Net.Tests;

var suites = new (string Suite, Func<int> Run)[]
{
    ("Frame", FrameTests.Run),
    ("Handshake", HandshakeTests.Run),
    ("RateLimit", RateLimitTests.Run),
    ("Reconnect", ReconnectTests.Run),
    ("Beacon", BeaconTests.Run),
    ("Manifest", ManifestTests.Run),
    ("Lobby", LobbyTests.Run),
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
