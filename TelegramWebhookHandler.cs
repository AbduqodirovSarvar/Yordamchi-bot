using System.Collections.Concurrent;
using System.Text;
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
public sealed class TelegramWebhookHandler(
    ITelegramBotClient bot,
    IOptionsMonitor<BotConfig> options,
    ILogger<TelegramWebhookHandler> log)
{
    private const int MaxCaption = 1024;   // Telegram limit for media captions
    private const int MaxText = 4096;      // Telegram limit for text messages

    // Telegram delivers each album item as a separate update; we collect them briefly, then send once.
    private static readonly TimeSpan AlbumWindow = TimeSpan.FromMilliseconds(1500);
    private readonly ConcurrentDictionary<string, List<Message>> _albums = new();

    private string BuildText(string? original, int limit) =>
        TextCleaner.Build(original, options.CurrentValue.ReplacementText ?? "", limit, options.CurrentValue.RemoveWords);

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
