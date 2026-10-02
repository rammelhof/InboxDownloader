using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace InboxDownloader;

public class DownloadStats
{
    public int MailsProcessed;
    public int Unmapped;
    /// <summary>Mails that met no criteria and were not downloaded (no unmapped subfolder configured).</summary>
    public int UnmappedSkipped;
    public int FilesSaved;
    public int FilesSkippedExisting;
    /// <summary>Mails still in the source folder that match no mapping criteria (monitoring should report down).</summary>
    public int LeftoverMails;
    /// <summary>Mails deleted from the archive folder because they exceeded the max age.</summary>
    public int ArchivedDeleted;
    /// <summary>Message count of the source folder when the run started (push placeholder {msgcount}).</summary>
    public int InboxCountAtStart;
    public List<string> SaveErrors { get; } = new();
    public string? Error;
}

/// <summary>
/// Connects to the configured IMAP account and stores all messages of the source
/// folder as readable text files plus their attachments, sorted into subfolders
/// according to the mapping file. Every run scans the whole folder: processed mails
/// are moved to the (mandatory) archive folder, so the source folder only ever
/// contains unprocessed mails.
/// </summary>
public static class MailDownloader
{
    public static async Task<DownloadStats> DownloadAsync(AppConfig config, CancellationToken ct)
    {
        var account = config.Account;
        var target = config.Target;
        var stats = new DownloadStats();

        if (string.IsNullOrWhiteSpace(account.Server) || string.IsNullOrWhiteSpace(account.UserName))
        {
            stats.Error = "Account is not configured (server / user name missing).";
            return stats;
        }

        // An archive folder is mandatory: processed mails must be moved out of the
        // source folder. In a shared mailbox the read state cannot be relied upon
        // (other users may have read the mail), so the archive is the only reliable
        // way to know which mails were already processed.
        if (string.IsNullOrWhiteSpace(account.ArchiveFolder))
        {
            stats.Error = "Archive folder is not configured - it is mandatory. " +
                          "Set it in the TUI (option 1) or in config.json (account.archiveFolder).";
            return stats;
        }

        // Where the mail files are stored (and the mapping file is read from):
        // a local folder or a Nextcloud server.
        IFileSink sink;
        if (target.Mode == TargetMode.Nextcloud)
        {
            if (string.IsNullOrWhiteSpace(target.WebDavBaseUrl) || string.IsNullOrWhiteSpace(target.WebDavUser))
            {
                stats.Error = "Target is not configured (Nextcloud URL / user name missing).";
                return stats;
            }
            sink = new WebDavDestination(target);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(target.Path))
            {
                stats.Error = "Target is not configured (folder path missing).";
                return stats;
            }
            sink = new LocalFileSink(target.Path);
        }

        var mappingContent = await sink.ReadMappingFileAsync(target.MappingFileName, ct);
        var mappingSource = target.Mode == TargetMode.Nextcloud
            ? sink.Description
            : Path.Combine(target.Path, target.MappingFileName);
        var mappings = FromAddressMappings.Parse(mappingContent, mappingSource);
        Console.WriteLine($"Mapping file: {(mappings.EntryCount > 0 ? $"{mappings.SourceFile} ({mappings.EntryCount} entries)" : "not found / empty - everything goes to the unmapped location")}");
        if (mappings.HasGlobalConstraints)
            Console.WriteLine($"Global constraint: {mappings.GlobalConstraintText}");

        // Every run scans the whole source folder. Processed mails are moved to the
        // archive folder, so the folder only ever contains unprocessed mails - a full
        // scan is cheap and reliable, and works for shared mailboxes where the read
        // state cannot be relied upon. lastSyncUtc is informational only.
        var query = SearchQuery.All;

        using var client = new ImapClient();
        if (account.AcceptUntrustedCertificates)
            client.ServerCertificateValidationCallback = (_, _, _, _) => true;

