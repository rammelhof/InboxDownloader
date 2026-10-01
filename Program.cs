using System.Net;

using InboxDownloader;
using MailKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// Command line:  InboxDownloader.exe [service] [--test] [--config <path>]
//   service      run as a (Windows) service instead of the TUI
//   --test       test the IMAP connection and list all folders (recursive), then exit
//   --config P   use config file P instead of %PROGRAMDATA%\InboxDownloader\config.json
//                (useful for services running as LocalSystem / Network Service,
//                 e.g. --config C:\ProgramData\InboxDownloader\config.json)
string? configPath = null;
var serviceMode = false;
var testMode = false;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] is "service" or "--service")
        serviceMode = true;
    else if (args[i] is "--test" or "--test-connection" or "--folders")
        testMode = true;
    else if (args[i] == "--config" && i + 1 < args.Length)
        configPath = args[++i];
    else if (args[i].StartsWith("--config=", StringComparison.OrdinalIgnoreCase))
        configPath = args[i]["--config=".Length..];
}
if (configPath is not null)
    ConfigStore.SetConfigPath(configPath);
if (testMode)
{
    var testConfig = ConfigStore.Load();
    return await TestConnection(testConfig, CancellationToken.None) ? 0 : 1;
}
if (serviceMode)
    return await RunServiceAsync();

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var config = ConfigStore.Load();

while (true)
{
    try
    {
        PrintMenu(config);

        var choice = (Console.ReadLine() ?? "0").Trim();
        Console.WriteLine();

        switch (choice)
        {
            case "1":
                ConfigureAccount(ref config);
                SaveIfChanged(ref config, "Account settings saved.");
                break;
            case "2":
                await ConfigureTarget(config, cts.Token);
                SaveIfChanged(ref config, "Target settings saved.");
                break;
            case "3":
                PrintCurrentConfig(config);
                break;
            case "4":
                await Download(config, cts.Token);
                ConfigStore.Save(config);
                break;
            case "5":
                await TestConnection(config, cts.Token);
                break;
            case "6":
                ConfigureService(ref config);
                SaveIfChanged(ref config, "Service settings saved.");
                break;
            case "7":
                Console.WriteLine("Starting service in this console (Ctrl+C to stop) ...\n");
                await RunServiceAsync();
                config = ConfigStore.Load(); // the service may have saved a newer config (LastSyncUtc ...)
                break;
            case "0":
                return 0;
            default:
                Console.WriteLine("Unknown option.");
                break;
        }
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine();
        Console.WriteLine("Cancelled.");
        return 130;
    }
}

// ---------------------------------------------------------------- helpers

static void PrintMenu(AppConfig config)
{
    Tui.Header("InboxDownloader");
    Console.WriteLine($"  Account : {config.Account.Name}  ({config.Account.Server}:{config.Account.Port}, user: {(config.Account.UserName.Length > 0 ? config.Account.UserName : "(not set)")} )");
    Console.WriteLine($"  Target  : {DescribeTarget(config.Target)}");
    Console.WriteLine($"  Service : poll every {config.Service.PollIntervalMinutes} min, push every {config.Service.PushIntervalMinutes} min {(config.Service.PushUrl.Length > 0 ? "(push: set)" : "(push: off)")}");
    Console.WriteLine($"  Last sync : {(config.Account.LastSyncUtc is { } d ? d.ToLocalTime().ToString("G") + " (local)" : "never (first run)")} ");
    Tui.Rule();
    Console.WriteLine("  1) Configure IMAP account");
    Console.WriteLine("  2) Configure target (local folder / Nextcloud)");
    Console.WriteLine("  3) Show current configuration");
    Console.WriteLine("  4) Download mails (scans the whole inbox)");
    Console.WriteLine("  5) Test connection (lists all folders, recursive)");
    Console.WriteLine("  6) Configure service settings (poll interval, monitoring push URL)");
    Console.WriteLine("  7) Start service in this console (Ctrl+C to stop)");
    Console.WriteLine("  0) Exit");
    Console.Write("  > ");
}

