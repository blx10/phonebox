using System.Diagnostics;

namespace PhoneBox.Agent;

/// <summary>Configures child processes with a minimal OS/audio/toolchain environment allow-list so parent secrets cannot leak.</summary>
internal static class SpeechProcessEnv
{
    private static readonly string[] AllowedVariables =
    [
        "PATH",
        "HOME",
        "USERPROFILE",
        "TMPDIR",
        "TEMP",
        "TMP",
        "SYSTEMROOT",
        "WINDIR",
        "APPDATA",
        "LOCALAPPDATA",
        "LANG",
        "LC_ALL",
        "__CF_USER_TEXT_ENCODING",
        "DEVELOPER_DIR",
        "SDKROOT",
        "XDG_RUNTIME_DIR",
        "XDG_CACHE_HOME",
        "PULSE_SERVER",
        "PIPEWIRE_REMOTE",
        "ALSA_CONFIG_PATH",
    ];

    public static void ConfigureMinimal(ProcessStartInfo psi)
    {
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.Environment.Clear();

        foreach (var name in AllowedVariables)
        {
            var val = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(val))
                psi.Environment[name] = val;
        }
    }
}

/// <summary>Per-client sliding-window rate limiter for phone-triggered Speech actions.</summary>
internal sealed class SpeechRateLimiter
{
    private readonly int _maxRequests;
    private readonly TimeSpan _window;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _buckets = new(StringComparer.Ordinal);

    public SpeechRateLimiter(int maxRequests = 12, TimeSpan? window = null, Func<DateTimeOffset>? clock = null)
    {
        _maxRequests = Math.Max(1, maxRequests);
        _window = window ?? TimeSpan.FromSeconds(10);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public bool TryAcquire(string clientKey)
    {
        var now = _clock();
        var cutoff = now - _window;
        var key = string.IsNullOrEmpty(clientKey) ? "default" : clientKey;

        lock (_gate)
        {
            if (_buckets.Count > 128)
            {
                var stale = _buckets
                    .Where(kv => kv.Value.Count == 0 || kv.Value.Peek() < cutoff)
                    .Select(kv => kv.Key)
                    .Take(64)
                    .ToList();
                foreach (var k in stale)
                    _buckets.Remove(k);
            }

            if (!_buckets.TryGetValue(key, out var queue))
            {
                queue = new Queue<DateTimeOffset>();
                _buckets[key] = queue;
            }

            while (queue.Count > 0 && queue.Peek() <= cutoff)
                queue.Dequeue();

            if (queue.Count >= _maxRequests)
                return false;

            queue.Enqueue(now);
            return true;
        }
    }
}

/// <summary>Maps phone and local Settings endpoints for Speech to text with feature-enable and LAN device gating.</summary>
internal static class SpeechEndpoints
{
    private static IResult ToHttpResult(SpeechActionResult res) =>
        res.Ok
            ? Results.Json(new { ok = true, state = res.State })
            : Results.Json(new { ok = false, error = res.Error, state = res.State }, statusCode: res.StatusCode);

    private static string ClientKey(HttpContext ctx, bool isLan, string? deviceId)
    {
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "local";
        return isLan ? $"lan:{ip}:{deviceId ?? ""}" : $"local:{ip}";
    }

    private static IResult? AuthorizePhoneRead(
        HttpContext ctx,
        ConfigStore store,
        int localPort,
        out bool isLan,
        out string? deviceId)
    {
        var cfg = store.Get();
        isLan = ctx.Connection.LocalPort != localPort;
        deviceId = isLan ? ctx.Request.Headers["X-PhoneBox-Device"].FirstOrDefault() : null;

        if (!(cfg.Speech?.Enabled ?? false))
            return Results.NotFound();

        if (isLan)
        {
            var devicePages = cfg.PagesFor(deviceId).Where(p => p.Enabled).ToList();
            if (!SpeechService.HasSpeechWidget(devicePages))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        return null;
    }

    private static IResult? AuthorizePhoneAction(
        HttpContext ctx,
        ConfigStore store,
        SpeechService speech,
        int localPort,
        out bool isLan,
        out string? deviceId)
    {
        if (AuthorizePhoneRead(ctx, store, localPort, out isLan, out deviceId) is { } err)
            return err;

        if (!speech.TryConsumeActionSlot(ClientKey(ctx, isLan, deviceId)))
            return Results.Json(
                new { ok = false, error = "Too many speech requests. Please wait a moment.", state = (SpeechStateDto?)null },
                statusCode: StatusCodes.Status429TooManyRequests);

        return null;
    }

    public static void Map(IEndpointRouteBuilder app, int localPort)
    {
        app.MapGet("/api/speech/state", (HttpContext ctx, ConfigStore store, SpeechService speech) =>
        {
            if (AuthorizePhoneRead(ctx, store, localPort, out var isLan, out var deviceId) is { } err)
                return err;
            return Results.Json(speech.GetState(isLan, deviceId));
        });

        app.MapPost("/api/speech/start", async (HttpContext ctx, string? widgetId, ConfigStore store, SpeechService speech, CancellationToken ct) =>
        {
            if (AuthorizePhoneAction(ctx, store, speech, localPort, out var isLan, out var deviceId) is { } err)
                return err;
            return ToHttpResult(await speech.StartAsync(widgetId, isLan, deviceId, ct));
        });

        app.MapPost("/api/speech/stop", async (HttpContext ctx, string? widgetId, ConfigStore store, SpeechService speech, CancellationToken ct) =>
        {
            if (AuthorizePhoneAction(ctx, store, speech, localPort, out var isLan, out var deviceId) is { } err)
                return err;
            return ToHttpResult(await speech.StopAsync(widgetId, isLan, deviceId, ct));
        });

        app.MapPost("/api/speech/cancel", async (HttpContext ctx, string? widgetId, ConfigStore store, SpeechService speech, CancellationToken ct) =>
        {
            if (AuthorizePhoneAction(ctx, store, speech, localPort, out var isLan, out var deviceId) is { } err)
                return err;
            return ToHttpResult(await speech.CancelAsync(widgetId, isLan, deviceId, ct));
        });

        app.MapPost("/api/speech/append-note", (HttpContext ctx, string? entryId, string? widgetId, ConfigStore store, SpeechService speech) =>
        {
            if (AuthorizePhoneAction(ctx, store, speech, localPort, out var isLan, out var deviceId) is { } err)
                return err;
            return ToHttpResult(speech.AppendEntryToNote(entryId, widgetId, targetOverride: null, isLan, deviceId));
        });

        app.MapPost("/api/speech/clear", (HttpContext ctx, string? widgetId, ConfigStore store, SpeechService speech) =>
        {
            if (AuthorizePhoneAction(ctx, store, speech, localPort, out var isLan, out var deviceId) is { } err)
                return err;
            return ToHttpResult(speech.Clear(widgetId, isLan, deviceId));
        });

        // Settings › General › Speech to text (local-only: intentionally excluded from Lan.AllowedOverLan)
        app.MapGet("/api/speech/setup-status", async (SpeechService speech, CancellationToken ct) =>
            Results.Json(await speech.GetSetupStatusAsync(ct)));
        app.MapPost("/api/speech/browse-dirs", (SpeechBrowseRequest body, SpeechService speech) =>
            Results.Json(speech.BrowseDirectories(body.Path)));
        app.MapPost("/api/speech/test", async (SpeechTestRequest body, SpeechService speech, CancellationToken ct) =>
            Results.Json(await speech.TestProviderAsync(body.Config, ct)));
    }
}
