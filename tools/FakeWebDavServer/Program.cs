using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

// Minimal single-user WebDAV server for testing InboxDownloader's Nextcloud sync.
// Usage: dotnet run -- <storage-root-dir> <port> [user:password]
// All requests below /remote.php/dav/files/<user>/ are mapped into <storage-root-dir>.
// Any Basic auth is accepted unless [user:password] is given.

var rootDir = args.Length > 0 ? Path.GetFullPath(args[0]) : @"C:\tmp\webdav-root";
var port = args.Length > 1 && int.TryParse(args[1], out var p) ? p : 1144;
var expectedAuth = args.Length > 2 && args[2].Contains(':') ? Convert.ToBase64String(Encoding.UTF8.GetBytes(args[2])) : null;

Directory.CreateDirectory(rootDir);

var listener = new TcpListener(IPAddress.Loopback, port);
listener.Start();
Log($"[webdav] listening on http://127.0.0.1:{port}  (root: {rootDir})");

while (true)
{
    var client = await listener.AcceptTcpClientAsync();
    _ = Task.Run(() => HandleClient(client));
}

return;

static void Log(string message) =>
    Console.WriteLine($"{DateTime.Now:HH:mm:ss} {message}");

/// <summary>Reads one CRLF-terminated line byte by byte (no buffering, safe to mix with raw body reads).</summary>
static async Task<string?> ReadLineRaw(NetworkStream stream)
{
    var sb = new StringBuilder();
    var one = new byte[1];
    while (true)
    {
        var r = await stream.ReadAsync(one, 0, 1);
        if (r <= 0) return null;
        if (one[0] == (byte)'\n')
            return sb.ToString();
        if (one[0] != (byte)'\r')
            sb.Append((char)one[0]);
    }
}

