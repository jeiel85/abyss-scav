using AbyssScav.Foundation;

namespace AbyssScav.Foundation.Tests;

internal static class LoggingTests
{
    public static int Run()
    {
        var cases = new (string Name, Action Test)[]
        {
            ("log_file_created_with_levels", LogFileLevels),
            ("ipv4_last_octet_masked", IpMasked),
            ("ipv6_masked", IpV6Masked),
            ("packet_payload_not_logged_verbatim", PayloadRedacted),
            ("profile_paths_redacted", ProfileRedacted),
            ("header_contains_no_username", NoUsernameLeak),
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

    private static void LogFileLevels()
    {
        var dir = TestTemp.NewRoot();
        string path;
        using (var logger = new LocalLogger(dir, Guid.NewGuid().ToString("N"), GameVersion.Current))
        {
            path = logger.LogPath;
            logger.Info("hello");
            logger.Warn("careful", ErrorCodes.BootConfigCorrupt);
            logger.Error("broken", ErrorCodes.SaveWrite);
        }

        var content = File.ReadAllText(path);
        TestAssert.True(content.Contains("[INFO]"), "info level");
        TestAssert.True(content.Contains("[WARN]"), "warn level");
        TestAssert.True(content.Contains("[ERROR]"), "error level");
        TestAssert.True(content.Contains("BOOT-001"), "code kept");
    }

    private static void IpMasked()
    {
        var masked = LocalLogger.Sanitize("peer 192.168.1.55 disconnected");
        TestAssert.True(masked.Contains("192.168.1.xxx"), "last octet masked: " + masked);
        TestAssert.True(!masked.Contains("192.168.1.55"), "raw ip absent");
    }

    private static void PayloadRedacted()
    {
        var masked = LocalLogger.Sanitize("net message payload: SECRET-BYTES-12345");
        TestAssert.True(masked.Contains("[redacted]"), "payload redacted");
        TestAssert.True(!masked.Contains("SECRET-BYTES-12345"), "raw payload absent");
    }

    private static void IpV6Masked()
    {
        var masked = LocalLogger.Sanitize("peer 2001:db8::1 disconnected");
        TestAssert.True(masked.Contains("[ipv6-masked]"), "ipv6 masked: " + masked);
        TestAssert.True(!masked.Contains("2001:db8::1"), "raw ipv6 absent");
    }

    private static void ProfileRedacted()
    {
        var user = Environment.UserName;
        var masked = LocalLogger.Sanitize($@"cannot write C:\Users\{user}\AppData\logs and /home/{user}/data");
        TestAssert.True(string.IsNullOrEmpty(user) || !masked.Contains(user), "username absent: " + masked);
    }

    private static void NoUsernameLeak()
    {
        var dir = TestTemp.NewRoot();
        var user = Environment.UserName;
        string path;
        using (var logger = new LocalLogger(dir, Guid.NewGuid().ToString("N"), GameVersion.Current))
        {
            path = logger.LogPath;
            logger.Info("marker");
        }

        var content = File.ReadAllText(path);
        // Header must not embed the OS username.
        TestAssert.True(!content.Contains(user) || string.IsNullOrEmpty(user), "username must not appear in log header");
    }
}