static string DescribeTarget(TargetFolder t) => t.Mode switch
{
    TargetMode.Nextcloud => $"nextcloud: {t.WebDavBaseUrl} (user: {t.WebDavUser}) -> {t.DestinationRoot}",
    _ => t.Path.Length > 0 ? $"local: {t.Path}" : "(not set)"
};

static void SaveIfChanged(ref AppConfig config, string message)
{
    ConfigStore.Save(config);
    Console.WriteLine(message);
}

static void ConfigureAccount(ref AppConfig config)
{
    var a = config.Account;
    Tui.Header("IMAP account configuration");

    a.Name = Tui.Ask("Account name (label only)", a.Name);
    a.Server = Tui.AskRequired("IMAP server host", a.Server);
    a.Port = Tui.AskInt("Port", a.Port, 1, 65535);
    a.UseSsl = Tui.AskYesNo("Use SSL/TLS (STARTTLS for port 143)", a.UseSsl);
    a.AcceptUntrustedCertificates = Tui.AskYesNo("Accept untrusted/self-signed certificates?", a.AcceptUntrustedCertificates);
    a.FolderName = Tui.Ask("IMAP folder to download from", a.FolderName);
    a.ArchiveFolder = Tui.AskRequired("Archive folder (REQUIRED - processed mails are moved here; subfolders allowed: Archive/2026)", a.ArchiveFolder);
    a.ArchiveFolder = a.ArchiveFolder.Trim().TrimEnd('/', '\\');
    a.ArchiveMaxAgeDays = Tui.AskInt("Max age for archived mails in days (0 = keep forever)", a.ArchiveMaxAgeDays, 0, 3650);
    a.UserName = Tui.AskRequired("User name", a.UserName);
    if (Tui.AskYesNo("Set password", a.Password.Length == 0))
        a.Password = Tui.ReadPassword();

    config.Account = a;
}

