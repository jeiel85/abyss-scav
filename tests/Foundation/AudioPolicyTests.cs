using AbyssScav.Foundation;

namespace AbyssScav.Foundation.Tests;

internal static class AudioPolicyTests
{
    public static int Run()
    {
        var cases = new (string Name, Action Test)[]
        {
            ("full_volume_is_zero_db", FullVolume),
            ("zero_volume_mutes_at_floor", ZeroMutes),
            ("mapping_is_monotonic", Monotonic),
            ("out_of_range_clamped", Clamped),
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

    private static void FullVolume()
    {
        TestAssert.True(Math.Abs(AudioPolicy.ToMasterVolumeDb(100)) < 0.001f, "100% is 0 dB");
        TestAssert.True(!AudioPolicy.IsMuted(100), "not muted");
    }

    private static void ZeroMutes()
    {
        TestAssert.True(AudioPolicy.IsMuted(0), "muted");
        TestAssert.Equal(AudioPolicy.SilenceFloorDb, AudioPolicy.ToMasterVolumeDb(0), "floor");
    }

    private static void Monotonic()
    {
        var previous = AudioPolicy.ToMasterVolumeDb(1);
        for (var v = 10; v <= 100; v += 10)
        {
            var current = AudioPolicy.ToMasterVolumeDb(v);
            TestAssert.True(current > previous, $"monotonic at {v}");
            previous = current;
        }
    }

    private static void Clamped()
    {
        TestAssert.Equal(AudioPolicy.ToMasterVolumeDb(100), AudioPolicy.ToMasterVolumeDb(250), "high clamp");
        TestAssert.Equal(AudioPolicy.SilenceFloorDb, AudioPolicy.ToMasterVolumeDb(-5), "low clamp");
    }
}
