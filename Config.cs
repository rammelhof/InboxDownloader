namespace InboxDownloader;

/// <summary>
/// Application configuration persisted as JSON in %PROGRAMDATA%\InboxDownloader\config.json
/// (override the location with "--config <path>").
/// </summary>
public class AppConfig
{
    public ImapAccount Account { get; set; } = new();
    public TargetFolder Target { get; set; } = new();
    public ServiceSettings Service { get; set; } = new();
}

public class ImapAccount
{
    /// <summary>Display name of this account (e.g. "work inbox").</summary>
    public string Name { get; set; } = "My IMAP Account";

    public string Server { get; set; } = "imap.example.com";

    public int Port { get; set; } = 993;

    public bool UseSsl { get; set; } = true;

    /// <summary>true = accept untrusted certificates (self signed test servers).</summary>
    public bool AcceptUntrustedCertificates { get; set; }

    public string UserName { get; set; } = "";

    /// <summary>
    /// IMAP password / app password. Stored in plain text in the local config file
    /// (not encrypted) - keep the file to yourself.
    /// </summary>
    public string Password { get; set; } = "";

    /// <summary>IMAP folder to read messages from.</summary>
    public string FolderName { get; set; } = "INBOX";

    /// <summary>
    /// When the last download was successful (informational; every run scans the
    /// whole inbox regardless, because processed mails are moved to the archive).
    /// </summary>
    public DateTime? LastSyncUtc { get; set; }

    /// <summary>
    /// REQUIRED: IMAP folder that processed mails are moved to (e.g. "Archive" or
    /// "Archive/2026"). The folder is created when it does not exist yet.
    /// It is mandatory because in a shared mailbox the read state cannot be relied
    /// upon (other users may have read the mail); a missing archive folder puts the
    /// service into error state.
    /// </summary>
    public string ArchiveFolder { get; set; } = "";

    /// <summary>
    /// Optional: mails older than this many days are deleted from the archive folder
    /// (compared with the mail's Date header). 0 = keep archived mails forever.
    /// Only applied when <see cref="ArchiveFolder"/> is set.
    /// </summary>
    public int ArchiveMaxAgeDays { get; set; } = 0;
}

public enum TargetMode
{
    /// <summary>Local folder on this machine.</summary>
    Local,

    /// <summary>Nextcloud server (files stored directly via WebDAV).</summary>
    Nextcloud
}

/// <summary>
/// Where the processed mail files are stored: either a local folder or a Nextcloud
/// server. The mapping file (subfolder=address rules) lives in the target - for a
/// Nextcloud target it is read from the destination root folder via WebDAV.
/// </summary>
public class TargetFolder
{
    public TargetMode Mode { get; set; } = TargetMode.Local;

    /// <summary>
    /// Root folder where mails are stored (Mode = Local). Subfolders are created
    /// according to the mapping file (mapping.ini) that lives in this folder:
    ///   200000=office@example.com
    /// </summary>
    public string Path { get; set; } = "";

    /// <summary>
    /// Name of the file that contains subfolder=address mappings.
    /// Location: Mode = Local: inside <see cref="Path"/>;
    /// Mode = Nextcloud: inside <see cref="DestinationRoot"/> on the server.
    /// </summary>
    public string MappingFileName { get; set; } = "mapping.ini";

    /// <summary>
    /// Subfolder (relative to the target root) for mails that match no criterion.
    /// Empty string = mails that match no criterion are NOT downloaded at all
    /// (they are not archived and not reported as leftovers either).
    /// </summary>
    public string UnmappedSubfolder { get; set; } = "Unsorted";

    /// <summary>File extension used for the stored mail text files (.txt by default).</summary>
    public string FileExtension { get; set; } = ".txt";

    /// <summary>Nextcloud base URL, e.g. https://cloud.example.com (Mode = Nextcloud).</summary>
    public string? WebDavBaseUrl { get; set; }

    public string WebDavUser { get; set; } = "";

    /// <summary>
    /// Nextcloud / WebDAV password. Stored in plain text in the local config file (not encrypted).
    /// </summary>
    public string WebDavPassword { get; set; } = "";

    /// <summary>true = accept untrusted certificates (self signed test servers, Mode = Nextcloud).</summary>
    public bool AcceptUntrustedCertificates { get; set; }

    /// <summary>
    /// Destination root folder on the Nextcloud server, relative to the user's files
    /// folder (e.g. "Mails" -> files/&lt;user&gt;/Mails). Mode = Nextcloud.
    /// </summary>
    public string DestinationRoot { get; set; } = "InboxDownloader";
}

/// <summary>
/// Settings for running the app as a Windows service ("InboxDownloader.exe service").
/// The service polls the IMAP inbox every <see cref="PollIntervalMinutes"/> minutes and
/// optionally reports the state of the last run to a push-based monitor (e.g. Uptime
/// Kuma) every <see cref="PushIntervalMinutes"/> minutes.
/// </summary>
public class ServiceSettings
{
    /// <summary>Poll interval in minutes (how often the inbox is checked).</summary>
    public int PollIntervalMinutes { get; set; } = 15;

    /// <summary>
    /// Optional push URL for a monitoring service (e.g. Uptime Kuma
    /// "http://mon.lan:3001/api/push/TOKEN"). Supports the placeholders {status},
    /// {msg} and {ping} for other services, e.g.
    /// "http://mon.lan:3001/api/push/TOKEN?status={status}&msg={msg}&ping=".
    /// Empty = no push. Stored in plain text in the local config file (the URL
    /// usually contains a token, so keep the file to yourself).
    /// </summary>
    public string PushUrl { get; set; } = "";

    /// <summary>Push interval in minutes (how often the status is reported).</summary>
    public int PushIntervalMinutes { get; set; } = 15;
}