static async Task ConfigureTarget(AppConfig config, CancellationToken ct)
{
    var t = config.Target;
    Tui.Header("Target configuration");
    Console.WriteLine("  The target is where the processed mail files are stored:");
    Console.WriteLine("    local     = a local folder (the mapping file lives in it)");
    Console.WriteLine("    nextcloud = a Nextcloud server via WebDAV (the mapping file is read from the destination root folder)");
    Console.WriteLine();

    while (true)
    {
        var mode = Tui.Ask("Target mode [local|nextcloud]", t.Mode.ToString().ToLowerInvariant()).ToLowerInvariant();
        if (mode is "local" or "nextcloud")
        {
            t.Mode = Enum.Parse<TargetMode>(mode, true);
            break;
        }
        Console.WriteLine("(enter local or nextcloud)");
    }

    if (t.Mode == TargetMode.Nextcloud)
    {
        t.WebDavBaseUrl = Tui.AskRequired("Nextcloud URL (e.g. https://cloud.example.com)", t.WebDavBaseUrl);
        t.WebDavUser = Tui.AskRequired("User name", t.WebDavUser);
        if (Tui.AskYesNo("Set password", t.WebDavPassword.Length == 0))
            t.WebDavPassword = Tui.ReadPassword();
        t.AcceptUntrustedCertificates = Tui.AskYesNo("Accept untrusted/self-signed certificates?", t.AcceptUntrustedCertificates);
        t.DestinationRoot = Tui.Ask("Destination root folder (relative to the user's files folder, e.g. Mails)", t.DestinationRoot);
        t.DestinationRoot = t.DestinationRoot.Trim().Trim('/').Trim('\\');
        if (string.IsNullOrWhiteSpace(t.DestinationRoot))
            t.DestinationRoot = "InboxDownloader";
    }
    else
    {
        t.Path = Tui.AskRequired("Target root folder (contains the mapping file)", t.Path);
        t.Path = Path.GetFullPath(t.Path);
    }

    t.MappingFileName = Tui.Ask("Mapping file name inside the target", t.MappingFileName);
    // "empty" is a meaningful value here (mails without a mapping are not downloaded),
    // so the current value is shown but an empty input clears the setting.
    t.UnmappedSubfolder = Tui.Ask(
        "Subfolder for mails without a mapping (empty = such mails are NOT downloaded; subfolders allowed: 200000\\er)"
        + $" [current: {(t.UnmappedSubfolder.Length > 0 ? t.UnmappedSubfolder : "(none)")}]", null);
    t.FileExtension = Tui.Ask("File extension for the saved mail text files", t.FileExtension);
    if (!t.FileExtension.StartsWith('.')) t.FileExtension = "." + t.FileExtension;

    config.Target = t;

    // show the mapping file (local: on disk, nextcloud: fetched via WebDAV)
    try
    {
        IFileSink sink = t.Mode == TargetMode.Nextcloud
            ? new WebDavDestination(t)
            : new LocalFileSink(t.Path);
        using (sink)
        {
            var content = await sink.ReadMappingFileAsync(t.MappingFileName, ct);
            var mappings = FromAddressMappings.Parse(content, sink.Description);
            if (mappings.EntryCount == 0 && !mappings.HasGlobalConstraints)
            {
                Console.WriteLine($"[info] Mapping file not found: {mappings.SourceFile}\n"
                          + "       Create it with lines like:\n"
                          + "         to=belege@example.com;erin@example.com   (global default: applied to every line without its own to:)\n"
                          + "         200000=office@example.com\n"
                          + "         200000\\er=post@example.com;office@example.com   (multiple addresses: ANY matches)\n"
                          + "         300000\\belege=to:belege@example.com   (match on recipient, To/Cc; overrides the global to:)\n"
                          + "         200000=from:post@example.com;to:belege@example.com   (from AND to must match)");
            }
            else
            {
                if (mappings.HasGlobalConstraints)
                    Console.WriteLine($"         global: {mappings.GlobalConstraintText}");
                Console.WriteLine($"[info] Mapping file {mappings.SourceFile} contains {mappings.EntryCount} entry(ies):");
                foreach (var rule in mappings.AllMappings)
                    Console.WriteLine($"         {rule.CriteriaText}  ->  {rule.SubfolderPath}");
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[warn] Could not read the mapping file: {ex.Message}");
    }
}

static void ConfigureService(ref AppConfig config)
{
    var s = config.Service;
    Tui.Header("Service configuration (InboxDownloader.exe service)");
    Console.WriteLine("  The service polls the inbox in the background and can report");
    Console.WriteLine("  the state of the last run to a push-based monitoring service");
    Console.WriteLine("  (e.g. Uptime Kuma): status=up on success, status=down on failure.");
    Console.WriteLine();

    s.PollIntervalMinutes = Tui.AskInt("Poll interval in minutes (IMAP download)", s.PollIntervalMinutes, 1, 24 * 60);

    if (Tui.AskYesNo("Change monitoring push URL?", s.PushUrl.Length == 0))
    {
        Console.Write("Monitoring push URL (masked; supports {status}, {msg}, {msgcount}, {ping} placeholders; empty = disabled): ");
        s.PushUrl = Tui.ReadPassword().Trim();
    }

    s.PushIntervalMinutes = Tui.AskInt("Push interval in minutes (how often the status is reported)", s.PushIntervalMinutes, 1, 24 * 60);

    Console.WriteLine($"  Push URL    : {(s.PushUrl.Length > 0 ? MaskPushUrl(s.PushUrl) : "(not set)")}");
    config.Service = s;
}

/// <summary>Hides the push token / query part of a push URL for display.</summary>
static string MaskPushUrl(string url)
{
    var slash = url.LastIndexOf('/');
    var masked = slash > 0 ? url[..(slash + 1)] + "***" : "***";
    var q = masked.IndexOf('?');
    return q > 0 ? masked[..q] : masked;
}

/// <summary>
/// Runs the app as a service. UseWindowsService() connects to the Windows Service
/// Control Manager when the process is started by it (installed service) and is a
/// no-op when started from a console (option 7 / "dotnet run -- service").
/// </summary>
static async Task<int> RunServiceAsync()
{
    var host = Host.CreateDefaultBuilder()
        .UseWindowsService()
        .ConfigureServices(s => s.AddHostedService<InboxWorker>())
        .Build();

    await host.RunAsync();
    return 0;
}

static void PrintCurrentConfig(AppConfig config)
{
    Tui.Header("Current configuration");
    Console.WriteLine($"  Config file : {ConfigStore.ConfigFilePath}");
    Console.WriteLine($"  Account     : {config.Account.Name}");
    Console.WriteLine($"  Server      : {config.Account.Server}:{config.Account.Port} (SSL: {config.Account.UseSsl}, untrusted: {config.Account.AcceptUntrustedCertificates})");
    Console.WriteLine($"  Folder      : {config.Account.FolderName}");
    Console.WriteLine($"  Archive     : {(config.Account.ArchiveFolder.Length > 0 ? config.Account.ArchiveFolder + (config.Account.ArchiveMaxAgeDays > 0 ? $" (delete after {config.Account.ArchiveMaxAgeDays} days)" : " (keep forever)") : "(NOT configured - REQUIRED, use option 1)")}");
    Console.WriteLine($"  User        : {config.Account.UserName}");
    Console.WriteLine($"  Password    : {(config.Account.Password.Length > 0 ? "(set)" : "(not set)")}");
    Console.WriteLine($"  Last sync   : {(config.Account.LastSyncUtc is { } d ? d.ToLocalTime().ToString("G") : "n/a")}");
    var t = config.Target;
    Console.WriteLine($"  Target      : {DescribeTarget(t)}");
    if (t.Mode == TargetMode.Nextcloud)
    {
        Console.WriteLine($"    Base URL    : {t.WebDavBaseUrl}");
        Console.WriteLine($"    User        : {t.WebDavUser}");
        Console.WriteLine($"    Password    : {(t.WebDavPassword.Length > 0 ? "(set)" : "(not set)")}");
        Console.WriteLine($"    Untrusted   : {t.AcceptUntrustedCertificates}");
        Console.WriteLine($"    Dest root   : {t.DestinationRoot}");
        Console.WriteLine($"    Mapping file: files/{t.WebDavUser}/{t.DestinationRoot}/{t.MappingFileName} (on the server)");
    }
    else
    {
        Console.WriteLine($"    Mapping file: {Path.Combine(t.Path, t.MappingFileName)}");
    }
    Console.WriteLine($"  Unmapped to : {(t.UnmappedSubfolder.Length > 0 ? t.UnmappedSubfolder : "(not downloaded)")}");
    Console.WriteLine($"  Extension   : {t.FileExtension}");
    Console.WriteLine($"  Service     : poll every {config.Service.PollIntervalMinutes} min");
    Console.WriteLine($"  Push URL    : {(config.Service.PushUrl.Length > 0 ? MaskPushUrl(config.Service.PushUrl) : "(not set)")}");
    Console.WriteLine($"  Push every  : {config.Service.PushIntervalMinutes} min");
}

static async Task Download(AppConfig config, CancellationToken ct)
{
    Tui.Header("Download mails (full inbox scan)");
    var stats = await MailDownloader.DownloadAsync(config, ct);

    if (stats.Error is not null)
    {
        Console.WriteLine($"[error] {stats.Error}");
        if (stats.SaveErrors.Count > 0)
        {
            Tui.Rule();
            foreach (var err in stats.SaveErrors)
                Console.WriteLine($"  [save error] {err}");
        }
        return;
    }

    Tui.Rule();
    Console.WriteLine($"  Mails processed   : {stats.MailsProcessed}");
    if (string.IsNullOrWhiteSpace(config.Target.UnmappedSubfolder))
        Console.WriteLine($"  Without mapping   : {stats.UnmappedSkipped} (not downloaded - unmapped subfolder not set)");
    else
        Console.WriteLine($"  Without mapping   : {stats.Unmapped} (left in {config.Account.FolderName}, not archived)");
    Console.WriteLine($"  Files saved       : {stats.FilesSaved}");
    Console.WriteLine($"  Files skipped     : {stats.FilesSkippedExisting} (already present)");
    Console.WriteLine($"  Archive deleted   : {stats.ArchivedDeleted} (older than {config.Account.ArchiveMaxAgeDays} days)");
    Console.WriteLine($"  LastSyncUtc now   : {config.Account.LastSyncUtc}");
    if (stats.LeftoverMails > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"  [warning] {stats.LeftoverMails} mail(s) in {config.Account.FolderName} match no mapping criteria");
        Console.WriteLine("  [warning] they were not processed/archived - monitoring will report DOWN until they are mapped or removed.");
    }
}

static async Task<bool> TestConnection(AppConfig config, CancellationToken ct)
{
    Tui.Header("Test connection");
    if (string.IsNullOrWhiteSpace(config.Account.Server) || string.IsNullOrWhiteSpace(config.Account.UserName))
    {
        Console.WriteLine("[error] Account not configured yet (option 1).");
        return false;
    }

    try
    {
        using var client = new MailKit.Net.Imap.ImapClient();
        if (config.Account.AcceptUntrustedCertificates)
            client.ServerCertificateValidationCallback = (_, _, _, _) => true;

        var ssl = config.Account.UseSsl
            ? MailKit.Security.SecureSocketOptions.SslOnConnect
            : MailKit.Security.SecureSocketOptions.None;
        await client.ConnectAsync(config.Account.Server, config.Account.Port, ssl, ct);
        await client.AuthenticateAsync(System.Text.Encoding.UTF8, new NetworkCredential(config.Account.UserName, config.Account.Password), ct);
        Console.WriteLine($"[ok] Connection successful. IMAP capabilities: {client.Capabilities}");

        // All folders, recursive (personal + shared namespaces).
        var namespaces = client.PersonalNamespaces.Concat(client.SharedNamespaces).ToList();
        if (namespaces.Count == 0)
            namespaces.Add(new MailKit.FolderNamespace('/', ""));

        Console.WriteLine("Folders:");
        foreach (var ns in namespaces)
        {
            var tops = await client.GetFoldersAsync(ns, false, ct);
            foreach (var top in tops)
                await PrintFolderAsync(top, 0, ct);
        }

        // Message counts of the folders this app actually uses.
        foreach (var path in new[] { config.Account.FolderName, config.Account.ArchiveFolder })
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            try
            {
                var folder = await client.GetFolderAsync(path, ct);
                await folder.OpenAsync(MailKit.FolderAccess.ReadOnly, ct);
                Console.WriteLine($"[ok] '{path}' contains {folder.Count} message(s).");
            }
            catch (MailKit.FolderNotFoundException)
            {
                Console.WriteLine($"[warn] folder '{path}' does not exist.");
            }
        }

        await client.DisconnectAsync(true, ct);
        return true;
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[error] {ex.Message}");
        return false;
    }
}

