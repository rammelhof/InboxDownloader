using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace InboxDownloader;

/// <summary>
/// A place where processed mail files are stored (and the mapping file is read from).
/// Relative paths always use '/' as the separator and are relative to the target root.
/// </summary>
public interface IFileSink : IDisposable
{
    /// <summary>Human readable description of this destination (for logs).</summary>
    string Description { get; }

    /// <summary>True if the file already exists (files are never overwritten).</summary>
    Task<bool> ExistsAsync(string relativePath, CancellationToken ct);

    /// <summary>Saves the file, creating any missing parent folders.</summary>
    Task SaveAsync(string relativePath, byte[] data, CancellationToken ct);

    /// <summary>Reads the mapping file from this destination, or null when it does not exist.</summary>
    Task<string?> ReadMappingFileAsync(string mappingFileName, CancellationToken ct);
}

/// <summary>File sink for a local folder.</summary>
public sealed class LocalFileSink : IFileSink
{
    private readonly string _root;

    public LocalFileSink(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
        Description = _root;
    }

    public string Description { get; }

    public Task<bool> ExistsAsync(string relativePath, CancellationToken ct)
        => Task.FromResult(File.Exists(CombineUnderRoot(relativePath)));

    public Task SaveAsync(string relativePath, byte[] data, CancellationToken ct)
    {
        var file = CombineUnderRoot(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, data);
        return Task.CompletedTask;
    }

    public Task<string?> ReadMappingFileAsync(string mappingFileName, CancellationToken ct)
    {
        var file = CombineUnderRoot(mappingFileName);
        return Task.FromResult<string?>(File.Exists(file) ? File.ReadAllText(file) : null);
    }

    /// <summary>Joins the root and a relative path, guarding against path traversal.</summary>
    private string CombineUnderRoot(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Path escapes the target folder: {relativePath}");
        return full;
    }

    public void Dispose() { }
}

/// <summary>
/// File sink for a Nextcloud server via WebDAV (remote.php/dav/files/&lt;user&gt;/).
/// Files are stored below the configured destination root folder.
/// </summary>
public sealed class WebDavDestination : IFileSink
{
    private readonly HttpClient _http;
    private readonly Uri _davRoot;
    private readonly string[] _rootSegments;
    private readonly HashSet<string> _existingDirs = new(StringComparer.OrdinalIgnoreCase) { "" };

    public WebDavDestination(TargetFolder target)
    {
        var baseUri = new Uri(target.WebDavBaseUrl!.TrimEnd('/') + "/");
        _davRoot = new Uri(baseUri, "remote.php/dav/files/" + Uri.EscapeDataString(target.WebDavUser) + "/");
        _rootSegments = target.DestinationRoot
            .Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

        var handler = new HttpClientHandler();
        if (target.AcceptUntrustedCertificates)
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;

        _http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{target.WebDavUser}:{target.WebDavPassword}")));
        _http.DefaultRequestHeaders.Add("User-Agent", "InboxDownloader/1.0");

        Description = $"{target.WebDavBaseUrl} (user: {target.WebDavUser}) -> {target.DestinationRoot}";
    }

    public string Description { get; }

    public Task<bool> ExistsAsync(string relativePath, CancellationToken ct)
        => HeadExistsAsync(_http, BuildUri(relativePath), ct);

    public async Task SaveAsync(string relativePath, byte[] data, CancellationToken ct)
    {
        var parts = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            throw new IOException($"Not a file below the root: {relativePath}");

        var dirSegments = _rootSegments.Concat(parts[..^1]).ToArray();
        var dirPath = await EnsureDirectoryAsync(dirSegments, ct);
        if (dirPath is null)
            throw new IOException($"Could not create directory {string.Join("/", dirSegments)}");

        var relativeUri = dirPath.Length > 0 ? $"{dirPath}/{Escape(parts[^1])}" : Escape(parts[^1]);
        using var put = new HttpRequestMessage(HttpMethod.Put, new Uri(_davRoot, relativeUri))
        {
            Content = new ByteArrayContent(data)
        };
        // Nextcloud preserves the file modification time from this header
        put.Headers.Add("X-OC-Mtime", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString("F0"));

        using var response = await _http.SendAsync(put, ct);
        if (!response.IsSuccessStatusCode)
            throw new IOException($"PUT failed with {(int)response.StatusCode}");
    }

    public async Task<string?> ReadMappingFileAsync(string mappingFileName, CancellationToken ct)
    {
        var segments = _rootSegments.Append(mappingFileName).ToArray();
        var uri = new Uri(_davRoot, EscapePath(string.Join("/", segments)));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            if (!response.IsSuccessStatusCode)
                throw new IOException($"GET failed with {(int)response.StatusCode}");
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (HttpRequestException)
        {
            return null; // not reachable / not found -> treat as "no mapping file"
        }
    }

    private Uri BuildUri(string relativePath)
    {
        var segments = _rootSegments.Concat(relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries)).ToArray();
        return new Uri(_davRoot, EscapePath(string.Join("/", segments)));
    }

    /// <summary>
    /// Creates the WebDAV directory (all missing segments) and returns its relative
    /// path below the DAV root, or null if creation failed.
    /// </summary>
    private async Task<string?> EnsureDirectoryAsync(string[] segments, CancellationToken ct)
    {
        var accumulated = "";
        foreach (var segment in segments)
        {
            accumulated = accumulated.Length == 0 ? segment : accumulated + "/" + segment;
            if (_existingDirs.Contains(accumulated))
                continue;

            var uri = new Uri(_davRoot, EscapePath(accumulated));
            using var request = new HttpRequestMessage(new HttpMethod("MKCOL"), uri);
            using var response = await _http.SendAsync(request, ct);

            // 201 Created / 405 Method Not Allowed (already exists) are both fine
            if (response.StatusCode is HttpStatusCode.Created or (HttpStatusCode)405)
                _existingDirs.Add(accumulated);
            else
                return null;
        }

        return accumulated;
    }

    private static async Task<bool> HeadExistsAsync(HttpClient http, Uri uri, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, uri);
            using var response = await http.SendAsync(request, ct);
            return response.StatusCode == HttpStatusCode.OK;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private static string Escape(string segment) => Uri.EscapeDataString(segment);

    private static string EscapePath(string relativePath) =>
        string.Join("/", relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

    public void Dispose() => _http.Dispose();
}
