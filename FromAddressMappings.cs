using System.IO;
using System.Text;
using MailKit;
using MimeKit;

namespace InboxDownloader;

/// <summary>
/// One mapping line: a subfolder path and address criteria.
/// Addresses of the same field (all "from:" or all "to:") are alternatives
/// (ANY of them may match); a line that has both "from:" and "to:" criteria
/// requires BOTH to match (AND).
/// </summary>
public sealed class MappingRule
{
    public required string[] Segments { get; init; }
    public required string[] FromAddresses { get; init; }
    public required string[] ToAddresses { get; init; }

    public string SubfolderPath => string.Join("\\", Segments);
    public string CriteriaText =>
        string.Join(";", FromAddresses.Select(a => "from:" + a).Concat(ToAddresses.Select(a => "to:" + a)));
}

/// <summary>
/// Parses the mapping file that lives inside the target folder.
/// Format (INI-like, one mapping per line):
///   ; comment or # comment
///   to=belege@example.com;erin@example.com   (global default "to:" criterion: it is
///                                             applied to every mapping line that has
///                                             no "to:" criterion of its own; a line
///                                             with its own "to:" overrides it)
///   200000=office@example.com                (match on From, the default)
///   200000=post@example.com;office@example.com (multiple addresses: ANY matches)
///   200000\er=erin@example.com               (subfolders with \ or /)
///   300000\belege=to:belege@example.com      (match on To/Cc, overrides the global "to=")
///   200000=from:post@example.com;to:belege@example.com (from AND to must match)
/// Left side = subfolder path (relative to the target root) - or the keyword "to" for
/// the global default criterion. Right side = one or more ';'-separated addresses,
/// each optionally prefixed with "from:" or "to:" (default: "from:").
/// Addresses of the same field are alternatives (ANY); a line with both "from:" and
/// "to:" criteria requires BOTH (AND). At least one address is required.
/// </summary>
public class FromAddressMappings
{
    private readonly List<MappingRule> _rules = new();
    private readonly List<string> _globalTo = new();

    public string SourceFile { get; }
    public int EntryCount => _rules.Count;
    public bool HasGlobalConstraints => _globalTo.Count > 0;
    public string GlobalConstraintText => _globalTo.Count > 0 ? "to=" + string.Join(";", _globalTo) : "";

    private FromAddressMappings(string sourceFile) => SourceFile = sourceFile;

    /// <summary>Loads the mapping file from a local folder.</summary>
    public static FromAddressMappings Load(string targetPath, string mappingFileName)
    {
        var file = Path.Combine(targetPath, mappingFileName);
        return File.Exists(file)
            ? Parse(File.ReadAllText(file, Encoding.UTF8), file)
            : new FromAddressMappings(file); // no file -> everything goes to the unmapped location
    }

    /// <summary>Parses mapping file content (content may be null/empty).</summary>
    public static FromAddressMappings Parse(string? content, string sourceFile)
    {
        var mappings = new FromAddressMappings(sourceFile);
        if (string.IsNullOrWhiteSpace(content))
            return mappings;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#"))
                continue;

            // INI section headers are ignored
            if (line.StartsWith('[') && line.EndsWith(']'))
                continue;

            var eq = line.IndexOf('=');
            if (eq <= 0 || eq == line.Length - 1)
                continue;

            var subfolder = line[..eq].Trim().Trim('"');
            var criteriaSpec = line[(eq + 1)..].Trim().Trim('"');
            if (subfolder.Length == 0 || criteriaSpec.Length == 0)
                continue;

            // global default "to:" criterion line: "to=<addresses>"
            if (subfolder.Equals("to", StringComparison.OrdinalIgnoreCase))
            {
                var (gFrom, gTo) = ParseAddresses(criteriaSpec);
                mappings._globalTo.AddRange(gFrom);
                mappings._globalTo.AddRange(gTo);
                continue;
            }

            var segments = ParseSubfolderPath(subfolder);
            if (segments is null)
                continue; // invalid path (traversal, absolute path, invalid chars)

            var (fromAddrs, toAddrs) = ParseAddresses(criteriaSpec);
            if (fromAddrs.Count == 0 && toAddrs.Count == 0)
                continue; // no valid address -> ignore the line

            mappings._rules.Add(new MappingRule
            {
                Segments = segments,
                FromAddresses = fromAddrs.ToArray(),
                ToAddresses = toAddrs.ToArray()
            });
        }