static async Task PrintFolderAsync(MailKit.IMailFolder folder, int depth, CancellationToken ct)
{
    var attrs = DescribeFolderAttributes(folder.Attributes);
    Console.WriteLine($"{new string(' ', depth * 2)}{folder.FullName}{(attrs.Length > 0 ? "  [" + attrs + "]" : "")}");

    // \NoInferiors = cannot have subfolders (skip); \HasNoChildren = none *currently*,
    // so we still look (servers may report it stale, e.g. INBOX).
    if (folder.Attributes.HasFlag(MailKit.FolderAttributes.NoInferiors))
        return;

    var subs = await folder.GetSubfoldersAsync(false, ct);
    foreach (var s in subs)
        await PrintFolderAsync(s, depth + 1, ct);
}

static string DescribeFolderAttributes(MailKit.FolderAttributes a)
{
    var list = new List<string>();
    foreach (var (name, flag) in new (string, MailKit.FolderAttributes)[]
    {
        ("\\Inbox", MailKit.FolderAttributes.Inbox),
        ("\\Archive", MailKit.FolderAttributes.Archive),
        ("\\Drafts", MailKit.FolderAttributes.Drafts),
        ("\\Flagged", MailKit.FolderAttributes.Flagged),
        ("\\Important", MailKit.FolderAttributes.Important),
        ("\\Junk", MailKit.FolderAttributes.Junk),
        ("\\Sent", MailKit.FolderAttributes.Sent),
        ("\\Trash", MailKit.FolderAttributes.Trash),
        ("\\All", MailKit.FolderAttributes.All),
    })
        if (a.HasFlag(flag))
            list.Add(name);
    return string.Join(" ", list);
}
