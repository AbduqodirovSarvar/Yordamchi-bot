using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace YordamchiBot;

/// <summary>
/// What to do with a post's text. A *Replacement text, when not empty, is put where the FIRST match was
/// found (later matches of the same kind are simply removed, so the replacement is not repeated).
/// </summary>
public sealed record CleanOptions(
    IEnumerable<string>? RemoveWords = null,
    bool Links = true, bool Usernames = true, bool Phones = true,
    string? LinkReplacement = null, string? UsernameReplacement = null, string? PhoneReplacement = null);

/// <summary>Strips contacts / words from a text and appends a footer.</summary>
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
    [GeneratedRegex("(\\d+)")] private static partial Regex PlaceholderRegex();

    // One regex per distinct word list (the list can change via hot reload).
    private static readonly ConcurrentDictionary<string, Regex> WordRegexCache = new();

    /// <summary>Case-insensitive, whole-word (or whole-phrase) removal; longer phrases are matched first.</summary>
    private static Regex? WordsRegex(IEnumerable<string>? words)
    {
        var list = words?.Select(w => w?.Trim()).Where(w => !string.IsNullOrEmpty(w))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(w => w!.Length).ToArray();
        if (list is not { Length: > 0 }) return null;

        return WordRegexCache.GetOrAdd(string.Join('\u0001', list), _ =>
            new Regex($@"(?<!\w)(?:{string.Join('|', list.Select(w => Regex.Escape(w!)))})(?!\w)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
    }

    private static bool IsPhone(Match m)
    {
        var digits = 0;
        foreach (var c in m.ValueSpan) if (char.IsAsciiDigit(c)) digits++;
        return digits is >= 9 and <= 15;
    }

    /// <summary>Removes links, @usernames, phone numbers and the configured words, and tidies leftover whitespace.</summary>
    public static string Clean(string? text, CleanOptions? o = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        o ??= new CleanOptions();

        // Replacement texts are parked behind placeholders so later steps (username / phone / word filters)
        // cannot touch them, and swapped in at the very end.
        var stash = new List<string>();
        MatchEvaluator Eval(string? replacement, Func<Match, bool>? isTarget = null)
        {
            var used = false;
            return m =>
            {
                if (isTarget is not null && !isTarget(m)) return m.Value;
                if (used || string.IsNullOrEmpty(replacement)) return "";
                used = true;
                stash.Add(replacement);
                return $"{stash.Count - 1}";
            };
        }

        if (o.Links) text = UrlRegex().Replace(text, Eval(o.LinkReplacement));        // first: URLs may contain digits and @
        if (o.Usernames) text = UsernameRegex().Replace(text, Eval(o.UsernameReplacement));
        if (o.Phones) text = PhoneRegex().Replace(text, Eval(o.PhoneReplacement, IsPhone));
        if (WordsRegex(o.RemoveWords) is { } wr) text = wr.Replace(text, "");

        text = MultiSpaceRegex().Replace(text, " ");
        text = TrailingSpaceRegex().Replace(text, "\n");
        text = ManyNewlinesRegex().Replace(text, "\n\n");
        text = text.Trim();

        return stash.Count == 0 ? text : PlaceholderRegex().Replace(text, m => stash[int.Parse(m.Groups[1].Value)]);
    }

    /// <summary>Cleans the text, appends <paramref name="footer"/> and fits the result into <paramref name="limit"/>.</summary>
    public static string Build(string? original, string footer, int limit, CleanOptions? options = null)
    {
        var body = Clean(original, options);
        if (body.Length == 0) return footer.TrimStart().Length <= limit ? footer.TrimStart() : footer[..limit];

        var room = limit - footer.Length;
        if (room < 1) return footer[..Math.Min(footer.Length, limit)];
        if (body.Length > room) body = body[..(room - 1)].TrimEnd() + "…";
        return body + footer;
    }
}