async Task HandleClient(TcpClient client)
{
    try
    {
        using (client)
        using (var stream = client.GetStream())
        {
            // NOTE: no StreamReader here - it would buffer ahead into the PUT body,
            // which is read from the raw stream below.
            var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { NewLine = "\r\n" };

            var requestLine = await ReadLineRaw(stream);
            if (string.IsNullOrEmpty(requestLine)) return;

            var parts = requestLine.Split(' ');
            if (parts.Length < 2) return;
            var method = parts[0].ToUpperInvariant();
            var target = parts[1];

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                var line = await ReadLineRaw(stream);
                if (line is null) return;
                if (line.Length == 0) break;
                var colon = line.IndexOf(':');
                if (colon > 0)
                    headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }

            Log($"C: {method} {target}");

            // ---- authorization -------------------------------------------------
            if (expectedAuth is not null)
            {
                var auth = headers.GetValueOrDefault("Authorization", "");
                if (!auth.StartsWith("Basic ") || !auth["Basic ".Length..].Equals(expectedAuth, StringComparison.Ordinal))
                {
                    await WriteResponse(writer, stream, 401, "Unauthorized",
                        new Dictionary<string, string> { ["WWW-Authenticate"] = "Basic realm=\"fake\"" }, Array.Empty<byte>());
                    return;
                }
            }

            // ---- path mapping ----------------------------------------------------
            var prefixIndex = target.IndexOf("/remote.php/dav/files/", StringComparison.OrdinalIgnoreCase);
            if (prefixIndex < 0)
            {
                await WriteResponse(writer, stream, 404, "Not Found", null, Encoding.UTF8.GetBytes("not found"));
                return;
            }

            var relative = Uri.UnescapeDataString(target[(prefixIndex + "/remote.php/dav/files/".Length)..]);
            // strip the user segment
            var slash = relative.IndexOf('/');
            if (slash >= 0) relative = relative[(slash + 1)..];

            var full = MapPath(rootDir, relative);
            if (full is null)
            {
                await WriteResponse(writer, stream, 403, "Forbidden", null, Encoding.UTF8.GetBytes("forbidden"));
                return;
            }

            // ---- dispatch ------------------------------------------------------
            switch (method)
            {
                case "OPTIONS":
                    await WriteResponse(writer, stream, 200, "OK", new Dictionary<string, string>
                    {
                        ["DAV"] = "1, 2",
                        ["Allow"] = "OPTIONS, GET, HEAD, PUT, PROPFIND, MKCOL, DELETE"
                    }, Array.Empty<byte>());
                    break;

                case "PROPFIND":
                {
                    string body;
                    int status;
                    if (Directory.Exists(full))
                    {
                        body = $"""
<?xml version="1.0" encoding="utf-8"?>
<d:multistatus xmlns:d="DAV:">
<d:response><d:href>{EscapeXml(target)}</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
</d:multistatus>
""";
                        status = 207;
                    }
                    else if (File.Exists(full))
                    {
                        var fi = new FileInfo(full);
                        body = $"""
<?xml version="1.0" encoding="utf-8"?>
<d:multistatus xmlns:d="DAV:">
<d:response><d:href>{EscapeXml(target)}</d:href><d:propstat><d:prop><d:resourcetype/><d:getcontentlength>{fi.Length}</d:getcontentlength></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
</d:multistatus>
""";
                        status = 207;
                    }
                    else
                    {
                        body = $"""
<?xml version="1.0" encoding="utf-8"?>
<d:multistatus xmlns:d="DAV:">
<d:response><d:href>{EscapeXml(target)}</d:href><d:propstat><d:prop/></d:propstat><d:status>HTTP/1.1 404 Not Found</d:status></d:propstat></d:response>
</d:multistatus>
""";
                        status = 404;
                    }
                    await WriteResponse(writer, stream, status, StatusText(status),
                        new Dictionary<string, string> { ["Content-Type"] = "application/xml; charset=utf-8" },
                        Encoding.UTF8.GetBytes(body));
                    break;
                }

                case "MKCOL":
                {
                    if (File.Exists(full) || Directory.Exists(full))
                    {
                        await WriteResponse(writer, stream, 405, "Method Not Allowed", null, Encoding.UTF8.GetBytes("exists"));
                        break;
                    }
                    if (!Directory.Exists(Path.GetDirectoryName(full)!))
                    {
                        await WriteResponse(writer, stream, 409, "Conflict", null, Encoding.UTF8.GetBytes("parent missing"));
                        break;
                    }
                    Directory.CreateDirectory(full);
                    await WriteResponse(writer, stream, 201, "Created", null, Array.Empty<byte>());
                    break;
                }

                case "PUT":
                {
                    var length = headers.TryGetValue("Content-Length", out var l) && int.TryParse(l, out var n) ? n : 0;
                    var buf = new byte[length];
                    var total = 0;
                    while (total < length)
                    {
                        var r = await stream.ReadAsync(buf, total, length - total);
                        if (r <= 0) break;
                        total += r;
                    }

                    var existed = File.Exists(full);
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    await File.WriteAllBytesAsync(full, buf[..total]);
                    await WriteResponse(writer, stream, existed ? 204 : 201, StatusText(existed ? 204 : 201), null, Array.Empty<byte>());
                    Log($"   PUT {target} ({total} bytes)");
                    break;
                }

                case "HEAD":
                {
                    if (File.Exists(full))
                    {
                        await WriteResponse(writer, stream, 200, "OK", new Dictionary<string, string> { ["Content-Length"] = new FileInfo(full).Length.ToString() }, Array.Empty<byte>());
                    }
                    else
                    {
                        await WriteResponse(writer, stream, 404, "Not Found", null, Array.Empty<byte>());
                    }
                    break;
                }

                case "GET":
                {
                    if (File.Exists(full))
                    {
                        var data = await File.ReadAllBytesAsync(full);
                        await WriteResponse(writer, stream, 200, "OK", new Dictionary<string, string> { ["Content-Length"] = data.Length.ToString() }, data);
                    }
                    else
                    {
                        await WriteResponse(writer, stream, 404, "Not Found", null, Encoding.UTF8.GetBytes("not found"));
                    }
                    break;
                }

                case "DELETE":
                {
                    if (File.Exists(full))
                    {
                        File.Delete(full);
                        await WriteResponse(writer, stream, 204, "No Content", null, Array.Empty<byte>());
                    }
                    else
                    {
                        await WriteResponse(writer, stream, 404, "Not Found", null, Array.Empty<byte>());
                    }
                    break;
                }

                default:
                    await WriteResponse(writer, stream, 405, "Method Not Allowed", null, Array.Empty<byte>());
                    break;
            }
        }
    }
    catch (Exception ex)
    {
        Log($"[webdav] client error: {ex.Message}");
    }
}

static async Task WriteResponse(StreamWriter writer, Stream stream, int status, string statusText, Dictionary<string, string>? headers, byte[] body)
{
    var sb = new StringBuilder();
    sb.Append($"HTTP/1.1 {status} {statusText}\r\n");
    if (headers is not null)
        foreach (var (key, value) in headers)
            sb.Append($"{key}: {value}\r\n");
    sb.Append($"Content-Length: {body.Length}\r\n");
    sb.Append("Connection: close\r\n\r\n");

    var headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
    await stream.WriteAsync(headerBytes);
    if (body.Length > 0)
        await stream.WriteAsync(body);
    await stream.FlushAsync();
}

/// <summary>Maps a WebDAV-relative path into the storage root, refusing path traversal.</summary>
static string? MapPath(string root, string relative)
{
    if (string.IsNullOrWhiteSpace(relative))
        return root;

    var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
    return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
}

static string StatusText(int code) => code switch
{
    200 => "OK",
    201 => "Created",
    204 => "No Content",
    207 => "Multi-Status",
    401 => "Unauthorized",
    403 => "Forbidden",
    404 => "Not Found",
    405 => "Method Not Allowed",
    409 => "Conflict",
    _ => "Error"
};

static string EscapeXml(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
