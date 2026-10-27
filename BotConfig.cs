namespace YordamchiBot;

/// <summary>
/// Bound to the "BotConfig" section of appsettings.json.
/// Consumed through IOptionsMonitor so edits are picked up without a restart
/// (except BotToken / WebhookUrl, which are only used at startup).
/// </summary>
public sealed class BotConfig
{
    public const string SectionName = "BotConfig";

    /// <summary>Bot token from @BotFather (read once at startup).</summary>
    public string BotToken { get; set; } = "";

    /// <summary>Public HTTPS URL Telegram will POST updates to (registered at startup).</summary>
    public string WebhookUrl { get; set; } = "";

    /// <summary>
    /// Optional shared secret. If set, it is sent to Telegram as secret_token and every
    /// request to /webhook must carry it in X-Telegram-Bot-Api-Secret-Token. Strongly recommended.
    /// </summary>
    public string WebhookSecret { get; set; } = "";

    /// <summary>Channel whose posts are processed.</summary>
    public long SourceChannelId { get; set; }

    /// <summary>Optional: private chats with these user IDs are processed too.</summary>
    public long[] AdminIds { get; set; } = [];

    /// <summary>Channels the cleaned message is re-published to.</summary>
    public long[] TargetChannelIds { get; set; } = [];

    /// <summary>Text appended to the cleaned message (plain text, no parse mode).</summary>
    public string ReplacementText { get; set; } = "";
}
