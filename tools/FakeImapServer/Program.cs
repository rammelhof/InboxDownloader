// Minimal multi-folder IMAP server for testing InboxDownloader.
// Serves the *.eml files found in the directory given as argv[1] (default: current dir)
// as the INBOX folder. Listens on 127.0.0.1 port given as argv[2] (default 1143), no TLS.
// Optional argv[3] "user:password" - when given, LOGIN must match exactly (auth is then enforced).
//
// Supported: CAPABILITY, LOGIN, SELECT/EXAMINE, LIST, NAMESPACE, NOOP, ID, ENABLE,
// SEARCH (ALL/UNSEEN/SEEN/DELETED/SINCE/BEFORE/ON/NOT/AND/OR), FETCH (UID/FLAGS/
// INTERNALDATE/RFC822.SIZE/ENVELOPE/BODY[...]), STORE/UID STORE, CREATE, COPY/UID COPY,
// EXPUNGE/UID EXPUNGE, CLOSE, LOGOUT.
// Folders persist for the lifetime of the process (state is server-wide, in memory).
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

var dataDir = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
var port = args.Length > 1 ? int.Parse(args[1]) : 1143;
var expectedAuth = ParseAuth(args.Length > 2 ? args[2] : null);
if (expectedAuth is { } auth)
    Console.WriteLine($"Auth enforced: user='{auth.User}'");

// seed INBOX (a file named *.read.eml is seeded as already read - simulates a
// shared mailbox where another user read the mail first)
ServerState.Folders["INBOX"] = new Folder("INBOX");
foreach (var file in Directory.GetFiles(dataDir, "*.eml").OrderBy(f => f, StringComparer.Ordinal))
{
    var data = File.ReadAllBytes(file);
    var flags = file.EndsWith(".read.eml", StringComparison.OrdinalIgnoreCase)
        ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"\Seen" }
        : null;
    ServerState.Folders["INBOX"].Add(data, flags: flags);
}
Console.WriteLine($"Serving {ServerState.Folders["INBOX"].Mails.Count} message(s) from {dataDir} on port {port}");

var listener = new TcpListener(IPAddress.Loopback, port);
listener.Start();
while (true)
{
    var client = await listener.AcceptTcpClientAsync();
    Console.WriteLine("[server] client connected");
    _ = Task.Run(() => HandleAsync(client, expectedAuth));
}

// ------------------------------------------------------------------ local fns

static async Task HandleAsync(TcpClient client, AuthCheck? expectedAuth)
{
    try
    {
        await HandleCoreAsync(client, expectedAuth);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[server] FATAL: {ex.Message}");
    }
    finally
    {
        client.Dispose();
    }
}

static Folder GetFolder(string name)
{
    if (string.IsNullOrWhiteSpace(name))
        name = "INBOX";
    if (!ServerState.Folders.TryGetValue(name, out var folder))
        throw new FolderNotFoundException(name);
    return folder;
}

