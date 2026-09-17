using AbyssScav.Protocol;

namespace AbyssScav.Net.Tests;

internal static class ReconnectTests
{
    public static int Run()
    {
        var cases = new (string Name, Action Test)[]
        {
            ("issue_and_takeover_within_grace", Takeover),
            ("token_scoped_to_session", SessionScope),
            ("expired_token_rejected", Expiry),
            ("run_end_discards_tokens", EndRun),
            ("reissue_rotates_token", Rotation),
        };

        var fail = 0;
        foreach (var (name, test) in cases)
        {
            try
            {
                test();
                Console.WriteLine($"  ok: {name}");
            }
            catch (Exception ex)
            {
                fail++;
                Console.WriteLine($"  FAIL: {name}: {ex.Message}");
            }
        }

        return fail;
    }

    private static void Takeover()
    {
        var clock = new ManualClock();
        var store = new ReconnectTokenStore(() => clock.Now);
        var token = store.Issue(2, 77, "diver");
        TestAssert.True(store.TryTakeover(token, 77, out var peer, out _), "takeover");
        TestAssert.Equal(2ul, peer, "peer restored");
    }

    private static void SessionScope()
    {
        var clock = new ManualClock();
        var store = new ReconnectTokenStore(() => clock.Now);
        var token = store.Issue(2, 77, "diver");
        TestAssert.False(store.TryTakeover(token, 78, out _, out _), "other session refused");
        TestAssert.False(store.TryTakeover("bogus", 77, out _, out _), "unknown token refused");
    }

    private static void Expiry()
    {
        var clock = new ManualClock();
        var store = new ReconnectTokenStore(() => clock.Now);
        var token = store.Issue(2, 77, "diver");
        clock.Advance(TimeSpan.FromSeconds(NetLimits.ReconnectGraceSeconds + 1));
        TestAssert.False(store.TryTakeover(token, 77, out _, out _), "expired refused");
    }

    private static void EndRun()
    {
        var clock = new ManualClock();
        var store = new ReconnectTokenStore(() => clock.Now);
        var token = store.Issue(2, 77, "diver");
        store.EndRun();
        TestAssert.False(store.TryTakeover(token, 77, out _, out _), "run end discards");
        TestAssert.Equal(0, store.ActiveCount, "empty");
    }

    private static void Rotation()
    {
        var clock = new ManualClock();
        var store = new ReconnectTokenStore(() => clock.Now);
        var first = store.Issue(2, 77, "diver");
        var second = store.Issue(2, 77, "diver");
        TestAssert.False(first == second, "rotated");
        TestAssert.False(store.TryTakeover(first, 77, out _, out _), "old invalid");
        TestAssert.True(store.TryTakeover(second, 77, out _, out _), "new valid");
    }
}
