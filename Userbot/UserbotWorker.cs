using Microsoft.Extensions.Options;
using WTelegram;

namespace YordamchiBot.Userbot;

/// <summary>
/// Owns the WTelegram client for the whole process lifetime: login (session is persisted),
/// update subscription and the processing loop. Transient failures restart the loop instead of killing the host.
/// </summary>
public sealed class UserbotWorker(IOptionsMonitor<UserbotConfig> options, UserbotMessageHandler handler, ILogger<UserbotWorker> log, ILoggerFactory loggerFactory)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Route WTelegramClient's own logging into Microsoft.Extensions.Logging.
        var wtLog = loggerFactory.CreateLogger("WTelegram");
        Helpers.Log = (level, text) => wtLog.Log((LogLevel)Math.Clamp(level, 0, 6), "{Text}", text);

        var cfg = options.CurrentValue;       // credentials are fixed for the process lifetime
        if (cfg.ApiId == 0 || string.IsNullOrWhiteSpace(cfg.ApiHash) || cfg.ApiHash == "YOUR_API_HASH")
            throw new InvalidOperationException("UserbotConfig:ApiId / ApiHash are not set in appsettings.json.");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var client = new Client(what => Config(what, cfg));
                var me = await Task.Run(() => client.LoginUserIfNeeded(), ct);
                log.LogInformation("Logged in as {Name} (id {Id})", me, me.id);

                client.OnUpdates += updates => { handler.OnUpdates(updates); return Task.CompletedTask; };

                await handler.RunAsync(client, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log.LogError(ex, "Client crashed, restarting in 15 s");
                try { await Task.Delay(TimeSpan.FromSeconds(15), ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>WTelegram asks for each setting by name; unknown ones return null (library default).</summary>
    private string? Config(string what, UserbotConfig cfg) => what switch
    {
        "api_id" => cfg.ApiId.ToString(),
        "api_hash" => cfg.ApiHash,
        "phone_number" => cfg.PhoneNumber,
        "session_pathname" => cfg.SessionPath,
        "verification_code" => Ask("Verification code sent by Telegram"),
        "password" => string.IsNullOrEmpty(cfg.Password) ? Ask("2FA password") : cfg.Password,
        _ => null
    };

    private string? Ask(string prompt)
    {
        // Only needed on the very first run (no session file yet): use `docker compose run --rm -it`.
        if (Console.IsInputRedirected)
            throw new InvalidOperationException($"{prompt} required, but no interactive terminal is attached. " +
                "Run the first login with: docker compose run --rm -it userbot");
        Console.Write($"{prompt}: ");
        return Console.ReadLine();
    }
}
