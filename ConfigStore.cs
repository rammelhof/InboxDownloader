using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InboxDownloader;

/// <summary>
/// Loads / saves the JSON config file inside %PROGRAMDATA%\InboxDownloader\config.json
/// (Windows only, by design). A different location can be used via "--config <path>".
/// </summary>
public static class ConfigStore
{
    private static string? _overridePath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// Uses an explicit config file path (via "--config <path>"). When not set, the
    /// default %PROGRAMDATA%\InboxDownloader\config.json is used.
    /// </summary>
    public static void SetConfigPath(string path) => _overridePath = Path.GetFullPath(path);

    public static string ConfigDirectory => _overridePath is not null
        ? Path.GetDirectoryName(_overridePath)!
        : DefaultConfigDirectory;

    public static string ConfigFilePath => _overridePath ?? Path.Combine(DefaultConfigDirectory, "config.json");

    private static string DefaultConfigDirectory
    {
        get
        {
            // %PROGRAMDATA% (C:\ProgramData) - shared by all users, so the same config
            // works for the TUI and for a service running under any account.
            var commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrEmpty(commonData))
                throw new InvalidOperationException("Could not resolve %PROGRAMDATA%.");
            return Path.Combine(commonData, "InboxDownloader");
        }
    }

    /// <summary>
    /// Config location of older versions (%APPDATA%\InboxDownloader\config.json) - used
    /// to hint that an existing config should be moved.
    /// </summary>
    private static string LegacyConfigFilePath
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return string.IsNullOrEmpty(appData) ? "" : Path.Combine(appData, "InboxDownloader", "config.json");
        }
    }

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigFilePath))
            {
                var json = File.ReadAllText(ConfigFilePath);
                var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
                MigrateLegacySyncSection(json, config);
                return config;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[warn] Could not read config ({ex.Message}); starting with defaults.");
        }

        // No config at the default location, but an old one exists in %APPDATA%:
        // hint that it should be moved (or used via --config).
        var legacy = LegacyConfigFilePath;
        if (_overridePath is null && legacy.Length > 0 && File.Exists(legacy))
            Console.WriteLine($"[info] Found a config from an older location: {legacy}\n"
                            + $"       Copy it to {ConfigFilePath} or start with --config {legacy}");

        return new AppConfig
        {
            Account = { Server = "imap.example.com", UserName = "" },
            Target = { Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Mail") }
        };
    }

    /// <summary>
    /// Old config versions stored the upload destination in a separate "sync" section.
    /// A "nextcloud" sync destination becomes a Nextcloud target; a "folder" one is
    /// dropped (the local target keeps the files).
    /// </summary>
    private static void MigrateLegacySyncSection(string json, AppConfig config)
    {
        LegacyConfig? legacy;
        try
        {
            legacy = JsonSerializer.Deserialize<LegacyConfig>(json, JsonOptions);
        }
        catch
        {
            return;
        }

        if (legacy?.Sync is not { Mode: { } mode })
            return;

        switch (mode.ToLowerInvariant())
        {
            case "nextcloud" when config.Target.Mode == TargetMode.Local:
                var t = config.Target;
                t.Mode = TargetMode.Nextcloud;
                if (!string.IsNullOrWhiteSpace(legacy.Sync.WebDavBaseUrl))
                    t.WebDavBaseUrl = legacy.Sync.WebDavBaseUrl;
                if (!string.IsNullOrWhiteSpace(legacy.Sync.WebDavUser))
                    t.WebDavUser = legacy.Sync.WebDavUser;
                if (!string.IsNullOrWhiteSpace(legacy.Sync.WebDavPassword))
                    t.WebDavPassword = legacy.Sync.WebDavPassword;
                t.AcceptUntrustedCertificates = legacy.Sync.AcceptUntrustedCertificates;
                if (!string.IsNullOrWhiteSpace(legacy.Sync.DestinationRoot))
                    t.DestinationRoot = legacy.Sync.DestinationRoot;
                break;

            case "folder":
                Console.WriteLine("[warn] Old config used a 'folder' sync destination (local copy). This is no longer supported - files stay in the local target folder.");
                break;
        }
    }

    private sealed class LegacyConfig
    {
        public LegacySync? Sync { get; set; }
    }

    private sealed class LegacySync
    {
        public string? Mode { get; set; }
        public string? WebDavBaseUrl { get; set; }
        public string? WebDavUser { get; set; }
        public string? WebDavPassword { get; set; }
        public bool AcceptUntrustedCertificates { get; set; }
        public string? DestinationRoot { get; set; }
    }

    public static void Save(AppConfig config)
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            var json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(ConfigFilePath, json);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Console.WriteLine($"\n[error] Could not save config to {ConfigFilePath}: {ex.Message}");
            Console.WriteLine("        The default location needs write access to the parent folder (C:\\ProgramData");
            Console.WriteLine("        is usually only writable by administrators). Run the TUI elevated,");
            Console.WriteLine("        or store the config in a writable location: InboxDownloader.exe --config <path>");
        }
    }
}
