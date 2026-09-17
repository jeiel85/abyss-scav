using AbyssScav.Persistence;
using AbyssScav.Persistence.Tests;

// Child-process mode for real cross-process regression tests:
//   <exe> --child-apply <savesDir> <settlementId> <creditsDelta>
// Applies one settlement in a separate OS process and prints the outcome.
if (args.Length >= 3 && string.Equals(args[0], "--child-apply", StringComparison.Ordinal))
{
    var savesDir = args[1];
    var settlementId = args[2];
    var delta = args.Length >= 4 ? long.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 0L;
    var store = new FileSaveStore(savesDir);
    var applied = await store.ApplySettlementAsync(SettlementPayload.ForCredits(settlementId, delta), CancellationToken.None);
    Console.WriteLine($"CHILD_RESULT {applied.Outcome} {applied.Save.Currencies.Credits}");
    return 0;
}

var suites = new (string Suite, Func<int> Run)[]
{
    ("SaveAtomic", SaveAtomicTests.Run),
    ("Migration", MigrationTests.Run),
    ("Settlement", SettlementTests.Run),
    ("EndToEnd", EndToEndTests.Run),
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
