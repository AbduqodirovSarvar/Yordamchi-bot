namespace YordamchiBot.Userbot;

/// <summary>
/// Bound to the "UserbotConfig" section of appsettings.json and consumed through IOptionsMonitor.
/// Routes and delays hot-reload; ApiId / ApiHash / PhoneNumber / Password / SessionPath are read once at startup.
/// </summary>
public sealed class UserbotConfig
{
    public const string SectionName = "UserbotConfig";

    /// <summary>Set false to run only the Bot API part.</summary>
    public bool Enabled { get; set; } = true;

    public int ApiId { get; set; }
    public string ApiHash { get; set; } = "";
    public string PhoneNumber { get; set; } = "";

    /// <summary>2FA cloud password, if the account has one (otherwise it is asked in the console).</summary>
    public string Password { get; set; } = "";

    /// <summary>Where the login session is stored. Keep it on a mounted volume in Docker.</summary>
    public string SessionPath { get; set; } = "WTelegram.session";

    /// <summary>Random pause before every outgoing message (anti-spam protection).</summary>
    public int MinDelayMs { get; set; } = 2000;
    public int MaxDelayMs { get; set; } = 5000;

    /// <summary>Media bigger than this is skipped, because it is buffered in RAM (MemoryStream).</summary>
    public int MaxMediaMb { get; set; } = 100;

    /// <summary>Caption limit: 1024 for normal accounts, 2048 for Telegram Premium.</summary>
    public int CaptionLimit { get; set; } = 1024;

    /// <summary>How many posts may wait in the queue (read once at startup). Queued items are tiny; media is downloaded only when its turn comes.</summary>
    public int QueueSize { get; set; } = 5000;

    public List<RouteConfig> Routes { get; set; } = [];
}

/// <summary>
/// One independent Source(s) -> Target(s) pipeline. What it does depends on the fields present:
/// <list type="bullet">
/// <item>only SourceChannelIds + TargetChannelIds: the post is re-published unchanged (not forwarded);</item>
/// <item>ReplacementText and/or RemoveWords (or an explicit Remove* flag = true): the text is edited.
/// Links, @usernames and phone numbers are then removed too, unless switched off with Remove* = false.</item>
/// </list>
/// </summary>
public sealed class RouteConfig
{
    public string RouteName { get; set; } = "";

    /// <summary>Channel IDs in the usual "-100..." form.</summary>
    public long[] SourceChannelIds { get; set; } = [];
    public long[] TargetChannelIds { get; set; } = [];

    /// <summary>Words / phrases removed from the text (case-insensitive, whole words). Optional.</summary>
    public string[] RemoveWords { get; set; } = [];

    /// <summary>
    /// Filter, optional. true = only posts that contain a photo are copied (an album counts if any item is a photo);
    /// text-only posts, videos and files are skipped. Does not make the route "editing".
    /// </summary>
    public bool OnlyWithPhoto { get; set; }

    /// <summary>Appended to the edited text (plain text). Optional.</summary>
    public string ReplacementText { get; set; } = "";

    /// <summary>Optional overrides; when omitted they default to true on an editing route and are ignored on a pass-through route.</summary>
    public bool? RemoveLinks { get; set; }
    public bool? RemoveUsernames { get; set; }
    public bool? RemovePhones { get; set; }

    /// <summary>
    /// Optional. Text put where the first link / @username / phone number was found (instead of just deleting it).
    /// Further matches of the same kind are removed, so the text is not repeated. Empty = just remove.
    /// </summary>
    public string LinkReplacement { get; set; } = "";
    public string UsernameReplacement { get; set; } = "";
    public string PhoneReplacement { get; set; } = "";

    /// <summary>True if this route changes the text in any way; false = copy the post exactly as it is.</summary>
    public bool Edits =>
        !string.IsNullOrEmpty(ReplacementText) || RemoveWords.Length > 0 ||
        !string.IsNullOrEmpty(LinkReplacement) || !string.IsNullOrEmpty(UsernameReplacement) || !string.IsNullOrEmpty(PhoneReplacement) ||
        RemoveLinks == true || RemoveUsernames == true || RemovePhones == true;

    public bool CleanLinks => Edits && (RemoveLinks ?? true);
    public bool CleanUsernames => Edits && (RemoveUsernames ?? true);
    public bool CleanPhones => Edits && (RemovePhones ?? true);

    /// <summary>The text-editing rules of this route, as understood by <see cref="TextCleaner"/>.</summary>
    public CleanOptions ToCleanOptions() =>
        new(RemoveWords, CleanLinks, CleanUsernames, CleanPhones, LinkReplacement, UsernameReplacement, PhoneReplacement);
}
