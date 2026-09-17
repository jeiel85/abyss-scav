using System.Text.RegularExpressions;

namespace AbyssScav.Foundation;

/// <summary>
/// Local-only privacy-conscious log writer (docs/01 §10, docs/09 §7):
/// file logs/game_YYYYMMDD_HHMMSS.log, INFO/WARN/ERROR, random session id,
/// IPv4 last-octet masking, no payload logging, no endpoints.
/// </summary>
public sealed class LocalLogger : IDisposable
{
    private static readonly Regex Ipv4Pattern = new(@"\b(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})\b", RegexOptions.Compiled);
    private static readonly Regex Ipv6Pattern = new(@"(?i)\b(?:[0-9a-f]{0,4}:){2,7}[0-9a-f]{0,4}\b", RegexOptions.Compiled);
    private static readonly Regex WinProfilePattern = new(@"(?i)C:\\Users\\[^\\/:\*\?""<>\|\s]+", RegexOptions.Compiled);
    private static readonly Regex HomeProfilePattern = new(@"(?i)/home/[^/\s]+", RegexOptions.Compiled);

    private readonly StreamWriter _writer;
    private bool _disposed;

    public string LogPath { get; }
    public string SessionId { get; }

    public LocalLogger(string logsDir, string sessionId, string gameVersion)
    {
        SessionId = sessionId;
        Directory.CreateDirectory(logsDir);
        var baseName = "game_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var attempt = Path.Combine(logsDir, baseName + ".log");
        FileStream? stream = null;
        for (var i = 0; i < 100; i++)
        {
            try
            {
                // ReadWrite share so support-bundle readers can copy the live log.
                stream = new FileStream(attempt, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
                break;
            }
            catch (IOException) when (i < 99)
            {
                attempt = Path.Combine(logsDir, $"{baseName}_{i + 2}.log");
            }
        }

        if (stream is null)
        {
            throw new IOException($"Cannot create log file in {logsDir}.");
        }

        LogPath = attempt;
        _writer = new StreamWriter(stream) { AutoFlush = true };
        WriteLine("INFO", $"AbyssScav v{gameVersion} session={sessionId} utc={DateTime.UtcNow:O} os={Environment.OSVersion} arch={System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}");
    }

    public void Info(string message, string? code = null) => WriteLine("INFO", Format(code, message));

    public void Warn(string message, string? code = null) => WriteLine("WARN", Format(code, message));

    public void Error(string message, string? code = null) => WriteLine("ERROR", Format(code, message));

    /// <summary>
    /// Masks IPs (v4 last octet, full v6), redacts user profile path segments
    /// (which embed OS usernames), and drops packet payload content instead of
    /// logging it verbatim.
    /// </summary>
    public static string Sanitize(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return message;
        }

        var masked = Ipv4Pattern.Replace(message, m => $"{m.Groups[1]}.{m.Groups[2]}.{m.Groups[3]}.xxx");
        masked = Ipv6Pattern.Replace(masked, "[ipv6-masked]");
        masked = WinProfilePattern.Replace(masked, @"C:\Users\[redacted]");
        masked = HomeProfilePattern.Replace(masked, "/home/[redacted]");
        const string marker = "payload:";
        var index = masked.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            masked = masked[..(index + marker.Length)] + " [redacted]";
        }

        return masked;
    }

    private static string Format(string? code, string message) =>
        string.IsNullOrEmpty(code) ? message : $"{code} {message}";

    private void WriteLine(string level, string message)
    {
        if (_disposed)
        {
            return;
        }

        _writer.WriteLine($"{DateTime.UtcNow:O} [{level}] {Sanitize(message)}");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _writer.Dispose();
    }
}
