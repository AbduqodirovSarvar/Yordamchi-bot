using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using TL;
using WTelegram;

namespace YordamchiBot.Userbot;

/// <summary>
/// Core logic. OnUpdates only filters and enqueues (it must return fast); a single background consumer
/// downloads media, cleans text, waits a human-like delay and publishes NEW messages (never forwards).
/// </summary>
public sealed class UserbotMessageHandler(IOptionsMonitor<UserbotConfig> options, ILogger<UserbotMessageHandler> log)
{
    private const int MaxText = 4096;
    private const long ChannelIdBase = 1_000_000_000_000;
    private static readonly TimeSpan AlbumWindow = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan PeerRefreshCooldown = TimeSpan.FromMinutes(2);

    /// <summary>A single message or a whole album, plus the routes it matched.</summary>
    private sealed record Job(Message[] Messages, RouteConfig[] Routes);

    private readonly System.Threading.Channels.Channel<Job> _queue = System.Threading.Channels.Channel.CreateBounded<Job>(
        new BoundedChannelOptions(200) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    private readonly ConcurrentDictionary<long, List<Message>> _albums = new();
    private readonly Dictionary<long, InputPeer> _peers = [];
    private DateTime _lastPeerRefresh = DateTime.MinValue;

    // ---------------------------------------------------------------- Intake (called from Client.OnUpdates)

    /// <summary>Config uses the "-100xxxxxxxxxx" form, MTProto uses the bare channel id. Both map to the same value here.</summary>
    private static long Normalize(long id) => id <= -ChannelIdBase ? -id - ChannelIdBase : Math.Abs(id);

    public void OnUpdates(UpdatesBase updates)
    {
        try
        {
            foreach (var update in updates.UpdateList)
            {
                if (update is not UpdateNewChannelMessage { message: Message msg }) continue;
                if (msg.peer_id is not PeerChannel peer) continue;

                var routes = options.CurrentValue.Routes
                    .Where(r => r.SourceChannelIds.Any(s => Normalize(s) == peer.channel_id))
                    .ToArray();
                if (routes.Length == 0) continue;

                if (msg.grouped_id != 0) BufferAlbum(msg, routes);
                else Enqueue(new Job([msg], routes));
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "OnUpdates failed");   // never let an exception escape into the MTProto reactor
        }
    }

    private void BufferAlbum(Message msg, RouteConfig[] routes)
    {
        var isFirst = false;
        _albums.AddOrUpdate(
            msg.grouped_id,
            _ => { isFirst = true; return [msg]; },
            (_, list) => { lock (list) list.Add(msg); return list; });

        if (!isFirst) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(AlbumWindow);      // wait for the remaining album items
            if (!_albums.TryRemove(msg.grouped_id, out var list)) return;
            Message[] items;
            lock (list) items = [.. list.OrderBy(m => m.id)];
            Enqueue(new Job(items, routes));
        });
    }

    private void Enqueue(Job job)
    {
        if (!_queue.Writer.TryWrite(job))
            log.LogWarning("Queue is full, message dropped");
    }

    // ---------------------------------------------------------------- Consumer

    public async Task RunAsync(Client client, CancellationToken ct)
    {
        await RefreshPeersAsync(client, force: true);

        await foreach (var job in _queue.Reader.ReadAllAsync(ct))
        {
            try
            {
                await ProcessAsync(client, job, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // Network errors, deleted messages, expired file references, ... : log and keep serving.
                log.LogError(ex, "Job failed (message {Id})", job.Messages[0].id);
            }
        }
    }

    /// <summary>Everything downloaded from a source message, ready to be uploaded.</summary>
    private sealed record MediaItem(MemoryStream Data, string FileName, bool IsPhoto, string? Mime, DocumentAttribute[]? Attributes);

    private async Task ProcessAsync(Client client, Job job, CancellationToken ct)
    {
        var cfg = options.CurrentValue;
        var rawText = job.Messages.Select(m => m.message).FirstOrDefault(t => !string.IsNullOrEmpty(t));

        // 1) Download media once (MemoryStream), then upload once and reuse the uploaded file for every target.
        var media = new List<InputMedia>();
        var hadMedia = false;
        foreach (var m in job.Messages)
        {
            if (m.media is MessageMediaPhoto or MessageMediaDocument) hadMedia = true;
            var item = await DownloadAsync(client, m, cfg.MaxMediaMb);
            if (item is null) continue;
            using (item.Data)
            {
                item.Data.Position = 0;
                var file = await client.UploadFileAsync(item.Data, item.FileName);
                media.Add(item.IsPhoto
                    ? new InputMediaUploadedPhoto { file = file }
                    : new InputMediaUploadedDocument(file, item.Mime ?? "application/octet-stream") { attributes = item.Attributes });
            }
        }

        if (hadMedia && media.Count == 0)
        {
            log.LogWarning("Message {Id}: media skipped (too large or unsupported), nothing sent", job.Messages[0].id);
            return;
        }
        if (media.Count == 0 && string.IsNullOrWhiteSpace(rawText)) return;

        // 2) Publish per route / per target.
        foreach (var route in job.Routes)
        {
            var text = TextCleaner.Build(rawText, route.ReplacementText ?? "", media.Count > 0 ? cfg.CaptionLimit : MaxText,
                cfg.RemoveWords.Concat(route.RemoveWords));

            foreach (var targetId in route.TargetChannelIds)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var peer = await ResolvePeerAsync(client, targetId);
                    if (peer is null)
                    {
                        log.LogWarning("[{Route}] target {Target} not found among the account's chats (is it a member?)", route.Name, targetId);
                        continue;
                    }

                    // Human-like pause to stay under Telegram's anti-spam radar.
                    await Task.Delay(Random.Shared.Next(cfg.MinDelayMs, Math.Max(cfg.MinDelayMs, cfg.MaxDelayMs) + 1), ct);

                    await WithFloodWaitAsync(async () =>
                    {
                        if (media.Count == 0) await client.SendMessageAsync(peer, text, null, 0, null, default, Client.LinkPreview.Disabled);
                        else if (media.Count == 1) await client.SendMessageAsync(peer, text, media[0]);
                        else await client.SendAlbumAsync(peer, media, text);
                    }, ct);

                    log.LogInformation("[{Route}] sent to {Target}", route.Name, targetId);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    log.LogError(ex, "[{Route}] sending to {Target} failed", route.Name, targetId);
                }
            }
        }
    }

    private async Task<MediaItem?> DownloadAsync(Client client, Message m, int maxMb)
    {
        var limit = maxMb * 1024L * 1024L;
        try
        {
            switch (m.media)
            {
                case MessageMediaPhoto { photo: Photo photo }:
                {
                    var ms = new MemoryStream();
                    await client.DownloadFileAsync(photo, ms);
                    return new MediaItem(ms, $"{photo.id}.jpg", true, null, null);
                }
                case MessageMediaDocument { document: Document doc }:
                {
                    if (doc.size > limit)
                    {
                        log.LogWarning("Message {Id}: file is {Mb} MB, over the {Max} MB limit", m.id, doc.size / 1024 / 1024, maxMb);
                        return null;
                    }
                    var ms = new MemoryStream((int)Math.Min(doc.size, int.MaxValue));
                    await client.DownloadFileAsync(doc, ms);
                    var name = doc.Filename ?? $"{doc.id}.bin";
                    return new MediaItem(ms, name, false, doc.mime_type, doc.attributes);
                }
                default:
                    return null;       // text, web page previews, polls, ... are not copied as media
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Download of message {Id} media failed", m.id);   // e.g. message deleted meanwhile
            return null;
        }
    }

    // ---------------------------------------------------------------- Peers

    /// <summary>Looks up the InputPeer (id + access_hash) of a target; refreshes the chat list on a miss.</summary>
    private async Task<InputPeer?> ResolvePeerAsync(Client client, long configuredId)
    {
        var id = Normalize(configuredId);
        if (_peers.TryGetValue(id, out var peer)) return peer;
        await RefreshPeersAsync(client, force: false);
        return _peers.GetValueOrDefault(id);
    }

    private async Task RefreshPeersAsync(Client client, bool force)
    {
        if (!force && DateTime.UtcNow - _lastPeerRefresh < PeerRefreshCooldown) return;
        _lastPeerRefresh = DateTime.UtcNow;
        try
        {
            var chats = await client.Messages_GetAllChats();
            foreach (var (id, chat) in chats.chats)
                if (chat is TL.Channel ch && ch.IsActive) _peers[id] = ch.ToInputPeer();
            log.LogInformation("Chat list loaded ({Count} channels/groups)", _peers.Count);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Loading chat list failed");
        }
    }

    // ---------------------------------------------------------------- Flood control

    /// <summary>Runs <paramref name="send"/>; on FLOOD_WAIT_x sleeps x seconds (if reasonable) and retries once.</summary>
    private static async Task WithFloodWaitAsync(Func<Task> send, CancellationToken ct)
    {
        try
        {
            await send();
        }
        catch (RpcException ex) when (ex.Code == 420 && ex.X is > 0 and <= 300)
        {
            await Task.Delay(TimeSpan.FromSeconds(ex.X + 1), ct);
            await send();
        }
    }
}
