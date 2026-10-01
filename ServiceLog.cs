using System.Globalization;

namespace InboxDownloader;

/// <summary>
/// Appends timestamped lines to a daily log file (service_yyyyMMdd.log) in the
/// config file's directory and echoes them to the console (visible when running
/// "InboxDownloader.exe service" in a console window). Log files whose date in
/// the file name is older than 14 days are deleted automatically.
/// </summary>
public static class ServiceLog
{
    private static readonly object Gate = new();
    private const int RetentionDays = 14;
    private static string? _lastCleanupDay;

    public static void Write(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        lock (Gate)
        {
            Console.WriteLine(line);
            try
            {
                var dir = ConfigStore.ConfigDirectory;
                Directory.CreateDirectory(dir);
                var day = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                if (_lastCleanupDay != day)
                {
                    DeleteOldLogs(dir);
                    _lastCleanupDay = day;
                }
                File.AppendAllText(Path.Combine(dir, $"service_{day}.log"), line + Environment.NewLine);
            }
            catch
            {
                // logging must never kill the service
            }
        }
    }

    /// <summary>
    /// Deletes service log files (service_yyyyMMdd.log) whose date is older than
    /// the retention period. Files whose names do not carry a valid date are kept.
    /// </summary>
    private static void DeleteOldLogs(string dir)
    {
        var cutoff = DateTime.Today.AddDays(-RetentionDays);
        const string prefix = "service_";
        foreach (var file in Directory.GetFiles(dir, prefix + "*.log"))
        {
            var datePart = Path.GetFileNameWithoutExtension(file);
            if (datePart.Length != prefix.Length + 8)
                continue;
            if (DateTime.TryParseExact(datePart[prefix.Length..], "yyyyMMdd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                && day < cutoff)
            {
                File.Delete(file);
            }
        }
    }
}
