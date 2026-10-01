using System.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace InboxDownloader;

/// <summary>
/// The service worker. Polls the IMAP inbox every ServiceSettings.PollIntervalMinutes
/// and - if a push URL is configured - reports the state of the last run to the
/// configured push-based monitoring service (e.g. Uptime Kuma) every
/// ServiceSettings.PushIntervalMinutes (status up = last run succeeded,
/// status down = last run failed).
///
/// The config file is reloaded before every step so that changes made with the TUI
/// (new password, intervals, push URL) are picked up without a service restart.
/// </summary>
public sealed class InboxWorker : BackgroundService
{
    private RunOutcome? _lastRun;

    /// <summary>Result of the last download run (what the Kuma push reports).</summary>
    private sealed record RunOutcome(bool Success, string Message, int MsgCount, double DurationSeconds);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ServiceLog.Write("InboxDownloader service started (Ctrl+C / Stop-Service to stop).");
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

        var nextPoll = DateTime.MinValue; // first poll immediately
        var nextPush = DateTime.MaxValue; // first push happens right after the first poll

        while (!stoppingToken.IsCancellationRequested)
        {
            var config = ConfigStore.Load();
            var svc = config.Service;
            var pollMinutes = Math.Max(1, svc.PollIntervalMinutes);
            var pushMinutes = Math.Max(1, svc.PushIntervalMinutes);
            var pushEnabled = !string.IsNullOrWhiteSpace(svc.PushUrl);
            var now = DateTime.UtcNow;

            if (now >= nextPoll)
            {
                _lastRun = await RunOnceAsync(config, stoppingToken);
                ServiceLog.Write(_lastRun.Success
                    ? $"poll OK: {_lastRun.Message}"
                    : $"poll FAILED: {_lastRun.Message}");

                nextPoll = now.AddMinutes(pollMinutes);
                if (pushEnabled)
                {
                    await PushAsync(svc, stoppingToken);
                    nextPush = now.AddMinutes(pushMinutes);
                }
            }
            else if (pushEnabled && now >= nextPush)
            {
                await PushAsync(svc, stoppingToken); // repeat the last state
                nextPush = now.AddMinutes(pushMinutes);
            }

            var nextWakeup = pushEnabled && nextPush < nextPoll ? nextPush : nextPoll;
            var delay = nextWakeup - DateTime.UtcNow;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, stoppingToken);
        }

        ServiceLog.Write("InboxDownloader service stopped.");
    }

    /// <summary>One download run. Never throws (except on shutdown) - failures become a "down" outcome.
    /// "Down" also when mails that meet no mapping criteria are left unread in the inbox.</summary>
    private static async Task<RunOutcome> RunOnceAsync(AppConfig config, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var stats = await MailDownloader.DownloadAsync(config, ct);
            sw.Stop();
            if (stats.Error is not null)
                return new RunOutcome(false, stats.Error, stats.InboxCountAtStart, sw.Elapsed.TotalSeconds);

            ConfigStore.Save(config); // persist LastSyncUtc for the next run

            if (stats.LeftoverMails > 0)
                return new RunOutcome(false,
                    $"{stats.LeftoverMails} mail(s) in {config.Account.FolderName} match no mapping criteria",
                    stats.InboxCountAtStart, sw.Elapsed.TotalSeconds);

            var message = $"{stats.MailsProcessed} message(s) processed, {stats.FilesSaved} new file(s)" +
                          (stats.ArchivedDeleted > 0 ? $", {stats.ArchivedDeleted} archived deleted" : "");
            return new RunOutcome(true, message, stats.InboxCountAtStart, sw.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // shutting down
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new RunOutcome(false, ex.Message, 0, sw.Elapsed.TotalSeconds);
        }
    }

    private async Task PushAsync(ServiceSettings svc, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(svc.PushUrl))
            return;

        var (status, message, msgCount, duration) = _lastRun is { } r
            ? (r.Success ? "up" : "down", Truncate(r.Message, 200), r.MsgCount, r.DurationSeconds)
            : ("up", "service started, no poll run yet", 0, 0.0);

        var error = await MonitorPush.SendAsync(svc.PushUrl, status, message, msgCount, duration, ct);
        ServiceLog.Write(error is null
            ? $"push sent: status={status}"
            : error);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";
}
