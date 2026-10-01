using System.Text;
using System.Text.RegularExpressions;

namespace InboxDownloader;

/// <summary>Small helpers for the console (TUI) interface.</summary>
public static class Tui
{
    public static void Header(string title)
    {
        Console.WriteLine();
        Console.WriteLine("=== " + title.PadRight(60) + " ===");
    }

    public static void Rule() => Console.WriteLine(new string('-', 64));

    public static string Ask(string prompt, string? defaultValue = null)
    {
        var suffix = defaultValue is null ? "" : $" [{defaultValue}]";
        while (true)
        {
            Console.Write(prompt + suffix + ": ");
            var input = (Console.ReadLine() ?? "").Trim();
            if (input.Length == 0)
            {
                if (defaultValue is not null)
                    return defaultValue;
                continue;
            }

            return input;
        }
    }

    public static bool AskYesNo(string prompt, bool defaultYes = true)
    {
        var hint = defaultYes ? "Y/n" : "y/N";
        while (true)
        {
            Console.Write(prompt + $" [{hint}]: ");
            var input = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
            if (input.Length == 0) return defaultYes;
            if (input is "y" or "yes") return true;
            if (input is "n" or "no") return false;
        }
    }

    public static string AskRequired(string prompt, string? defaultValue = null)
    {
        var suffix = defaultValue is null ? "" : $" [{defaultValue}]";
        while (true)
        {
            Console.Write(prompt + suffix + ": ");
            var input = (Console.ReadLine() ?? "").Trim();
            if (input.Length > 0)
                return input;
            if (defaultValue is not null)
                return defaultValue;
            Console.Write("(value is required) ");
        }
    }

    public static int AskInt(string prompt, int defaultValue, int min, int max)
    {
        while (true)
        {
            Console.Write($"{prompt} [{defaultValue}]: ");
            var input = (Console.ReadLine() ?? "").Trim();
            if (input.Length == 0)
                return defaultValue;
            if (int.TryParse(input, out var value) && value >= min && value <= max)
                return value;
            Console.Write($"(enter a number between {min} and {max}) ");
        }
    }

    /// <summary>
    /// Reads input masked: each character is echoed as "*" (for passwords and other
    /// secrets), backspace removes the last one.
    /// </summary>
    public static string ReadPassword()
    {
        var sb = new StringBuilder();
        try
        {
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                    break;
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (sb.Length > 0 && Console.IsOutputRedirected == false)
                    {
                        sb.Remove(sb.Length - 1, 1);
                        if (Console.CursorLeft > 0)
                        {
                            Console.SetCursorPosition(Console.CursorLeft - 1, Console.CursorTop);
                            Console.Write(" ");
                            Console.SetCursorPosition(Console.CursorLeft - 1, Console.CursorTop);
                        }
                    }
                    continue;
                }
                if (!char.IsControl(key.KeyChar))
                {
                    sb.Append(key.KeyChar);
                    Console.Write("*");
                }
            }
        }
        catch (InvalidOperationException)
        {
            // Input redirected -> fall back to plain read
            return (Console.ReadLine() ?? "").Trim();
        }

        Console.WriteLine();
        return sb.ToString();
    }

    /// <summary>Builds a clean file name from a mail subject.</summary>
    public static string SanitizeFileName(string input)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = input.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var result = new string(chars).Trim();
        result = Regex.Replace(result, @"\s+", " ");
        return result.Length == 0 ? "message" : result;
    }
}
