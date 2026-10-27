using Microsoft.Extensions.Options;
using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using YordamchiBot;
using YordamchiBot.Userbot;

var builder = WebApplication.CreateBuilder(args);

// reloadOnChange is on by default for appsettings.json -> IOptionsMonitor sees edits instantly.
builder.Services.Configure<BotConfig>(builder.Configuration.GetSection(BotConfig.SectionName));
builder.Services.Configure<UserbotConfig>(builder.Configuration.GetSection(UserbotConfig.SectionName));

// Credentials are read once at startup (changing them requires a restart).
var botCfg = builder.Configuration.GetSection(BotConfig.SectionName).Get<BotConfig>() ?? new BotConfig();
var userbotCfg = builder.Configuration.GetSection(UserbotConfig.SectionName).Get<UserbotConfig>() ?? new UserbotConfig();

// ---- Part 1: Bot API (webhook) ----
if (botCfg.Enabled)
{
    if (string.IsNullOrWhiteSpace(botCfg.BotToken) || botCfg.BotToken == "YOUR_TOKEN")
        throw new InvalidOperationException("BotConfig:BotToken is not set in appsettings.json (or set BotConfig:Enabled=false).");

    builder.Services.AddSingleton<ITelegramBotClient>(_ => new TelegramBotClient(botCfg.BotToken));
    builder.Services.AddSingleton<TelegramWebhookHandler>();
}

// ---- Part 2: Userbot (MTProto, runs as a background service in the same process) ----
if (userbotCfg.Enabled)
{
    builder.Services.AddSingleton<UserbotMessageHandler>();
    builder.Services.AddHostedService<UserbotWorker>();
}

var app = builder.Build();

if (botCfg.Enabled)
{
    // ---- Register the webhook with Telegram on startup ----
    try
    {
        var bot = app.Services.GetRequiredService<ITelegramBotClient>();
        await bot.SetWebhook(
            url: botCfg.WebhookUrl,
            allowedUpdates: [UpdateType.Message, UpdateType.ChannelPost],
            secretToken: string.IsNullOrEmpty(botCfg.WebhookSecret) ? null : botCfg.WebhookSecret);
        app.Logger.LogInformation("Webhook set to {Url}", botCfg.WebhookUrl);
    }
    catch (Exception ex)
    {
        // Don't crash the container: the webhook may already be set, or DNS/TLS may not be ready yet.
        app.Logger.LogError(ex, "SetWebhook failed");
    }

    // ---- Webhook endpoint ----
    app.MapPost("/webhook", async (HttpRequest req, TelegramWebhookHandler handler, IOptionsMonitor<BotConfig> opt, CancellationToken ct) =>
    {
        var secret = opt.CurrentValue.WebhookSecret;
        if (!string.IsNullOrEmpty(secret) &&
            req.Headers["X-Telegram-Bot-Api-Secret-Token"] != secret)
            return Results.Unauthorized();

        Update? update;
        try
        {
            // Telegram.Bot 22.x needs its own serializer options (snake_case, enums, polymorphic types).
            update = await JsonSerializer.DeserializeAsync<Update>(req.Body, JsonBotAPI.Options, ct);
        }
        catch (JsonException)
        {
            return Results.BadRequest();
        }

        if (update is not null)
        {
            try { await handler.HandleAsync(update, ct); }
            catch (Exception ex) { app.Logger.LogError(ex, "Update {Id} failed", update.Id); }
        }

        // Always 200 so Telegram doesn't re-deliver (and duplicate) the post.
        return Results.Ok();
    });
}

app.MapGet("/healthz", () => Results.Ok());

app.Run();
