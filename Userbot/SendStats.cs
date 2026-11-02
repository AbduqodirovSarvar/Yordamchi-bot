using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace YordamchiBot.Userbot;

/// <summary>
/// Daily counters (local day): posts received per source channel, sent per target channel and skipped per route.
/// The last <see cref="KeepDays"/> days are kept in stats.json next to the session file (a mounted volume in Docker),
/// so the numbers survive restarts and redeploys.
/// </summary>
public sealed class SendStats : IDisposable
{
    private const int KeepDays = 30;
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(5);

    /// <summary>One day. Channel ids are in the configured "-100..." form; Skipped is keyed by RouteName.</summary>
    public sealed class Day
    {
        public Dictionary<long, int> Received { get; set; } = [];
        public Dictionary<long, int> Sent { get; set; } = [];
        public Dictionary<string, int> Skipped { get; set; } = [];
    }

    private readonly IOptionsMonitor<UserbotConfig> _options;
    private readonly ILogger<SendStats> _log;
    private readonly string _path;
    private readonly object _lock = new();
    private DateTime _lastSave = DateTime.MinValue;
    private bool _dirty;

    /// <summary>"2026-11-02" -> counters of that day.</summary>
    private readonly SortedDictionary<string, Day> _days;

    public SendStats(IOptionsMonitor<UserbotConfig> options, ILogger<SendStats> log)
    {
        _options = options;
        _log = log;
        var dir = Path.GetDirectoryName(Path.GetFullPath(options.CurrentValue.SessionPath)) ?? ".";
        _path = Path.Combine(dir, "stats.json");
        _days = Load();
    }

    private DateTime LocalNow => DateTime.UtcNow.AddHours(_options.CurrentValue.StatsUtcOffsetHours);

    private static string DayKey(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public void Received(long sourceId) => Count(d => d.Received, sourceId);
    public void Sent(long targetId) => Count(d => d.Sent, targetId);
    public void Skipped(string route) => Count(d => d.Skipped, route);

    private void Count<TKey>(Func<Day, Dictionary<TKey, int>> counters, TKey key) where TKey : notnull
    {
        lock (_lock)
        {
            var dayKey = DayKey(LocalNow);
            if (!_days.TryGetValue(dayKey, out var day)) _days[dayKey] = day = new Day();
            var map = counters(day);
            map[key] = map.GetValueOrDefault(key) + 1;
            while (_days.Count > KeepDays) _days.Remove(_days.Keys.First());

            // Bursts of incoming posts must not rewrite the file every time; at most one write per SaveInterval.
            _dirty = true;
            if (DateTime.UtcNow - _lastSave >= SaveInterval) Save();
        }
    }

    /// <summary>Report for the last <paramref name="days"/> days (newest first); <paramref name="title"/> gives a channel's display name.</summary>
    public string Report(int days, Func<long, string?> title)
    {
        days = Math.Clamp(days, 1, KeepDays);
        var today = LocalNow.Date;
        var sb = new StringBuilder("📊 Statistika\n");
        int received = 0, sent = 0, skipped = 0;

        void Section<TKey>(string header, Dictionary<TKey, int> map, Func<TKey, string> name) where TKey : notnull
        {
            if (map.Count == 0) return;
            sb.Append($"  {header}: {map.Values.Sum()}\n");
            foreach (var (key, n) in map.OrderByDescending(c => c.Value))
                sb.Append($"    • {name(key)}: {n}\n");
        }
        string Channel(long id) => title(id) ?? id.ToString(CultureInfo.InvariantCulture);

        lock (_lock)
        {
            if (_dirty) Save();
            for (var i = 0; i < days; i++)
            {
                var d = today.AddDays(-i);
                var label = i switch { 0 => "Bugun ", 1 => "Kecha ", _ => "" };
                sb.Append($"\n{label}{d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)}:\n");
                if (!_days.TryGetValue(DayKey(d), out var day) || day.Received.Count + day.Sent.Count + day.Skipped.Count == 0)
                {
                    sb.Append("  — hech narsa bo'lmagan\n");
                    continue;
                }
                Section("📥 Kelgan", day.Received, Channel);
                Section("📤 Yuborilgan", day.Sent, Channel);
                Section("⏭ O'tkazib yuborilgan", day.Skipped, r => r);
                received += day.Received.Values.Sum();
                sent += day.Sent.Values.Sum();
                skipped += day.Skipped.Values.Sum();
            }
        }
        if (days > 1) sb.Append($"\n{days} kunda jami: 📥 {received} · 📤 {sent} · ⏭ {skipped}");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Called by the host on shutdown (docker compose down / redeploy): writes the counts not saved yet.</summary>
    public void Dispose()
    {
        lock (_lock)
            if (_dirty) Save();
    }

    private SortedDictionary<string, Day> Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<SortedDictionary<string, Day>>(File.ReadAllText(_path)) ?? [];
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read {Path}, statistics start from zero", _path);
        }
        return [];
    }

    private void Save()
    {
        _lastSave = DateTime.UtcNow;
        _dirty = false;
        try
        {
            var tmp = _path + ".tmp";      // write-then-move, so a crash never leaves a half-written file
            File.WriteAllText(tmp, JsonSerializer.Serialize(_days));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not save {Path}", _path);
        }
    }
}
