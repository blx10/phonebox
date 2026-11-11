namespace PhoneBox.Agent;

/// <param name="Source">What the phone charges from: "usb", "ac", "wireless" or null (older app).</param>
/// <param name="Usb">The phone's own view of its USB link: "connected", "configured", "adb" (comma-separated), "none", or null.</param>
public sealed record PhoneBatteryReport(int Level, bool Plugged, double? Temp, long At, string? Source = null, string? Usb = null);

/// <summary>
/// Keeps the phone's battery between two levels (say 50 and 80 %) so a panel that is plugged in all the time does not
/// wear its battery out. Phones cannot stop charging on their own without root, so the power is switched where it comes
/// from: a Home Assistant smart plug (switch entity) or two shortcuts (for example uhubctl for a USB hub with switchable
/// ports, or a smart plug's command line). The phone reports its level every half minute (over USB or Wi-Fi).
/// </summary>
public sealed class ChargeGuard(ConfigStore config, HomeAssistant ha, PushStore push, AudioControl audio, ILogger<ChargeGuard> log) : BackgroundService
{
    /// <summary>A switch command is repeated at most this often if the phone's charging state does not follow.</summary>
    private const long RetryMs = 10 * 60_000;
    /// <summary>No report from the phone for this long while the power is off: switch it back on to be safe.</summary>
    private const long SilentMs = 10 * 60_000;

    private static PhoneBatteryReport? _report;
    private string? _last;      // "on" / "off"
    private long _lastAt;
    private string? _error;

    /// <summary>Called by the phone (POST /api/phone/battery).</summary>
    public static void Report(int level, bool plugged, double? temp, string? source = null, string? usb = null) =>
        _report = new PhoneBatteryReport(Math.Clamp(level, 0, 100), plugged, temp, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            string.IsNullOrWhiteSpace(source) ? null : source.Trim().ToLowerInvariant(), string.IsNullOrWhiteSpace(usb) ? null : usb.Trim().ToLowerInvariant());

    /// <summary>The phone's latest report (over USB or Wi-Fi), or null.</summary>
    public static PhoneBatteryReport? LastReport => _report;

    public object Status() => new
    {
        phone = _report,
        power = _last,
        switchedAt = _lastAt == 0 ? (long?)null : _lastAt,
        error = _error,
    };

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(5000, ct);
        while (!ct.IsCancellationRequested)
        {
            try { await Check(); }
            catch (Exception ex) { log.LogWarning("charge guard: {Msg}", ex.Message); }
            await Task.Delay(30_000, ct);
        }
    }

    /// <summary>What to do for a battery reading: "on", "off" or null (leave the power as it is).</summary>
    public static string? Decide(ChargeConfig c, PhoneBatteryReport? r, long now, string? last, long lastAt)
    {
        if (r is null || now - r.At > SilentMs) return last == "off" ? "on" : null; // lost the phone: never leave it unpowered
        var retry = now - lastAt > RetryMs;
        if (r.Level >= c.Max && r.Plugged && (last != "off" || retry)) return "off";
        if (r.Level <= c.Min && !r.Plugged && (last != "on" || retry)) return "on";
        return null;
    }

    private async Task Check()
    {
        var c = config.Get().Charge;
        if (!c.Enabled) { if (_last == "off") await Switch(c, "on"); return; } // turning the guard off restores the power
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (Decide(c, _report, now, _last, _lastAt) is { } action) await Switch(c, action);
    }

    public async Task<string?> Switch(ChargeConfig c, string action)
    {
        string? error;
        if (c.Method == "shortcuts")
        {
            var id = action == "on" ? c.OnShortcut : c.OffShortcut;
            error = string.IsNullOrWhiteSpace(id) ? $"no shortcut set for power {action}" : Shortcuts.Run(config.Get(), id, log, push, audio);
        }
        else error = string.IsNullOrWhiteSpace(c.Entity) ? "no Home Assistant switch set" : await ha.SwitchAsync(c.Entity, action == "on");
        // a failed "on" leaves the plug off: keep the old state so the next pass asks again (never leave the phone unpowered)
        if (error is null || action == "off") _last = action;
        _lastAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _error = error;
        if (error is null) log.LogInformation("charge guard: power {Action} (phone at {Level}%)", action, _report?.Level);
        else log.LogWarning("charge guard: power {Action} failed: {Error}", action, error);
        return error;
    }
}
