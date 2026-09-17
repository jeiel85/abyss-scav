using AbyssScav.Protocol;

namespace AbyssScav.Net.Tests;

internal static class RateLimitTests
{
    public static int Run()
    {
        var cases = new (string Name, Action Test)[]
        {
            ("burst_is_bounded", BurstBounded),
            ("bucket_refills_over_time", Refill),
            ("reliable_and_unreliable_tracked_separately", SeparateChannels),
            ("unknown_types_rejected", UnknownRejected),
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

    private static void BurstBounded()
    {
        var clock = new ManualClock();
        var limiter = new PeerRateLimiter();
        var allowed = 0;
        for (var i = 0; i < 200; i++)
        {
            if (limiter.TryConsume(2, MessageType.PlayerIntent, clock.Now))
            {
                allowed++;
            }
        }

        TestAssert.Equal((int)NetLimits.ReliableBurst, allowed, "reliable burst cap");
    }

    private static void Refill()
    {
        var clock = new ManualClock();
        var limiter = new PeerRateLimiter();
        for (var i = 0; i < (int)NetLimits.ReliableBurst; i++)
        {
            TestAssert.True(limiter.TryConsume(2, MessageType.PlayerIntent, clock.Now), "drain");
        }

        TestAssert.False(limiter.TryConsume(2, MessageType.PlayerIntent, clock.Now), "empty blocks");
        clock.Advance(TimeSpan.FromSeconds(1));
        TestAssert.True(limiter.TryConsume(2, MessageType.PlayerIntent, clock.Now), "refill admits");
    }

    private static void SeparateChannels()
    {
        var clock = new ManualClock();
        var limiter = new PeerRateLimiter();
        for (var i = 0; i < (int)NetLimits.ReliableBurst; i++)
        {
            limiter.TryConsume(2, MessageType.PlayerIntent, clock.Now);
        }

        TestAssert.False(limiter.TryConsume(2, MessageType.GameEvent, clock.Now), "reliable exhausted");
        TestAssert.True(limiter.TryConsume(2, MessageType.Snapshot, clock.Now), "snapshot bucket independent");
    }

    private static void UnknownRejected()
    {
        var clock = new ManualClock();
        var limiter = new PeerRateLimiter();
        TestAssert.False(limiter.TryConsume(2, MessageType.Unknown, clock.Now), "unknown type");
        TestAssert.False(limiter.TryConsume(NetLimits.InvalidId, MessageType.PlayerIntent, clock.Now), "zero peer");
    }
}
