using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace PhoneBox.Agent;

internal static class CodexBar
{
    private static readonly Dictionary<string, string> Names = new() { ["claude"] = "Claude", ["codex"] = "Codex", ["antigravity"] = "Antigravity" };

    public static async Task<IReadOnlyList<QuotaProvider>> ReadAsync(string path, CancellationToken ct)
    {
        var start = new ProcessStartInfo(path);
        start.ArgumentList.Add("usage"); start.ArgumentList.Add("--json");
        var result = await ToolProcess.RunAsync(start, TimeSpan.FromSeconds(25), ct);
        return Parse(result.Output, result.ExitCode);
    }

    public static IReadOnlyList<QuotaProvider> Parse(string json, int exitCode = 0)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("Expected a CodexBar provider array");
        var providers = new List<QuotaProvider>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var id = Str(item, "provider");
            if (id is null || !Names.TryGetValue(id, out var name)) continue;
            var usage = Prop(item, "usage");
            var error = Prop(item, "error");
            var message = error.ValueKind == JsonValueKind.String ? error.GetString() : Str(error, "message");
            if (error.ValueKind == JsonValueKind.Object && message is null) message = "CodexBar provider unavailable";
            var limits = new List<QuotaLimit>();
            var labels = Prop(item, "rateWindowLabels");
            void Add(JsonElement window, string label)
            {
                if (Prop(window, "isSyntheticPlaceholder").ValueKind == JsonValueKind.True || Num(window, "usedPercent") is not { } percent) return;
                var minutes = Num(window, "windowMinutes");
                var title = Str(labels, label) ?? (label is "primary" or "secondary" or "tertiary"
                    ? minutes switch { 300 => "5-hour session", 10080 => "Weekly", > 0 => string.Create(CultureInfo.InvariantCulture, $"{minutes / 60:0.#}-hour"), _ => label switch { "primary" => "Primary limit", "secondary" => "Secondary limit", _ => "Additional limit" } } : label);
                percent = Math.Max(0, percent);
                limits.Add(new QuotaLimit(title, percent, Date(Str(window, "resetsAt")),
                    minutes is > 0 and <= 525600 ? (long)(minutes.Value * 60) : null,
                    percent >= 95 ? "critical" : percent >= 80 ? "warning" : "normal"));
            }
            Add(Prop(usage, "primary"), "primary");
            Add(Prop(usage, "secondary"), "secondary");
            Add(Prop(usage, "tertiary"), "tertiary");
            var extra = Prop(usage, "extraRateWindows");
            if (extra.ValueKind == JsonValueKind.Array)
                foreach (var w in extra.EnumerateArray()) Add(Prop(w, "window"), Str(w, "title") ?? "Additional limit");
            var plan = Str(usage, "loginMethod") ?? Str(Prop(usage, "planObservation"), "rawPlan") ?? name;
            var credits = Num(Prop(item, "credits"), "remaining");
            providers.Add(new QuotaProvider(id, name, plan, limits, credits is { } c ? string.Create(CultureInfo.InvariantCulture, $"Credits: {c:0.##}") : null,
                Date(Str(usage, "updatedAt")) ?? Date(Str(item, "updatedAt")) ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                message ?? (limits.Count == 0 ? "CodexBar returned no measured limits" : null), Source: "codexbar"));
        }
        if (exitCode != 0 && providers.Count == 0) throw new IOException("CodexBar exited with code " + exitCode);
        return providers.GroupBy(p => p.Id).Select(g => g.First()).ToList();
    }

    // Cached limits are usable only while the provider's latest result is healthy and recent.
    internal static QuotaProvider? Select(QuotaProvider? local, QuotaProvider? external, long now)
    {
        if (external is { Error: null, Limits.Count: > 0, UpdatedAt: { } updated } && now - updated <= 600_000)
            return external with { Codex = local?.Codex };
        if (local is { Error: null, Limits.Count: > 0 }) return local;
        if (external is not null) return external with { Error = external.Error ?? "CodexBar data is stale", Codex = local?.Codex };
        return local;
    }

    private static JsonElement Prop(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var value) ? value : default;
    private static string? Str(JsonElement e, string key) => Prop(e, key) is var value && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static double? Num(JsonElement e, string key) => Prop(e, key) is var value && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;
    private static long? Date(string? value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.ToUnixTimeMilliseconds() : null;
}
