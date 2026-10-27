using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace YordamchiBot;

/// <summary>Shared by the Bot API handler and the userbot: strips contacts and appends a footer.</summary>
public static partial class TextCleaner
{
    // http(s)://..., www...., t.me/..., telegram.me/..., tg://...
    [GeneratedRegex(@"(?:https?://|www\.|tg://|(?:t|telegram)\.me/)\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();

    // @username (4+ chars); the lookbehind skips e-mail addresses.
    [GeneratedRegex(@"(?<![\w@])@[A-Za-z][A-Za-z0-9_]{3,31}\b", RegexOptions.CultureInvariant)]
    private static partial Regex UsernameRegex();

    // Digit runs with separators; kept only when they hold 9-15 digits (so prices / dates survive):
    // +998901234567 | 981234567 | 998 90 123-45-67 | (90) 123 45 67
    [GeneratedRegex(@"(?<![\w])\+?\(?\d[\d \t().\-]{6,}\d(?!\w)", RegexOptions.CultureInvariant)]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"[ \t]{2,}")] private static partial Regex MultiSpaceRegex();
    [GeneratedRegex(@"[ \t]+\n")] private static partial Regex TrailingSpaceRegex();
    [GeneratedRegex(@"\n{3,}")] private static partial Regex ManyNewlinesRegex();

    // One compiled-on-demand regex per distinct word list (the list can change via hot reload).
    private static readonly ConcurrentDictionary<string, Regex> WordRegexCache = new();

    /// <summary>Case-insensitive, whole-word (or whole-phrase) removal; longer phrases are matched first.</summary>
    private static Regex? WordsRegex(IEnumerable<string>? words)
    {
        var list = words?.Select(w => w?.Trim()).Where(w => !string.IsNullOrEmpty(w))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(w => w!.Length).ToArray();
        if (list is not { Length: > 0 }) return null;

        return WordRegexCache.GetOrAdd(string.Join('', list), _ =>
            new Regex($@"(?<!\w)(?:{string.Join('|', list.Select(w => Regex.Escape(w!)))})(?!\w)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
    }

    private static string RemovePhone(Match m)
    {
        var digits = 0;
        foreach (var c in m.ValueSpan) if (char.IsAsciiDigit(c)) digits++;
        return digits is >= 9 and <= 15 ? "" : m.Value;
    }

    /// <summary>Removes links, @usernames, phone numbers and the configured words, and tidies leftover whitespace.</summary>
    public static string Clean(string? text, IEnumerable<string>? removeWords = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        text = UrlRegex().Replace(text, "");          // first: URLs may contain digits and @
        text = UsernameRegex().Replace(text, "");
        text = PhoneRegex().Replace(text, RemovePhone);
        if (WordsRegex(removeWords) is { } wr) text = wr.Replace(text, "");
        text = MultiSpaceRegex().Replace(text, " ");
        text = TrailingSpaceRegex().Replace(text, "\n");
        text = ManyNewlinesRegex().Replace(text, "\n\n");
        return text.Trim();
    }

    /// <summary>Cleans the text, appends <paramref name="footer"/> and fits the result into <paramref name="limit"/>.</summary>
    public static string Build(string? original, string footer, int limit, IEnumerable<string>? removeWords = null)
    {
        var body = Clean(original, removeWords);
        if (body.Length == 0) return footer.TrimStart().Length <= limit ? footer.TrimStart() : footer[..limit];

        var room = limit - footer.Length;
        if (room < 1) return footer[..Math.Min(footer.Length, limit)];
        if (body.Length > room) body = body[..(room - 1)].TrimEnd() + "…";
        return body + footer;
    }
}