        var ssl = account.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.None;
        Console.WriteLine($"Connecting to {account.Server}:{account.Port} ...");
        await client.ConnectAsync(account.Server, account.Port, ssl, ct);
        await client.AuthenticateAsync(Encoding.UTF8, new NetworkCredential(account.UserName, account.Password), ct);
        Console.WriteLine("Connected and logged in.");

        var folder = await client.GetFolderAsync(account.FolderName, ct);

        // the (mandatory) archive folder - ensure it exists before opening any folder
        // (an IMAP connection can only have one folder open at a time)
        IMailFolder archiveFolder = await GetOrCreateFolderAsync(client, account.ArchiveFolder.Replace('\\', '/'), ct);
        Console.WriteLine($"Archive folder: '{archiveFolder.FullName}'");

        await folder.OpenAsync(FolderAccess.ReadWrite, ct);
        stats.InboxCountAtStart = folder.Count;
        Console.WriteLine($"Folder '{folder.FullName}' contains {folder.Count} message(s) ...");

        var uids = await folder.SearchAsync(query, ct);
        Console.WriteLine($"Selected {uids.Count} message(s) for download.");

        for (int i = 0; i < uids.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var message = await folder.GetMessageAsync(uids[i], ct);

            var subfolders = mappings.GetSubfolders(message);
            if (subfolders is null && string.IsNullOrWhiteSpace(target.UnmappedSubfolder))
            {
                // No criteria met and no unmapped subfolder configured: the mail is
                // deliberately ignored - not downloaded, not archived, not reported
                // as a leftover (the user opted out by leaving the unmapped subfolder
                // empty).
                stats.UnmappedSkipped++;
            }
            else
            {
                stats.MailsProcessed++;
                if (subfolders is null)
                {
                    var unmapped = FromAddressMappings.ParseSubfolderPath(target.UnmappedSubfolder) ?? Array.Empty<string>();
                    stats.Unmapped++;
                    await ProcessMessageAsync(message, target, sink, unmapped, stats, ct);
                    // deliberately NOT archived: it meets no criteria and is reported as a
                    // leftover mail (monitoring reports down)
                }
                else
                {
                    foreach (var segments in subfolders)
                        await ProcessMessageAsync(message, target, sink, segments, stats, ct);

                    // processed: mark read and move to the archive folder
                    try
                    {
                        await folder.StoreAsync(uids[i], new StoreFlagsRequest(StoreAction.Add, MessageFlags.Seen), ct);
                        await folder.MoveToAsync(uids[i], archiveFolder, ct);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[warn] could not mark read / move message {uids[i]}: {ex.Message}");
                    }
                }
            }

            if (i % 10 == 0 || i == uids.Count - 1)
                Console.WriteLine($"  progress: {i + 1}/{uids.Count} (files saved: {stats.FilesSaved})");
        }

        // leftover mails: everything that is still in the source folder and matches no
        // criterion (including the global "to=" default criterion). Deliberately
        // independent of the read flag - in a shared mailbox other users may have
        // read the mail, so "unread" is not a reliable signal. Only applies when an
        // unmapped subfolder is configured - otherwise unmapped mails are
        // deliberately ignored (see above) and must not trigger a down state.
        if ((mappings.EntryCount > 0 || mappings.HasGlobalConstraints) &&
            !string.IsNullOrWhiteSpace(target.UnmappedSubfolder))
        {
            for (var start = 0; start < folder.Count; start += 500)
            {
                var end = Math.Min(start + 499, folder.Count - 1);
                var summaries = await folder.FetchAsync(start, end, new FetchRequest(MessageSummaryItems.Envelope), ct);
                foreach (var summary in summaries)
                    if (!mappings.MatchesAny(summary.Envelope))
                        stats.LeftoverMails++;
            }
        }