static async Task HandleCoreAsync(TcpClient client, AuthCheck? expectedAuth)
{
    using var stream = client.GetStream();
    var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\r\n" };
    var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);

    async Task Write(string line) { Console.WriteLine("S: " + line); writer.WriteLine(line); await writer.FlushAsync(); }

    Folder? current = null;

    await Write("* OK [CAPABILITY IMAP4rev1 UIDPLUS] Fake IMAP ready");

    try
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Length == 0) continue;
            var tag = line[..line.IndexOf(' ')];
            var command = line[(line.IndexOf(' ') + 1)..].Trim();
            var words = Tokenize(command);
            var verb = words[0].ToUpperInvariant();
            var isUidPrefixed = verb == "UID" && words.Count > 1;
            if (isUidPrefixed)
            {
                verb = words[1].ToUpperInvariant();
                words = words.Skip(1).ToList();
            }
            Console.WriteLine($"C: {line}");

            switch (verb)
            {
                case "CAPABILITY":
                    await Write("* CAPABILITY IMAP4rev1 UIDPLUS");
                    await Write($"{tag} OK CAPABILITY completed");
                    break;

                case "NOOP":
                    await Write($"{tag} OK NOOP completed");
                    break;

                case "ID":
                    await Write("* ID (\"name\" \"fake\" \"version\" \"1.0\")");
                    await Write($"{tag} OK ID completed");
                    break;

                case "ENABLE":
                    await Write($"{tag} OK ENABLE completed");
                    break;

                case "NAMESPACE":
                    await Write("* NAMESPACE ((\"\" \"/\") ((NIL NIL)) ((NIL NIL)))");
                    await Write($"{tag} OK NAMESPACE completed");
                    break;

                case "LOGIN":
                case "AUTHENTICATE":
                    if (expectedAuth is null ||
                        (verb == "LOGIN" && TryParseLogin(command, out var loginUser, out var loginPass) &&
                         loginUser == expectedAuth.Value.User && loginPass == expectedAuth.Value.Password))
                    {
                        await Write($"{tag} OK LOGIN completed");
                    }
                    else
                    {
                        Console.WriteLine("[server] AUTH FAILED");
                        await Write($"{tag} NO [AUTHENTICATIONFAILED] authentication failed");
                    }
                    break;

                case "SELECT":
                case "EXAMINE":
                    {
                        var folderName = words.Count > 1 ? words[1] : "INBOX";
                        try
                        {
                            current = GetFolder(folderName);
                            await Write($"* {current.Mails.Count} EXISTS");
                            await Write("* 0 RECENT");
                            await Write("* OK [UIDVALIDITY 1]");
                            await Write($"* OK [UIDNEXT {current.NextUid}]");
                            await Write("* FLAGS (\\Seen \\Answered \\Flagged \\Deleted \\Draft)");
                            await Write("* OK [PERMANENTFLAGS (\\Seen \\Answered \\Flagged \\Deleted \\Draft)]");
                            await Write($"{tag} OK {(verb == "SELECT" ? "[READ-WRITE] SELECT" : "[READ-ONLY] EXAMINE")} completed");
                        }
                        catch (FolderNotFoundException)
                        {
                            current = null;
                            await Write($"{tag} NO [NONEXISTENT] folder does not exist");
                        }
                    }
                    break;

                case "LIST":
                    {
                        // LIST <refname> <pattern> - both possibly quoted.
                        // MailKit 4.x sends e.g. LIST "" "", LIST "" "*",
                        // LIST "" "INBOX" and LIST "" "Archive/%" (RFC 3501 '%').
                        var refName = words.Count > 1 ? Unquote(words[1]) : "";
                        var pattern = words.Count > 2 ? Unquote(words[2]) : "*";
                        foreach (var f in ServerState.Folders.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                        {
                            var parent = f.Name == "INBOX" ? "" : ParentOf(f.Name);
                            if (refName.Length > 0 && parent != refName && !f.Name.StartsWith(refName + "/", StringComparison.OrdinalIgnoreCase))
                                continue;

                            // path relative to the reference name
                            var rel = refName == "" || f.Name == refName
                                ? f.Name
                                : f.Name[(refName.Length + 1)..];

                            var matches = pattern.Length == 0 || pattern == "*"
                                ? parent == refName
                                : pattern.Contains('/')
                                    ? WildcardMatches(pattern, rel)
                                    : parent == refName && WildcardMatches(pattern, rel);
                            if (!matches)
                                continue;

                            var hasChildren = ServerState.Folders.Keys.Any(n =>
                                n.StartsWith(f.Name + "/", StringComparison.OrdinalIgnoreCase));
                            var flags = hasChildren ? "\\HasChildren" : "\\HasNoChildren";
                            var quotedName = f.Name == "INBOX" ? "INBOX" : $"\"{f.Name}\"";
                            // Note: MailKit 4.x expects * LIST <flags> <delim> <name> without a refname
                            await Write($"* LIST ({flags}) \"/\" {quotedName}");
                        }
                        await Write($"{tag} OK LIST completed");
                    }
                    break;

                case "CREATE":
                    {
                        var folderName = words[1];
                        if (ServerState.Folders.ContainsKey(folderName))
                            await Write($"{tag} NO folder already exists");
                        else
                        {
                            ServerState.Folders[folderName] = new Folder(folderName);
                            Console.WriteLine($"[server] created folder {folderName}");
                            await Write($"{tag} OK CREATE completed");
                        }
                    }
                    break;

                case "SEARCH":
                    {
                        if (current is null)
                        {
                            await Write($"{tag} NO not in selected folder");
                            break;
                        }
                        var matched = new List<int>();
                        for (var i = 0; i < current.Mails.Count; i++)
                        {
                            if (SearchEval(current.Mails[i], words.Skip(1).ToArray()))
                                matched.Add(isUidPrefixed ? (int)current.Mails[i].Uid : i + 1);
                        }
                        await Write($"* SEARCH {string.Join(' ', matched)}");
                        await Write($"{tag} OK SEARCH completed");
                    }
                    break;

                case "STORE":
                    {
                        if (current is null)
                        {
                            await Write($"{tag} NO not in selected folder");
                            break;
                        }
                        HandleStore(current, words, isUidPrefixed);
                        await Write($"{tag} OK STORE completed");
                    }
                    break;

                case "COPY":
                    {
                        if (current is null)
                        {
                            await Write($"{tag} NO not in selected folder");
                            break;
                        }
                        var destName = words[^1];
                        if (!ServerState.Folders.TryGetValue(destName, out var dest))
                        {
                            await Write($"{tag} NO [NONEXISTENT] destination does not exist");
                            break;
                        }
                        var mails = ResolveIds(current, words[1], isUidPrefixed);
                        foreach (var mail in mails)
                        {
                            dest.Add(mail.Data, mail.Uid, mail.Flags);
                            Console.WriteLine($"[server] copied uid {mail.Uid} -> {destName}");
                        }
                        await Write($"{tag} OK COPY completed");
                    }
                    break;

                case "EXPUNGE":
                    {
                        if (current is null)
                        {
                            await Write($"{tag} OK EXPUNGE completed");
                            break;
                        }
                        if (isUidPrefixed && words.Count > 1)
                        {
                            var toDelete = current.Mails
                                .Where(m => ParseIdList(words[1]).Contains(m.Uid))
                                .ToList();
                            foreach (var m in toDelete)
                            {
                                Console.WriteLine($"[server] expunged uid {m.Uid}");
                                current.Mails.Remove(m);
                            }
                        }
                        else
                        {
                            var before = current.Mails.Count;
                            current.Expunge();
                            Console.WriteLine($"[server] expunged {before - current.Mails.Count} message(s)");
                        }
                        await Write($"{tag} OK EXPUNGE completed");
                    }
                    break;

                case "FETCH":
                    await HandleFetch(command, tag, current, stream, writer);
                    continue;

                case "CLOSE":
                    current?.Expunge();
                    current = null;
                    await Write($"{tag} OK CLOSE completed");
                    break;

                case "LOGOUT":
                    await Write("* BYE Server logging out");
                    await Write($"{tag} OK LOGOUT completed");
                    return;

                default:
                    Console.WriteLine($"[warn] unhandled: {verb}");
                    await Write($"{tag} BAD unknown command");
                    break;
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[server] inner error:\n{ex}");
    }
}

