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

    /// <summary>Words / phrases removed from every post of every route (case-insensitive, whole words).</summary>
    public string[] RemoveWords { get; set; } = [];

    public List<RouteConfig> Routes { get; set; } = [];
}

/// <summary>One independent Source(s) -> Target(s) pipeline with its own footer text.</summary>
public sealed class RouteConfig
{
    public string Name { get; set; } = "";

    /// <summary>Channel IDs in the usual "-100..." form.</summary>
    public long[] SourceChannelIds { get; set; } = [];
    public long[] TargetChannelIds { get; set; } = [];

    /// <summary>Extra words removed only for this route (added to the global list).</summary>
    public string[] RemoveWords { get; set; } = [];

    /// <summary>Appended to the cleaned text (plain text).</summary>
    public string ReplacementText { get; set; } = "";
}
