namespace PhoneBox.Agent;

public sealed partial class ControlDeck
{
    private readonly Dictionary<string, Dictionary<string, (int Original, int Applied)>> _fades = [];

    internal static bool PlayerSessionMatches(string player, string application)
    {
        static string Clean(string text) => Path.GetFileNameWithoutExtension(text.Split('!')[0].Split('_')[0]).ToLowerInvariant();
        var name = Clean(application); return name.Length > 2 && Clean(player) == name;
    }

    private void RestoreFade(string player)
    {
        if (!_fades.Remove(player, out var saved)) return;
        var sessions = _native.Audio().Sessions;
        foreach (var (id, level) in saved)
            if (level.Original >= 0 && sessions.FirstOrDefault(s => s.Id == id)?.Volume == level.Applied)
                _native.Session(id, level.Original, null);
        Invalidate();
    }

    internal async Task TickSleep(CancellationToken ct)
    {
        KeyValuePair<string, long>[] timers;
        lock (_state) timers = _sleep.ToArray();
        foreach (var (player, deadline) in timers)
        {
            var remaining = deadline - _now();
            if (remaining <= 30_000)
            {
                lock (_state)
                {
                    if (!_sleep.TryGetValue(player, out var current) || current != deadline) continue;
                    if (!_fades.TryGetValue(player, out var saved)) _fades[player] = saved = [];
                    foreach (var session in _native.Audio().Sessions.Where(s => PlayerSessionMatches(player, s.Name)))
                    {
                        if (_duck.Remove(session.Id, out var ducked) && session.Volume == ducked.Applied)
                        {
                            if (_native.Session(session.Id, ducked.Original, null) && !saved.ContainsKey(session.Id))
                                saved[session.Id] = (ducked.Original, ducked.Original);
                            // Read back on the next tick before applying the fade.
                            continue;
                        }
                        if (saved.TryGetValue(session.Id, out var level) && session.Volume != level.Applied)
                        {
                            // A manual volume change takes ownership back from the timer.
                            saved[session.Id] = (-1, session.Volume); continue;
                        }
                        if (!saved.ContainsKey(session.Id)) level = (session.Volume, session.Volume);
                        if (level.Original < 0) continue;
                        var volume = (int)Math.Round(level.Original * Math.Clamp(remaining / 30_000d, 0, 1));
                        if (_native.Session(session.Id, volume, null)) saved[session.Id] = (level.Original, volume);
                    }
                    Invalidate();
                }
            }
            if (remaining <= 0)
            {
                try { await _native.MediaAction(player, "pause", 0, ct); }
                finally { lock (_state) { if (_sleep.GetValueOrDefault(player) == deadline) { _sleep.Remove(player); RestoreFade(player); } } }
            }
        }
    }
}