static string ParentOf(string folderName)
{
    var slash = folderName.LastIndexOf('/');
    return slash < 0 ? "" : folderName[..slash];
}

static string Unquote(string token)
    => token.Length >= 2 && token[0] == '"' && token[^1] == '"' ? token[1..^1] : token;

static bool WildcardMatches(string pattern, string value)
{
    // RFC 3501 wildcards: '%' matches anything (incl. the hierarchy separator),
    // '*' matches anything except the separator.
    var regex = "^" + string.Concat(pattern.Select(c => c switch
    {
        '%' => ".*",
        '*' => "[^/]*",
        _ => System.Text.RegularExpressions.Regex.Escape(c.ToString())
    })) + "$";
    return System.Text.RegularExpressions.Regex.IsMatch(value, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}

static void HandleStore(Folder current, List<string> words, bool uidBased)
{
    // STORE <ids> +FLAGS (\Seen) | FLAGS (\Deleted) | -FLAGS (...)
    if (words.Count < 4)
        throw new Exception("bad STORE: " + string.Join(" ", words));

    var idsSpec = words[1];
    var op = words[2].ToUpperInvariant();
    var flags = words.Skip(3).Where(w => w.StartsWith("\\")).ToList();

    var mails = uidBased
        ? current.Mails.Where(m => ParseIdList(idsSpec).Contains(m.Uid)).ToList()
        : ParseSeqList(idsSpec).Where(i => i >= 1 && i <= current.Mails.Count).Select(i => current.Mails[i - 1]).ToList();

    foreach (var mail in mails)
    {
        switch (op)
        {
            case "+FLAGS":
                mail.Flags.UnionWith(flags);
                break;
            case "-FLAGS":
                mail.Flags.ExceptWith(flags);
                break;
            default:
                foreach (var f in new HashSet<string>(mail.Flags, StringComparer.OrdinalIgnoreCase))
                    if (!flags.Contains(f))
                        mail.Flags.Remove(f);
                mail.Flags.UnionWith(flags);
                break;
        }
        Console.WriteLine($"[server] STORE uid={mail.Uid} {op} [{string.Join(" ", flags)}] -> [{string.Join(" ", mail.Flags)}]");
    }
}

static List<uint> ParseIdList(string spec) =>
    ParseSeqList(spec).Select(x => (uint)x).ToList();

static List<int> ParseSeqList(string spec)
{
    var result = new List<int>();
    foreach (var part in spec.Split(','))
    {
        var p = part.Trim();
        if (p == "*") continue;
        var dash = p.IndexOf(':');
        if (dash >= 0)
        {
            if (int.TryParse(p[..dash], out var lo) && int.TryParse(p[(dash + 1)..], out var hi))
                result.AddRange(Enumerable.Range(Math.Min(lo, hi), Math.Abs(hi - lo) + 1));
        }
        else if (int.TryParse(p, out var n))
            result.Add(n);
    }
    return result;
}

static List<Mail> ResolveIds(Folder folder, string spec, bool uidBased)
{
    if (uidBased)
        return folder.Mails.Where(m => ParseIdList(spec).Contains(m.Uid)).ToList();
    return ParseSeqList(spec).Where(i => i >= 1 && i <= folder.Mails.Count).Select(i => folder.Mails[i - 1]).ToList();
}

// ---------------------------------------------------------------- SEARCH eval

static bool SearchEval(Mail mail, string[] tokens)
{
    var i = 0;
    return ParseOr(mail, tokens, ref i);
}

static bool ParseOr(Mail mail, string[] tokens, ref int i)
{
    var leadingOr = i < tokens.Length && tokens[i] == "OR";
    if (leadingOr)
        i++; // MailKit serializes Or as a flat list: "OR A B" == A OR B
    var left = ParseAnd(mail, tokens, ref i);
    if (leadingOr)
    {
        // flat OR list: everything that follows is another OR operand
        while (i < tokens.Length)
            left = ParseAnd(mail, tokens, ref i) || left;
    }
    else
    {
        while (i < tokens.Length && tokens[i] == "OR")
        {
            i++;
            left = ParseAnd(mail, tokens, ref i) || left;
        }
    }
    return left;
}

static bool ParseAnd(Mail mail, string[] tokens, ref int i)
{
    // MailKit always emits an explicit AND/OR keyword for multi-operand nodes,
    // so a bare following token belongs to the next operand of an outer list.
    var left = ParseNot(mail, tokens, ref i);
    while (i < tokens.Length && tokens[i] == "AND")
    {
        i++;
        left = ParseNot(mail, tokens, ref i) && left;
    }
    return left;
}

static bool ParseNot(Mail mail, string[] tokens, ref int i)
{
    if (i < tokens.Length && tokens[i] == "NOT")
    {
        i++;
        return !ParseNot(mail, tokens, ref i);
    }
    return ParsePrimary(mail, tokens, ref i);
}

static bool ParsePrimary(Mail mail, string[] tokens, ref int i)
{
    if (i >= tokens.Length)
        return false;
    var t = tokens[i].ToUpperInvariant();
    i++;
    switch (t)
    {
        case "ALL": return true;
        case "UNSEEN": return !mail.Flags.Contains("\\Seen");
        case "SEEN": return mail.Flags.Contains("\\Seen");
        case "DELETED": return mail.Flags.Contains("\\Deleted");
        case "ANSWERED": return mail.Flags.Contains("\\Answered");
        case "FLAGGED": return mail.Flags.Contains("\\Flagged");
        case "DRAFT": return mail.Flags.Contains("\\Draft");
        case "SINCE":
        case "SENTSINCE":
        case "SENTAFTER":
        case "BEFORE":
        case "SENTBEFORE":
        case "SENTEARLIER":
        case "ON":
        case "SENTEQUAL":
            {
                if (i >= tokens.Length)
                    return false;
                if (TryParseImapDate(tokens[i], out var date))
                {
                    i++;
                    var start = date.Date; // UTC midnight
                    return t switch
                    {
                        "SINCE" or "SENTSINCE" or "SENTAFTER" => mail.InternalDate >= start,
                        "BEFORE" or "SENTBEFORE" or "SENTEARLIER" => mail.InternalDate < start,
                        _ => mail.InternalDate >= start && mail.InternalDate < start.AddDays(1)
                    };
                }
                return false;
            }
        default:
            Console.WriteLine($"[warn] unsupported SEARCH keyword: {t}");
            return false;
    }
}

static bool TryParseImapDate(string token, out DateTime utcDate)
{
    token = token.Trim('"');
    if (DateTime.TryParseExact(token, "dd-MMM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d))
    {
        utcDate = d.ToUniversalTime();
        return true;
    }
    utcDate = default;
    return false;
}

// ------------------------------------------------------------------- tokenizer

static List<string> Tokenize(string command)
{
    var tokens = new List<string>();
    var i = 0;
    while (i < command.Length)
    {
        if (char.IsWhiteSpace(command[i]))
        {
            i++;
            continue;
        }
        if (command[i] == '(')
        {
            var end = command.IndexOf(')', i);
            var inner = command[(i + 1)..(end < 0 ? command.Length : end)].Trim();
            foreach (var t in inner.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                tokens.Add(t);
            i = end < 0 ? command.Length : end + 1;
            continue;
        }
        if (command[i] == '"')
        {
            var end = command.IndexOf('"', i + 1);
            if (end < 0) end = command.Length - 1;
            tokens.Add(command[(i + 1)..end]);
            i = end + 1;
            continue;
        }
        var start = i;
        while (i < command.Length && !char.IsWhiteSpace(command[i]) && command[i] != '(' && command[i] != '"')
            i++;
        tokens.Add(command[start..i]);
    }
    return tokens;
}

// ----------------------------------------------------------------------- FETCH

static async Task HandleFetch(string command, string tag, Folder? current, Stream stream, StreamWriter writer)
{
    async Task WriteRaw(string s) { Console.WriteLine("S| " + s.TrimEnd('\r', '\n')); writer.Write(s); await writer.FlushAsync(); }
    async Task WriteRawBytes(byte[] data) { await stream.WriteAsync(data); await writer.FlushAsync(); }

    if (current is null)
    {
        await WriteRaw($"{tag} NO FETCH in non-selected folder\r\n");
        return;
    }

    var isUid = command.StartsWith("UID ", StringComparison.OrdinalIgnoreCase);
    var rest = (isUid ? command[10..] : command[6..]).Trim();

    string rangeSpec, itemsSpec;
    var paren = rest.IndexOf('(');
    if (paren >= 0)
    {
        rangeSpec = rest[..paren].Trim();
        itemsSpec = rest[(paren + 1)..rest.LastIndexOf(')')].Trim();
    }
    else
    {
        var sp = rest.IndexOf(' ');
        rangeSpec = rest[..sp];
        itemsSpec = rest[(sp + 1)..].Trim();
    }
    var items = ParseItems(itemsSpec);
    var mails = ResolveIds(current, rangeSpec, isUid);

    foreach (var mail in mails)
    {
        var data = mail.Data;
        var headerEnd = FindHeaderEnd(data);
        var seq = current.Mails.IndexOf(mail) + 1;

        var scalarParts = new List<string> { $"UID {mail.Uid}" };
        scalarParts.Add(FormatFlags(mail));
        var dt = mail.InternalDate.ToUniversalTime();
        var dateStr = dt.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture).Replace(".", "");
        var timeStr = $"{dt.Hour:00}:{dt.Minute:00}:{dt.Second:00}";
        scalarParts.Add($"INTERNALDATE \"{dateStr} {timeStr} +0000\"");
        scalarParts.Add($"RFC822.SIZE {data.Length}");

        var literals = new List<(string Key, byte[] Payload)>();
        foreach (var item in items)
        {
            if (item == "ENVELOPE")
            {
                // ENVELOPE is an inline parenthesized list, not a literal
                scalarParts.Add(Encoding.ASCII.GetString(FormatEnvelope(data)));
                continue;
            }
            var (key, payload) = item switch
            {
                "RFC822" or "BODY[]" => ("BODY[]", data),
                _ when item.StartsWith("BODY[HEADER") => ("BODY[HEADER]", data[..headerEnd]),
                _ when item.StartsWith("BODY[TEXT") => ("BODY[TEXT]", data[headerEnd..]),
                _ => (null, null)
            };
            if (key is not null)
                literals.Add((key, payload!));
        }

        await WriteRaw($"* {seq} FETCH({string.Join(' ', scalarParts)}");
        foreach (var (key, payload) in literals)
        {
            await WriteRaw($" {key} {{{payload.Length}}}\r");
            await WriteRaw("\n");
            await WriteRawBytes(payload);
        }

        await WriteRaw(")\r\n");
    }

    await WriteRaw($"{tag} OK FETCH completed\r\n");
}

static string FormatFlags(Mail mail)
{
    if (mail.Flags.Count == 0)
        return "FLAGS ()";
    return "FLAGS (" + string.Join(' ', mail.Flags.Select(f => "\\" + f.TrimStart('\\')).OrderBy(f => f, StringComparer.Ordinal)) + ")";
}

static byte[] FormatEnvelope(byte[] data)
{
    var text = Encoding.ASCII.GetString(data, 0, Math.Min(data.Length, 8192));
    string GetHeader(string name)
    {
        var idx = text.IndexOf(name + ":", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return "";
        var end = text.IndexOf('\n', idx);
        return text[(idx + name.Length + 1)..(end < 0 ? text.Length : end)].Trim();
    }

    var subject = GetHeader("Subject");
    var messageId = GetHeader("Message-ID");
    var from = GetHeader("From");
    var to = GetHeader("To");
    var cc = GetHeader("Cc");

    string MailboxTuple(string headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
            return "(NIL NIL NIL NIL NIL)";
        var lt = headerValue.IndexOf('<');
        var gt = headerValue.IndexOf('>');
        string display = "NIL", local = "NIL", host = "NIL";
        if (lt >= 0 && gt > lt)
        {
            display = Quote(headerValue[..lt].Trim());
            var addr = headerValue[(lt + 1)..gt];
            var at = addr.LastIndexOf('@');
            if (at >= 0)
            {
                local = Quote(addr[..at]);
                host = Quote(addr[(at + 1)..]);
            }
            else
            {
                local = Quote(addr);
            }
        }
        else
        {
            var at = headerValue.LastIndexOf('@');
            if (at >= 0)
            {
                local = Quote(headerValue[..at]);
                host = Quote(headerValue[(at + 1)..]);
            }
            else
            {
                local = Quote(headerValue.Trim());
            }
        }
        return $"({display} NIL NIL {local} {host})";
    }

    string AddressList(string headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
            return "NIL";
        var addrs = headerValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return "(" + string.Join(" ", addrs.Select(MailboxTuple)) + ")";
    }

    var sb = new StringBuilder();
    sb.Append("ENVELOPE (");
    sb.Append(Quote(subject)).Append(' ');
    sb.Append(Quote(messageId)).Append(' ');
    sb.Append(AddressList(from)).Append(' ');
    sb.Append(AddressList(from)).Append(' '); // sender
    sb.Append(AddressList(to)).Append(' ');
    sb.Append(AddressList(cc)).Append(' ');
    sb.Append("NIL NIL ");
    sb.Append(Quote(messageId)).Append(' ');
    sb.Append(data.Length).Append(')');
    return Encoding.ASCII.GetBytes(sb.ToString());
}

static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

static List<string> ParseItems(string spec)
{
    var result = new List<string>();
    foreach (var raw in spec.Split(' ', StringSplitOptions.RemoveEmptyEntries))
    {
        var item = raw.ToUpperInvariant();
        if (item is "RFC822" or "RFC822.SIZE" or "UID" or "FLAGS" or "INTERNALDATE" or "ENVELOPE")
        {
            result.Add(item);
            continue;
        }
        if (item.StartsWith("BODY[") || item.StartsWith("BODY.PEEK["))
        {
            // BODY.PEEK[HEADER.FIELDS (FROM SUBJECT)] -> normalize
            var section = item[..(item.LastIndexOf(']') + 1)];
            var normalized = section
                .Replace("HEADER.FIELDS.NOT", "HEADER")
                .Replace("HEADER.FIELDS", "HEADER")
                .Replace("BODY.PEEK[", "BODY[");
            result.Add(normalized);
        }
    }
    return result;
}

static int FindHeaderEnd(byte[] data)
{
    for (int i = 3; i < data.Length - 3; i++)
    {
        if (data[i - 1] == (byte)'\r' && data[i] == (byte)'\n' && data[i + 1] == (byte)'\r' && data[i + 2] == (byte)'\n')
            return i + 1; // end of the blank line (inclusive of first CRLF)
    }
    return data.Length;
}

static bool TryParseLogin(string command, out string user, out string password)
{
    user = "";
    password = "";

    var i = command.IndexOf(' ');
    if (i < 0) return false;
    var rest = command[(i + 1)..].Trim();

    if (rest.Length == 0) return false;
    if (rest[0] == '"')
    {
        var e = rest.IndexOf('"', 1);
        if (e < 0) return false;
        user = rest[1..e];
        rest = rest[(e + 1)..].TrimStart();
    }
    else
    {
        var sp = rest.IndexOf(' ');
        if (sp < 0) return false;
        user = rest[..sp];
        rest = rest[(sp + 1)..].TrimStart();
    }

    if (rest.Length == 0) return false;
    if (rest[0] == '"')
    {
        var e = rest.IndexOf('"', 1);
        if (e < 0) return false;
        password = rest[1..e];
    }
    else
    {
        password = rest;
    }
    return true;
}

static AuthCheck? ParseAuth(string? spec)
{
    if (spec is null || !spec.Contains(':'))
        return null;
    var i = spec.IndexOf(':');
    return new AuthCheck(spec[..i], spec[(i + 1)..]);
}

// ---------------------------------------------------------------------- types

sealed class Mail
{
    public byte[] Data = Array.Empty<byte>();
    public HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase);
    public DateTime InternalDate;
    public uint Uid;
}

sealed class Folder
{
    public string Name;
    public List<Mail> Mails = new();
    public uint NextUid = 1;
    public Folder(string name) => Name = name;

    public void Add(byte[] data, uint? uid = null, HashSet<string>? flags = null)
    {
        var mail = new Mail
        {
            Data = data,
            InternalDate = ServerState.ParseDateHeader(data) ?? new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            Uid = uid ?? NextUid
        };
        NextUid = Math.Max(NextUid, mail.Uid + 1);
        if (flags is not null)
            mail.Flags.UnionWith(flags);
        Mails.Add(mail);
    }

    public void Expunge()
    {
        Mails.RemoveAll(m => m.Flags.Contains("\\Deleted"));
    }
}

static class ServerState
{
    public static readonly Dictionary<string, Folder> Folders = new(StringComparer.OrdinalIgnoreCase);

    public static DateTime? ParseDateHeader(byte[] data)
    {
        var text = Encoding.ASCII.GetString(data, 0, Math.Min(data.Length, 8192));
        var idx = text.IndexOf("Date:", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return null;
        var lineEnd = text.IndexOf('\n', idx);
        var line = text[idx..(lineEnd < 0 ? text.Length : lineEnd)];
        var value = line["Date:".Length..].Trim();
        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)
            ? dt.ToUniversalTime()
            : null;
    }
}

sealed class FolderNotFoundException(string name) : Exception($"NO [NONEXISTENT] folder {name} does not exist");

readonly record struct AuthCheck(string User, string Password);