        return mappings;
    }

    /// <summary>
    /// Splits a criteria specification into (fromAddresses, toAddresses).
    /// "from:addr" (or a bare "addr") goes to the from list, "to:addr" to the to list.
    /// </summary>
    private static (List<string> From, List<string> To) ParseAddresses(string criteriaSpec)
    {
        var from = new List<string>();
        var to = new List<string>();
        foreach (var raw in criteriaSpec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var address = raw.Trim().Trim('"');
            var isTo = false;
            if (address.StartsWith("to:", StringComparison.OrdinalIgnoreCase))
            {
                isTo = true;
                address = address["to:".Length..].Trim();
            }
            else if (address.StartsWith("from:", StringComparison.OrdinalIgnoreCase))
            {
                address = address["from:".Length..].Trim();
            }
            if (address.Length == 0 || !address.Contains('@'))
                continue;
            (isTo ? to : from).Add(address);
        }
        return (from, to);
    }

    /// <summary>
    /// Returns the subfolder path segment(s) a message maps to, or null if it matches
    /// nothing. A line matches when ALL its criterion groups match: the "from:" group
    /// (any of its addresses must equal the sender) and the "to:" group (any of its
    /// addresses must be in To/Cc). A line without a "to:" group uses the global
    /// "to=" criterion as its "to:" group (a line with its own "to:" overrides it).
    /// </summary>
    public IReadOnlyList<string[]>? GetSubfolders(MimeMessage message)
    {
        var result = new List<string[]>();
        var from = message.From is { Count: > 0 } fl ? GetAddress(fl[0]) : null;
        var recipients = GetRecipientAddresses(message);

        foreach (var rule in _rules)
        {
            if (RuleMatches(rule, from, recipients)
                && !result.Any(s => s.SequenceEqual(rule.Segments, StringComparer.OrdinalIgnoreCase)))
                result.Add(rule.Segments);
        }

        return result.Count > 0 ? result : null;
    }

    /// <summary>
    /// True if any mapping line matches the given envelope (including the global
    /// "to=" default criterion). Used to detect mails that are left in the source
    /// folder because they meet no criteria - deliberately independent of the read
    /// flag, because in a shared mailbox other users may have read the mail.
    /// </summary>
    public bool MatchesAny(Envelope? envelope)
    {
        if (envelope is null)
            return false;

        var from = envelope.From?.Mailboxes.FirstOrDefault()?.Address;
        var recipients = new List<string>();
        if (envelope.To is { } to)
            foreach (var mb in to.Mailboxes)
                recipients.Add(mb.Address);
        if (envelope.Cc is { } cc)
            foreach (var mb in cc.Mailboxes)
                recipients.Add(mb.Address);

        foreach (var rule in _rules)
            if (RuleMatches(rule, from, recipients))
                return true;

        return false;
    }

    private bool RuleMatches(MappingRule rule, string? from, IReadOnlyList<string> recipientAddresses)
    {
        if (rule.FromAddresses.Length > 0)
        {
            if (from is null || !rule.FromAddresses.Any(a => string.Equals(from, a, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        // the line's own "to:" criterion wins over the global "to=" constraint
        List<string> toAddresses = rule.ToAddresses.Length > 0 ? new(rule.ToAddresses) : _globalTo;
        if (toAddresses.Count > 0
            && !toAddresses.Any(a => recipientAddresses.Contains(a, StringComparer.OrdinalIgnoreCase)))
            return false;

        return true;
    }

    private static List<string> GetRecipientAddresses(MimeMessage message)
    {
        var addresses = new List<string>();
        if (message.To is { } to)
            for (var i = 0; i < to.Count; i++)
                if (GetAddress(to[i]) is { } a)
                    addresses.Add(a);
        if (message.Cc is { } cc)
            for (var i = 0; i < cc.Count; i++)
                if (GetAddress(cc[i]) is { } a)
                    addresses.Add(a);
        return addresses;
    }

    private static string? GetAddress(InternetAddress? address)
        => address is MailboxAddress mailbox ? mailbox.Address : null;

    /// <summary>All parsed rules (for display), in file order.</summary>
    public IReadOnlyList<MappingRule> AllMappings => _rules;

    /// <summary>
    /// Splits a subfolder value into validated path segments.
    /// Supports "\ " and "/" as separators; rejects ".", "..", absolute paths and
    /// characters that are invalid in file names.
    /// </summary>
    public static string[]? ParseSubfolderPath(string raw)
    {
        var segments = raw
            .Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

        if (segments.Length == 0)
            return null;

        var invalid = Path.GetInvalidFileNameChars();
        foreach (var segment in segments)
        {
            if (segment is "." or "..")
                return null;
            if (segment.Length > 80)
                return null;
            if (segment.Any(invalid.Contains))
                return null;
        }

        return segments;
    }

    /// <summary>
    /// Combines the target root with the subfolder segments into a full directory path.
    /// </summary>
    public static string CombineWithRoot(string targetRoot, params string[] segments)
    {
        return segments.Length == 0
            ? Path.GetFullPath(targetRoot)
            : Path.GetFullPath(Path.Combine(new[] { targetRoot }.Concat(segments).ToArray()));
    }
}
