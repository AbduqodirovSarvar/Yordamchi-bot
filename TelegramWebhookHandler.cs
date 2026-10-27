using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace YordamchiBot;

/// <summary>
/// Core logic: filter incoming updates, strip phones / usernames / links from the text,
/// append the configured footer and re-publish (never forward) to every target channel.
/// Registered as a singleton; holds only a tiny buffer for in-flight albums.
/// </summary>
public sealed partial class TelegramWebhookHandler(
    ITelegramBotClient bot,
    IOptionsMonitor<BotConfig> options,
    ILogger<TelegramWebhookHandler> log)
{
    private const int MaxCaption = 1024;   // Telegram limit for media captions
    private const int MaxText = 4096;      // Telegram limit for text messages

    // Telegram delivers each album item as a separate update; we collect them briefly, then send once.
    private static readonly TimeSpan AlbumWindow = TimeSpan.FromMilliseconds(1500);
    private readonly ConcurrentDictionary<string, List<Message>> _albums = new();

    // ---------------------------------------------------------------- Regex (source-generated, no startup cost)

    // http(s)://..., www...., t.me/..., telegram.me/..., tg://...
    [GeneratedRegex(@"(?:https?://|www\.|tg://|(?:t|telegram)\.me/)\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();

    // @username (5-32 chars per Telegram rules, relaxed to 4+); the lookbehind skips e-mail addresses.
    [GeneratedRegex(@"(?<![\w@])@[A-Za-z][A-Za-z0-9_]{3,31}\b", RegexOptions.CultureInvariant)]
    private static partial Regex UsernameRegex();

    // Any digit run with common separators ( + ( ) - . space ). The evaluator keeps it only if it has
    // 9-15 digits, so prices, dates and short numbers survive:
    // +998901234567 | 998 90 123-45-67 | (90) 123 45 67 | 90-123-45-67
    [GeneratedRegex(@"(?<![\w])\+?\(?\d[\d \t().\-]{6,}\d(?!\w)", RegexOptions.CultureInvariant)]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(@"[ \t]+\n")]
    private static partial Regex TrailingSpaceRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ManyNewlinesRegex();

    private static string RemovePhone(Match m)
    {
        var digits = 0;
        foreach (var c in m.ValueSpan) if (char.IsAsciiDigit(c)) digits++;
        return digits is >= 9 and <= 15 ? "" : m.Value;
    }

    /// <summary>Removes links, @usernames and phone numbers and tidies the leftover whitespace.</summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        text = UrlRegex().Replace(text, "");          // first: URLs may contain digits and @
        text = UsernameRegex().Replace(text, "");
        text = PhoneRegex().Replace(text, RemovePhone);

        text = MultiSpaceRegex().Replace(text, " ");
        text = TrailingSpaceRegex().Replace(text, "\n");
        text = ManyNewlinesRegex().Replace(text, "\n\n");
        return text.Trim();
    }

    /// <summary>Cleans <paramref name="original"/>, appends the footer and fits the result into <paramref name="limit"/>.</summary>
    private string BuildText(string? original, int limit)
    {
        var footer = options.CurrentValue.ReplacementText ?? "";
        var body = Clean(original);

        if (body.Length == 0) return footer.TrimStart().Length <= limit ? footer.TrimStart() : footer[..limit];

        var room = limit - footer.Length;
        if (room < 1) return footer[..Math.Min(footer.Length, limit)];
        if (body.Length > room) body = body[..(room - 1)].TrimEnd() + "…";
        return body + footer;
    }

    // ---------------------------------------------------------------- Update entry point

    public async Task HandleAsync(Update update, CancellationToken ct)
    {
        var msg = update.ChannelPost ?? update.Message;
        if (msg is null || !IsAuthorized(msg)) return;

        if (msg.MediaGroupId is { } groupId)
        {
            Buffer(groupId, msg);
            return;
        }

        await SendSingleAsync(msg, ct);
    }

    private bool IsAuthorized(Message msg)
    {
        var cfg = options.CurrentValue;
        if (msg.Chat.Id == cfg.SourceChannelId) return true;
        return msg.Chat.Type == ChatType.Private
               && msg.From is { } from
               && Array.IndexOf(cfg.AdminIds, from.Id) >= 0;
    }

    // ---------------------------------------------------------------- Single messages

    private async Task SendSingleAsync(Message msg, CancellationToken ct)
    {
        if (msg.Photo is { Length: > 0 } photos)
        {
            var fileId = photos[^1].FileId;                       // largest size
            var caption = BuildText(msg.Caption, MaxCaption);
            await ToAllTargetsAsync(chat => bot.SendPhoto(chat, InputFile.FromFileId(fileId), caption: caption, cancellationToken: ct), ct);
        }
        else if (msg.Video is { } video)
        {
            var caption = BuildText(msg.Caption, MaxCaption);
            await ToAllTargetsAsync(chat => bot.SendVideo(chat, InputFile.FromFileId(video.FileId), caption: caption, supportsStreaming: true, cancellationToken: ct), ct);
        }
        else if (!string.IsNullOrEmpty(msg.Text))
        {
            var text = BuildText(msg.Text, MaxText);
            var noPreview = new LinkPreviewOptions { IsDisabled = true };
            await ToAllTargetsAsync(chat => bot.SendMessage(chat, text, linkPreviewOptions: noPreview, cancellationToken: ct), ct);
        }
        else
        {
            log.LogDebug("Skipping unsupported message type in chat {Chat}", msg.Chat.Id);
        }
    }

    // ---------------------------------------------------------------- Albums (MediaGroup)

    private void Buffer(string groupId, Message msg)
    {
        var isFirst = false;
        _albums.AddOrUpdate(
            groupId,
            _ => { isFirst = true; return [msg]; },
            (_, list) => { lock (list) list.Add(msg); return list; });

        // The first item schedules the flush; the request's own token is cancelled once we answer 200.
        if (isFirst) _ = FlushAlbumAsync(groupId);
    }

    private async Task FlushAlbumAsync(string groupId)
    {
        try
        {
            await Task.Delay(AlbumWindow);
            if (!_albums.TryRemove(groupId, out var list)) return;

            Message[] items;
            lock (list) items = [.. list.OrderBy(m => m.MessageId)];

            var caption = BuildText(items.Select(m => m.Caption).FirstOrDefault(c => !string.IsNullOrEmpty(c)), MaxCaption);

            var media = new List<IAlbumInputMedia>(items.Length);
            foreach (var m in items)
            {
                var first = media.Count == 0;
                if (m.Photo is { Length: > 0 } p)
                    media.Add(new InputMediaPhoto(InputFile.FromFileId(p[^1].FileId)) { Caption = first ? caption : null });
                else if (m.Video is { } v)
                    media.Add(new InputMediaVideo(InputFile.FromFileId(v.FileId)) { Caption = first ? caption : null, SupportsStreaming = true });
            }
            if (media.Count == 0) return;

            await ToAllTargetsAsync(chat => bot.SendMediaGroup(chat, media), CancellationToken.None);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to flush album {Group}", groupId);
        }
    }

    // ---------------------------------------------------------------- Delivery

    /// <summary>Runs <paramref name="send"/> for each target sequentially; one failing channel never blocks the others.</summary>
    private async Task ToAllTargetsAsync(Func<long, Task> send, CancellationToken ct)
    {
        foreach (var target in options.CurrentValue.TargetChannelIds)
        {
            try
            {
                await SendWithRetryAsync(send, target, ct);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Send to {Target} failed", target);
            }
        }
    }

    private static async Task SendWithRetryAsync(Func<long, Task> send, long target, CancellationToken ct)
    {
        try
        {
            await send(target);
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 429 && ex.Parameters?.RetryAfter is { } wait and <= 30)
        {
            await Task.Delay(TimeSpan.FromSeconds(wait + 1), ct);
            await send(target);
        }
    }
}
