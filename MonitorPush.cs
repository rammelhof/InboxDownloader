using System.Net;

namespace InboxDownloader;

/// <summary>
/// Push client for push-based monitoring services. The primary target is Uptime
/// Kuma ("Push", see https://uptime.kuma.pet/docs/features/Push/) - the URL is used
/// as-is except that the following placeholders are substituted when present (the
/// rest of the URL, query string included, is never touched):
///   {status}    "up" or "down"
///   {msg}       the (URL encoded) status message
///   {msgcount}  message count of the inbox when the run started
///   {ping}      seconds the whole run took
/// Example template: http://mon.lan:3001/api/push/TOKEN?status={status}&amp;msg={msg}&amp;ping=
/// </summary>
public static class MonitorPush
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>
    /// Builds the request URL: {status}, {msg}, {msgcount} and {ping} are substituted
    /// where they occur in the stored URL. A URL without placeholders is returned
    /// unchanged (no query string is appended or stripped).
    /// </summary>
    public static string BuildRequestUrl(string storedUrl, string status, string message, int msgCount, double durationSeconds)
    {
        var url = storedUrl.Trim();
        if (!url.Contains('{', StringComparison.Ordinal))
            return url;

        return url
            .Replace("{status}", status, StringComparison.Ordinal)
            .Replace("{msg}", Uri.EscapeDataString(message), StringComparison.Ordinal)
            .Replace("{msgcount}", msgCount.ToString("F0"), StringComparison.Ordinal)
            .Replace("{ping}", durationSeconds.ToString("F0"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Sends the push. Returns null on success or a short error description on failure
    /// (a failed push must not crash the service - the monitor keeps its last state).
    /// </summary>
    public static async Task<string?> SendAsync(string storedUrl, string status, string message, int msgCount, double durationSeconds, CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync(BuildRequestUrl(storedUrl, status, message, msgCount, durationSeconds), ct);
            if (!response.IsSuccessStatusCode)
                return $"push returned HTTP {(int)response.StatusCode}";
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"push failed: {ex.Message}";
        }
    }
}
