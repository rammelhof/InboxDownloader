// Minimal Uptime Kuma "Push" endpoint for testing InboxDownloader's health push.
// Usage: dotnet run -- [port]      (default 1301)
// Accepts any request under /api/push/<token> and logs method, path and query.
// Returns 200 "OK" (Uptime Kuma behaviour).
using System.Net;

var port = args.Length > 0 ? int.Parse(args[0]) : 1301;

var listener = new HttpListener();
listener.Prefixes.Add($"http://127.0.0.1:{port}/");
listener.Start();
Console.WriteLine($"[kuma] listening on http://127.0.0.1:{port}/api/push/...");

while (true)
{
    var ctx = await listener.GetContextAsync();
    var req = ctx.Request;
    Console.WriteLine($"{DateTime.Now:HH:mm:ss} {req.HttpMethod} {req.Url?.PathAndQuery}");

    var body = System.Text.Encoding.UTF8.GetBytes("OK");
    ctx.Response.StatusCode = 200;
    ctx.Response.ContentType = "text/plain";
    await ctx.Response.OutputStream.WriteAsync(body);
    ctx.Response.Close();
}