        // archive cleanup by age
        if (account.ArchiveMaxAgeDays > 0)
        {
            await archiveFolder.OpenAsync(FolderAccess.ReadWrite, ct); // closes the inbox (single-open IMAP)
            var cutoff = DateTime.UtcNow.AddDays(-account.ArchiveMaxAgeDays);
            var old = await archiveFolder.SearchAsync(SearchQuery.SentBefore(cutoff), ct);
            if (old.Count > 0)
            {
                for (var i = 0; i < old.Count; i += 100)
                {
                    var batch = old.Skip(i).Take(100).ToList();
                    await archiveFolder.StoreAsync(batch, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Deleted), ct);
                }
                await archiveFolder.ExpungeAsync(ct);
                stats.ArchivedDeleted = old.Count;
            }
        }

        account.LastSyncUtc = DateTime.UtcNow;
        await CloseFolderSafely(folder, ct);
        await CloseFolderSafely(archiveFolder, ct);
        await client.DisconnectAsync(true, ct);
        sink.Dispose();

        if (stats.SaveErrors.Count > 0)
            stats.Error = $"{stats.SaveErrors.Count} file(s) could not be saved to the target.";

        return stats;
    }

    /// <summary>
    /// Saves the readable text and all attachments of one message into the given
    /// subfolder of the target. All files of a message share the same name prefix,
    /// so they group/sort together.
    /// </summary>
    private static async Task ProcessMessageAsync(MimeMessage message, TargetFolder target, IFileSink sink, string[] subfolderSegments, DownloadStats stats, CancellationToken ct)
    {
        var subfolder = string.Join('/', subfolderSegments); // "" = target root
        var prefix = BuildPrefix(message);

        static string RelPath(string subfolder, string name)
            => subfolder.Length == 0 ? name : subfolder + "/" + name;

        // --- mail text ---------------------------------------------------------
        var textName = prefix + target.FileExtension;
        if (await sink.ExistsAsync(RelPath(subfolder, textName), ct))
        {
            stats.FilesSkippedExisting++;
        }
        else
        {
            await TrySaveAsync(sink, RelPath(subfolder, textName),
                new UTF8Encoding(false).GetBytes(ExtractText(message)), textName, stats, ct);
        }

        // --- attachments ---------------------------------------------------------
        var attachments = new List<MimePart>();
        if (message.Body is { } body)
            CollectAttachments(body, attachments);

        for (var i = 0; i < attachments.Count; i++)
        {
            var part = attachments[i];
            if (part.Content is not { } content)
                continue;

            var name = prefix + "_" + GetAttachmentFileName(part, i);
            if (await sink.ExistsAsync(RelPath(subfolder, name), ct))
            {
                stats.FilesSkippedExisting++;
                continue;
            }

            using var ms = new MemoryStream();
            await content.DecodeToAsync(ms, ct);
            await TrySaveAsync(sink, RelPath(subfolder, name), ms.ToArray(), name, stats, ct);
        }
    }

    private static async Task TrySaveAsync(IFileSink sink, string relativePath, byte[] data, string name, DownloadStats stats, CancellationToken ct)
    {
        try
        {
            await sink.SaveAsync(relativePath, data, ct);
            stats.FilesSaved++;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            stats.SaveErrors.Add($"{name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Builds the shared file name prefix: yyyyMMdd-HHmm_message-id_sanitized-subject
    /// (leading part = the mail's sent date and time, minute precision).
    /// </summary>
    private static string BuildPrefix(MimeMessage message)
    {
        var date = message.Date is { Year: > 2000 } d ? d.LocalDateTime : DateTime.Now;

        var parts = new List<string> { date.ToString("yyyyMMdd-HHmm") };

        var mid = message.MessageId;
        if (!string.IsNullOrWhiteSpace(mid))
        {
            var hash = XxHash32(mid.Trim('<', '>')) % 1_000_000;
            parts.Add(hash.ToString("000000"));
        }

        var subject = Tui.SanitizeFileName(message.Subject ?? "");
        if (subject.Length > 60)
            subject = subject[..60].TrimEnd();
        if (subject.Length > 0)
            parts.Add(subject);
       

        var prefix = string.Join('_', parts);
        if (prefix.Length > 120)
            prefix = prefix[..120].TrimEnd();
        return prefix;
    }

    /// <summary>
    /// Builds the readable text file content: a small header block followed by the
    /// message body (text/plain is preferred, HTML is converted to plain text).
    /// </summary>
    public static string ExtractText(MimeMessage message)
    {
        var sb = new StringBuilder();

        void Line(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                sb.AppendLine($"{label}: {value}");
        }

        Line("Subject", message.Subject);
        Line("From", message.From?.ToString());
        Line("To", message.To?.ToString());
        Line("Cc", message.Cc?.ToString());
        Line("Date", message.Date is { Year: > 2000 } d ? d.LocalDateTime.ToString("G") : null);

        sb.AppendLine();
        sb.AppendLine("----");
        sb.AppendLine();

        var body = message.TextBody;
        if (string.IsNullOrWhiteSpace(body))
        {
            var html = message.HtmlBody;
            body = html is null ? null : HtmlToText(html);
        }

        sb.Append(body is null || body.Length == 0 ? "(no readable body)" : body.Trim());
        sb.AppendLine();

        return sb.ToString();
    }

    /// <summary>
    /// Lightweight HTML to plain text conversion (no external dependency).
    /// </summary>
    public static string HtmlToText(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return "";

        var s = html;
        s = Regex.Replace(s, "<(script|style)[^>]*>.*?</\\1>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        s = Regex.Replace(s, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "</(p|div|tr|li|ul|ol|table|h[1-6]|blockquote|pre|section|article|header|footer|title)>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "<[^>]+>", " ");
        s = WebUtility.HtmlDecode(s);
        s = Regex.Replace(s, "[ \\t]+", " ");
        s = Regex.Replace(s, " *\\n *", "\n");
        s = Regex.Replace(s, "\n{3,}", "\n\n");
        return s.Trim();
    }

    /// <summary>
    /// Recursively collects all attachment parts (skips inline images and the text/HTML body).
    /// MimeKit 4.x: MimeMessage is no longer a MimeEntity, so the body tree is walked manually.
    /// </summary>
    private static void CollectAttachments(MimeEntity entity, List<MimePart> result)
    {
        if (entity is Multipart multipart)
        {
            for (var i = 0; i < multipart.Count; i++)
                CollectAttachments(multipart[i], result);
        }
        else if (entity is MimePart part)
        {
            var hasName = !string.IsNullOrWhiteSpace(part.FileName);
            var isHtml = part.ContentType?.MimeType == "text/html";
            var isAttachment = part.IsAttachment || part.ContentDisposition?.IsAttachment == true;

            if (isAttachment || (hasName && !isHtml))
                result.Add(part);
        }
    }

    /// <summary>
    /// File name for an attachment (sanitized, length limited, extension preserved).
    /// </summary>
    private static string GetAttachmentFileName(MimePart part, int index)
    {
        var name = part.FileName;
        if (string.IsNullOrWhiteSpace(name))
            name = $"attachment{index + 1}";

        // never store path components from the mail
        name = Tui.SanitizeFileName(Path.GetFileName(name));

        // limit length, keep the extension (max 50 chars for the stem, 10 for the extension)
        var dotIdx = name.LastIndexOf('.');
        if (dotIdx > 0 && name.Length - dotIdx <= 10)
        {
            var stem = name[..dotIdx];
            var ext = name[dotIdx..];
            if (stem.Length > 50)
                stem = stem[..50];
            name = stem + ext;
        }
        else if (name.Length > 60)
        {
            name = name[..60];
        }

        return name.Length == 0 ? $"attachment{index + 1}" : name;
    }

    /// <summary>
    /// Returns the IMAP folder with the given path, creating it (and any missing
    /// parent folders) when it does not exist yet.
    /// </summary>
    private static async Task<IMailFolder> GetOrCreateFolderAsync(ImapClient client, string path, CancellationToken ct)
    {
        IMailFolder? folder = null;
        try
        {
            folder = await client.GetFolderAsync(path, ct);
        }
        catch (FolderNotFoundException)
        {
            folder = null; // does not exist yet
        }

        if (folder is not null && await FolderExistsAsync(folder, ct))
            return folder;

        // Create the missing path below the top-level parent of INBOX
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var parent = client.Inbox.ParentFolder ?? client.Inbox!;
        foreach (var segment in segments)
            parent = await parent!.CreateAsync(segment, isMessageFolder: true, ct);
        return parent!; // CreateAsync does not open the folder
    }

    private static async Task<bool> FolderExistsAsync(IMailFolder folder, CancellationToken ct)
    {
        try
        {
            await folder.OpenAsync(FolderAccess.ReadOnly, ct);
            await folder.CloseAsync(false, ct);
            return true;
        }
        catch (FolderNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Closes the folder; some server/engine combinations report the folder as already
    /// closed at this point - in that case the LOGOUT below cleans up.
    /// </summary>
    private static async Task CloseFolderSafely(IMailFolder folder, CancellationToken ct)
    {
        try
        {
            await folder.CloseAsync(false, ct);
        }
        catch (MailKit.FolderNotOpenException)
        {
            // ignore - the connection is closed right after
        }
    }

    /// <summary>
    /// XXH32 (32-bit XXHash) - fast, stable non-cryptographic hash.
    /// Same input always yields the same 32-bit value across runs/platforms.
    /// </summary>
    private static uint XxHash32(string input, uint seed = 0)
    {
        const uint P1 = 2654435761u;
        const uint P2 = 2246822519u;
        const uint P3 = 3266489917u;
        const uint P4 = 668265263u;
        const uint P5 = 374761393u;

        byte[] data = Encoding.UTF8.GetBytes(input);
        int len = data.Length;
        int idx = 0;
        uint h;

        if (len >= 16)
        {
            uint v1 = seed + P1 + P2;
            uint v2 = seed + P2;
            uint v3 = seed;
            uint v4 = seed - P1;

            while (idx <= len - 16)
            {
                v1 += ReadLE32(data, idx) * P2; v1 = (v1 << 13) | (v1 >> 19); v1 *= P1;
                v2 += ReadLE32(data, idx + 4) * P2; v2 = (v2 << 13) | (v2 >> 19); v2 *= P1;
                v3 += ReadLE32(data, idx + 8) * P2; v3 = (v3 << 13) | (v3 >> 19); v3 *= P1;
                v4 += ReadLE32(data, idx + 12) * P2; v4 = (v4 << 13) | (v4 >> 19); v4 *= P1;
                idx += 16;
            }

            h = (RotateLeft(v1, 1) + RotateLeft(v2, 7) + RotateLeft(v3, 12) + RotateLeft(v4, 18));
        }
        else
        {
            h = seed + P5;
        }

        h += (uint)len;

        while (idx <= len - 4)
        {
            h += ReadLE32(data, idx) * P3;
            h = (h << 17) | (h >> 15);
            h *= P4;
            idx += 4;
        }

        while (idx < len)
        {
            h += data[idx] * P5;
            h = (h << 11) | (h >> 21);
            h *= P1;
            idx++;
        }

        h ^= h >> 15;
        h *= P2;
        h ^= h >> 13;
        h *= P3;
        h ^= h >> 16;
        return h;

        static uint ReadLE32(byte[] b, int o) =>
            (uint)b[o] | ((uint)b[o + 1] << 8) | ((uint)b[o + 2] << 16) | ((uint)b[o + 3] << 24);

        static uint RotateLeft(uint x, int n) => (x << n) | (x >> (32 - n));
    }
}
