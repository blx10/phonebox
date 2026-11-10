using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

public sealed record QuotaPace(
    double? DeltaPercent,
    double? ExpectedUsedPercent,
    string? Stage,
    string? Summary,
    bool? WillLastToReset,
    long? EtaSeconds);

public sealed record CodexUsage(string? Model, string? Effort, long? TotalInput, long? TotalCached,
    long? TotalOutput, long? TotalReasoning, long? TotalTokens, long? LastInput, long? LastOutput,
    long? ContextWindow, long UpdatedAt);

public sealed record QuotaLimit(
    string Label,
    double Percent,
    long? ResetsAt,
    long? WindowSec,
    string Severity,
    string? ResetDescription = null,
    QuotaPace? Pace = null);

public sealed record QuotaModelCost(string ModelName, double Cost, long TotalTokens);

public sealed record QuotaDayCost(string Date, double TotalCost, long TotalTokens, IReadOnlyList<QuotaModelCost> Models);

public sealed record QuotaProjectCost(string Name, string? Path, double TotalCost, long TotalTokens);

public sealed record QuotaCostSummary(
    double TodayCostUsd,
    long TodayTokens,
    double? WindowCostUsd,
    long? WindowTokens,
    double Last30DaysCostUsd,
    long Last30DaysTokens,
    int HistoryDays,
    string? LatestDayDate,
    double? LatestDayCostUsd,
    long? LatestDayTokens,
    IReadOnlyList<QuotaModelCost> LatestDayModels,
    IReadOnlyList<QuotaModelCost> TopModels30d,
    IReadOnlyList<QuotaDayCost> Daily,
    IReadOnlyList<QuotaProjectCost> TopProjects);

public sealed record QuotaProvider(
    string Id,
    string Name,
    string Plan,
    IReadOnlyList<QuotaLimit> Limits,
    string? Extra,
    long? UpdatedAt,
    string? Error,
    string? AccountEmail = null,
    string Source = "local",
    QuotaCostSummary? Cost = null,
    CodexUsage? Codex = null);

public sealed record QuotaSubscriptionItem(
    string Id,
    string Name,
    string Plan,
    long Tokens30d,
    double Cost30dUsd,
    double? PrimaryUsedPercent,
    double? PrimaryRemainingPercent,
    string? PrimaryLabel);

public sealed record QuotaTopModelItem(
    string Name,
    string ProviderId,
    string ProviderName,
    double Cost30dUsd,
    long Tokens30d,
    string? SubModels);

public sealed record QuotaOverview(
    long TotalTokens30d,
    double TotalCost30dUsd,
    int HistoryDays,
    int ConnectedCount,
    long UpdatedAt,
    IReadOnlyList<QuotaSubscriptionItem> Subscriptions,
    IReadOnlyList<QuotaTopModelItem> TopModels);

public sealed record ConfiguredProviderInfo(string Id, string Name, bool Enabled);

/// <summary>
/// Collects quota usage and local 30-day token/cost statistics of AI subscriptions:
///  - CodexBar CLI (`codexbar usage --json-only`, `codexbar cost --json-only`, `codexbar config providers --json-only`)
///    plus `~/.config/codexbar/config.json` for instant discovery of all enabled providers.
///  - Fallback Claude: api.anthropic.com/api/oauth/usage with the OAuth token from ~/.claude/.credentials.json.
///  - Fallback Codex: the last "rate_limits" record in the newest rollout file under ~/.codex/sessions.
///  - Antigravity: a running local server, or the signed-in agy CLI's structured /usage command.
/// </summary>
/// </summary>
public sealed partial class QuotaMonitor : BackgroundService
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly Dictionary<string, string> KnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["codex"] = "Codex",
        ["claude"] = "Claude",
        ["antigravity"] = "Antigravity",
        ["alibabatokenplan"] = "Alibaba Token Plan",
        ["alibaba-token-plan"] = "Alibaba Token Plan",
        ["alibaba"] = "Alibaba",
        ["alibaba-coding-plan"] = "Alibaba Coding Plan",
        ["cursor"] = "Cursor",
        ["openai"] = "OpenAI",
        ["azureopenai"] = "Azure OpenAI",
        ["azure-openai"] = "Azure OpenAI",
        ["clinepass"] = "ClinePass",
        ["opencode"] = "OpenCode",
        ["opencodego"] = "OpenCode Go",
        ["qwencloud"] = "Qwen Cloud",
        ["qwen-cloud"] = "Qwen Cloud",
        ["factory"] = "Droid",
        ["fireworks"] = "Fireworks",
        ["gemini"] = "Gemini",
        ["copilot"] = "Copilot",
        ["devin"] = "Devin",
        ["zai"] = "z.ai / GLM",
        ["minimax"] = "MiniMax",
        ["manus"] = "Manus",
        ["kimi"] = "Kimi Code",
        ["kilo"] = "Kilo",
        ["kiro"] = "Kiro",
        ["vertexai"] = "Vertex AI",
        ["augment"] = "Augment",
        ["jetbrains"] = "JetBrains AI",
        ["moonshot"] = "Moonshot / Kimi Open Platform",
        ["amp"] = "Amp",
        ["t3chat"] = "T3 Chat",
        ["langdock"] = "Langdock",
        ["ollama"] = "Ollama",
        ["synthetic"] = "Synthetic",
        ["openrouter"] = "OpenRouter",
        ["elevenlabs"] = "ElevenLabs",
        ["warp"] = "Warp",
        ["windsurf"] = "Windsurf",
        ["zed"] = "Zed",
        ["perplexity"] = "Perplexity",
        ["mimo"] = "Xiaomi MiMo",
        ["doubao"] = "Doubao",
        ["sakana"] = "Sakana AI",
        ["abacus"] = "Abacus AI",
        ["abacusai"] = "Abacus AI",
        ["mistral"] = "Mistral",
        ["deepseek"] = "DeepSeek",
        ["deepinfra"] = "DeepInfra",
        ["codebuff"] = "Codebuff",
        ["venice"] = "Venice",
        ["commandcode"] = "Command Code",
        ["qoder"] = "Qoder",
        ["stepfun"] = "StepFun",
        ["bedrock"] = "AWS Bedrock",
        ["grok"] = "Grok",
        ["groq"] = "Groq",
        ["groqcloud"] = "Groq",
        ["llmproxy"] = "LLM Proxy",
        ["litellm"] = "LiteLLM",
        ["bifrost"] = "Bifrost",
        ["aixy"] = "Aixy",
        ["deepgram"] = "Deepgram",
        ["soniox"] = "Soniox",
        ["poe"] = "Poe",
        ["chutes"] = "Chutes",
        ["neuralwatt"] = "Neuralwatt",
        ["helmcode"] = "Helmcode",
        ["clawrouter"] = "ClawRouter",
        ["longcat"] = "LongCat",
        ["sub2api"] = "sub2api",
        ["wayfinder"] = "Wayfinder",
        ["zenmux"] = "ZenMux",
        ["aiand"] = "ai&",
        ["zoommate"] = "ZoomMate",
        ["xai"] = "xAI",
        ["xapi"] = "X API",
        ["notion"] = "Notion AI",
        ["ibmbob"] = "IBM Bob",
        ["nous"] = "Nous Portal",
        ["muse"] = "Muse Code",
        ["coderabbit"] = "CodeRabbit",
        ["replicate"] = "Replicate",
        ["huggingface"] = "Hugging Face",
        ["raycast"] = "Raycast",
        ["pi"] = "Pi",
        ["v0"] = "v0",
        ["typesafe"] = "TypeSafe",
        ["hyper"] = "Charm Hyper",
        ["gitkraken"] = "GitKraken AI",
        ["futureagi"] = "Future AGI",
        ["devpass"] = "DevPass",
        ["atlascloud"] = "Atlas Cloud",
        ["vercel"] = "Vercel AI Gateway",
        ["llmman"] = "llmman",
        ["xkiro"] = "xKiro",
        ["museai"] = "Muse (muse.ai)",
        ["lithosai"] = "LithosAI",
        ["workbuddy"] = "WorkBuddy",
        ["tavily"] = "Tavily",
        ["linkup"] = "Linkup",
        ["tinyapi"] = "TinyApi",
        ["exa"] = "Exa",
        ["cosmic"] = "Cosmic AI",
        ["aerostack"] = "Aerostack",
        ["sailresearch"] = "Sail Research",
        ["sofya"] = "Sofya",
        ["fal"] = "fal",
        ["flora"] = "FLORA",
        ["parallel"] = "Parallel",
        ["claudeplatform"] = "Claude Platform",
        ["munito"] = "Munito",
        ["traecn"] = "Trae CN",
        ["chatbox"] = "Chatbox AI",
        ["abliteration"] = "abliteration.ai",
        ["opendesign"] = "OpenDesign",
        ["siliconflow"] = "SiliconFlow",
        ["tripo"] = "Tripo",
    };

    private readonly ILogger<QuotaMonitor> _log;
    private readonly ILogger<QuotaMonitor> log;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly ConcurrentDictionary<string, QuotaProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private volatile IReadOnlyList<ConfiguredProviderInfo> _configured = [];
    private volatile QuotaOverview? _overview;
    private volatile IReadOnlyList<QuotaProvider> _external = [];
    private volatile QuotaProvider? _antigravity;
    private long _nextClaude;
    private long _nextCodexBar;
    private long _nextAntigravity;

    public QuotaMonitor(ILogger<QuotaMonitor> log)
    {
        _log = log;
        this.log = log;
        LoadInitialConfigProviders();
    }

    public IReadOnlyList<QuotaProvider> Snapshot()
    {
        var order = _configured.Select((c, idx) => (c.Id, idx)).ToDictionary(x => x.Id, x => x.idx, StringComparer.OrdinalIgnoreCase);
        return _providers.Values
            .OrderBy(p => order.TryGetValue(p.Id, out var i) ? i : 999)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<ConfiguredProviderInfo> ConfiguredProviders() => _configured;

    public QuotaOverview? Overview() => _overview;

    public static string DisplayNameFor(string id, string? customName = null)
    {
        if (!string.IsNullOrWhiteSpace(customName)) return customName.Trim();
        if (KnownNames.TryGetValue(id, out var known)) return known;
        return string.IsNullOrEmpty(id) ? "Provider" : char.ToUpperInvariant(id[0]) + id[1..];
    }

    private static readonly HashSet<string> AliasIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "alibaba-token-plan", "alibaba-coding-plan", "azure-openai", "qwen-cloud", "groqcloud", "abacusai"
    };

    /// <summary>Reads provider IDs from ~/.config/codexbar/config.json synchronously.</summary>
    public static List<ConfiguredProviderInfo> ReadLocalCodexBarProviders(bool onlyEnabled = false)
    {
        var enabledList = new List<ConfiguredProviderInfo>();
        var disabledList = new List<ConfiguredProviderInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var cfgPath = Path.Combine(Home, ".config", "codexbar", "config.json");
            if (File.Exists(cfgPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(cfgPath));
                if (doc.RootElement.TryGetProperty("providers", out var provs) && provs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in provs.EnumerateArray())
                    {
                        var id = Str(p, "id")?.Trim();
                        if (string.IsNullOrEmpty(id) || !seen.Add(id)) continue;
                        var enabled = p.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True;
                        if (enabled)
                            enabledList.Add(new ConfiguredProviderInfo(id, DisplayNameFor(id), true));
                        else if (!onlyEnabled)
                            disabledList.Add(new ConfiguredProviderInfo(id, DisplayNameFor(id), false));
                    }
                }
            }
        }
        catch { }

        if (enabledList.Count == 0)
        {
            foreach (var id in new[] { "claude", "codex", "antigravity" })
                if (seen.Add(id))
                    enabledList.Add(new ConfiguredProviderInfo(id, DisplayNameFor(id), true));
        }

        if (!onlyEnabled)
        {
            foreach (var (id, name) in KnownNames)
            {
                if (AliasIds.Contains(id)) continue;
                if (seen.Add(id))
                    disabledList.Add(new ConfiguredProviderInfo(id, name, false));
            }
            enabledList.AddRange(disabledList);
        }

        return enabledList;
    }

    private void LoadInitialConfigProviders()
    {
        _configured = ReadLocalCodexBarProviders(onlyEnabled: false);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("PhoneBox/" + AppPaths.Version);
        while (!ct.IsCancellationRequested)
        {
            var codexBar = Sh.Which("codexbar");
            if (codexBar is not null && Environment.TickCount64 >= _nextCodexBar)
            {
                var ok = await RefreshFromCodexBar(codexBar, ct);
                _nextCodexBar = Environment.TickCount64 + (ok ? 120_000 : 45_000);
            }

            if (!_providers.TryGetValue("codex", out var existingCodex) || existingCodex.Limits.Count == 0)
            {
                try { var c = ReadCodex(); if (c is not null) _providers["codex"] = c; }
                catch (Exception ex) { if (!_providers.ContainsKey("codex")) _providers["codex"] = Fail("codex", "Codex", ex.Message); }
            }

            if ((!_providers.TryGetValue("antigravity", out var existingAntigravity) || existingAntigravity.Limits.Count == 0 || existingAntigravity.Source == "native") && Environment.TickCount64 >= _nextAntigravity)
            {
                var ok = await RefreshAntigravity(ct);
                _nextAntigravity = Environment.TickCount64 + (ok ? 60_000 : 30_000);
            }

            if ((!_providers.TryGetValue("claude", out var existingClaude) || existingClaude.Limits.Count == 0) && Environment.TickCount64 >= _nextClaude)
            {
                var ok = await RefreshClaude(ct);
                _nextClaude = Environment.TickCount64 + (ok ? 180_000 : 60_000);
            }

            await Wake.Delay(TimeSpan.FromSeconds(30), ct);
        }
    }

    // ------------------------------------------------------------------ Claude
    private async Task<bool> RefreshClaude(CancellationToken ct)
    {
        var path = Path.Combine(Home, ".claude", ".credentials.json");
        string? json = File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : null;
#if !WINDOWS
        // macOS: Claude Code stores the credentials in the login Keychain instead of a file (read-only access).
        if (json is null && OperatingSystem.IsMacOS()) json = Sh.Run("security", "find-generic-password", "-s", "Claude Code-credentials", "-w")?.Trim();
#endif
        if (string.IsNullOrEmpty(json)) return true;

        string plan = "Claude";
        try
        {
            using var creds = JsonDocument.Parse(json);
            var o = creds.RootElement.GetProperty("claudeAiOauth");
            plan = PlanLabel(Str(o, "subscriptionType"), Str(o, "rateLimitTier"));
            var token = Str(o, "accessToken");
            var expires = o.TryGetProperty("expiresAt", out var e) && e.TryGetInt64(out var ms) ? ms : 0;

            if (string.IsNullOrEmpty(token)) { _providers["claude"] = Keep("claude", "Claude", plan, "Not signed in to Claude Code"); return false; }
            if (expires > 0 && expires < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            {
                _providers["claude"] = Keep("claude", "Claude", plan, "Token expired; it will refresh when Claude Code runs");
                return false;
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                _providers["claude"] = Keep("claude", "Claude", plan, $"Could not fetch quota (HTTP {(int)res.StatusCode})");
                return false;
            }
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var prevCost = _providers.TryGetValue("claude", out var prev) ? prev.Cost : null;
            _providers["claude"] = new QuotaProvider("claude", "Claude", plan, ParseClaudeLimits(doc.RootElement),
                ClaudeExtra(doc.RootElement), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null, null, "oauth", prevCost);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogDebug("claude quota: {Msg}", ex.Message);
            _providers["claude"] = Keep("claude", "Claude", plan, "Could not fetch quota: " + ex.Message);
            return false;
        }
    }

    /// <summary>On errors, keeps the last good values and attaches the error.</summary>
    private QuotaProvider Keep(string id, string name, string plan, string error) =>
        _providers.TryGetValue(id, out var prev) && prev.Limits.Count > 0
            ? prev with { Plan = plan, Error = error }
            : new QuotaProvider(id, name, plan, [], null, null, error);

    private static List<QuotaLimit> ParseClaudeLimits(JsonElement root)
    {
        var list = new List<QuotaLimit>();
        if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var l in limits.EnumerateArray())
            {
                var kind = Str(l, "kind");
                var model = l.TryGetProperty("scope", out var sc) && sc.ValueKind == JsonValueKind.Object
                    && sc.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.Object ? Str(m, "display_name") : null;
                var (label, window) = kind switch
                {
                    "session" => ("5-hour session", 5 * 3600L),
                    "weekly_all" => ("Weekly", 7 * 86400L),
                    "weekly_scoped" => ($"Weekly · {model ?? "model"}", 7 * 86400L),
                    _ => (kind ?? "Limit", 0L),
                };
                list.Add(new QuotaLimit(label, Num(l, "percent") ?? 0, Iso(Str(l, "resets_at")),
                    window > 0 ? window : null, Str(l, "severity") ?? "normal"));
            }
        }
        if (list.Count == 0)
        {
            // Legacy format: five_hour / seven_day
            foreach (var (key, label, window) in new[] { ("five_hour", "5-hour session", 18000L), ("seven_day", "Weekly", 604800L) })
                if (root.TryGetProperty(key, out var o) && o.ValueKind == JsonValueKind.Object)
                    list.Add(new QuotaLimit(label, Num(o, "utilization") ?? 0, Iso(Str(o, "resets_at")), window, "normal"));
        }
        return list;
    }

    private static string? ClaudeExtra(JsonElement root)
    {
        if (root.TryGetProperty("extra_usage", out var x) && x.ValueKind == JsonValueKind.Object
            && x.TryGetProperty("is_enabled", out var en) && en.ValueKind == JsonValueKind.True)
            return $"Extra usage on · {Num(x, "utilization") ?? 0:0}%";
        return null;
    }

    private static string PlanLabel(string? sub, string? tier)
    {
        var s = sub switch { "team" => "Team", "max" => "Max", "pro" => "Pro", "enterprise" => "Enterprise", null => "", _ => sub };
        var m = tier is null ? null : MaxTier().Match(tier);
        return m is { Success: true } ? $"{s} · Max {m.Groups[1].Value}".Trim(' ', '·') : (s == "" ? "Claude" : s);
    }

    [GeneratedRegex(@"max_(\d+x)")]
    private static partial Regex MaxTier();

    // ------------------------------------------------------------------ Codex
    private QuotaProvider? ReadCodex()
    {
        var root = Path.Combine(Home, ".codex", "sessions");
        if (!Directory.Exists(root)) return null;

        var files = new DirectoryInfo(root).EnumerateFiles("rollout-*.jsonl", SearchOption.AllDirectories);
        foreach (var f in files.OrderByDescending(f => f.LastWriteTimeUtc).Take(5))
        {
            var line = LastLineWith(f.FullName, "\"rate_limits\"");
            if (line is null) continue;
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("payload", out var p) || !p.TryGetProperty("rate_limits", out var rl)
                || rl.ValueKind != JsonValueKind.Object) continue;

            var limits = new List<QuotaLimit>();
            foreach (var key in new[] { "primary", "secondary" })
            {
                if (!rl.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object) continue;
                var mins = (long)(Num(w, "window_minutes") ?? 0);
                var label = mins switch { 300 => "5-hour", 10080 => "Weekly", >= 1440 => $"{mins / 1440}-day", > 0 => $"{mins / 60}-hour", _ => key };
                var resets = w.TryGetProperty("resets_at", out var r) && r.TryGetInt64(out var ra) ? ra * 1000
                    : Num(w, "resets_in_seconds") is { } rs ? new DateTimeOffset(f.LastWriteTimeUtc).ToUnixTimeMilliseconds() + (long)(rs * 1000) : (long?)null;
                var pct = Num(w, "used_percent") ?? 0;
                limits.Add(new QuotaLimit(label, pct, resets, mins > 0 ? mins * 60 : null, pct >= 95 ? "critical" : pct >= 80 ? "warning" : "normal"));
            }

            string? extra = null;
            if (rl.TryGetProperty("credits", out var cr) && cr.ValueKind == JsonValueKind.Object)
            {
                if (cr.TryGetProperty("unlimited", out var u) && u.ValueKind == JsonValueKind.True) extra = "Credits: unlimited";
                else if (cr.TryGetProperty("has_credits", out var has) && has.ValueKind == JsonValueKind.True
                    && Str(cr, "balance") is { } b && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var bal))
                    extra = "Credits: " + bal.ToString("#,0", CultureInfo.InvariantCulture);
                else if (has.ValueKind == JsonValueKind.False) extra = "Credits not enabled";
            }
            var reached = limits.Any(l => l.Percent >= 100)
                || rl.TryGetProperty("rate_limit_reached_type", out var reachedType) && reachedType.ValueKind == JsonValueKind.String
                || rl.TryGetProperty("spend_control_reached", out var spend) && spend.ValueKind == JsonValueKind.True;
            if (reached) extra = string.IsNullOrEmpty(extra) ? "Limit reached" : "Limit reached · " + extra;
            var plan = Str(rl, "plan_type") is { Length: > 0 } pt ? char.ToUpper(pt[0]) + pt[1..] : "Codex";
            var prevCost = _providers.TryGetValue("codex", out var prev) ? prev.Cost : null;
            return new QuotaProvider("codex", "Codex", plan, limits, extra,
                new DateTimeOffset(f.LastWriteTimeUtc).ToUnixTimeMilliseconds(), null, null, "local", prevCost, ReadCodexUsage(f.FullName));
        }
        return new QuotaProvider("codex", "Codex", "Codex", [], null, null, "No quota data in recent sessions");
    }

    private static CodexUsage? ReadCodexUsage(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var len = Math.Min(fs.Length, 1 << 20);
        fs.Seek(-len, SeekOrigin.End);
        var buf = new byte[len];
        fs.ReadExactly(buf);
        var lines = Encoding.UTF8.GetString(buf).Split('\n');
        string? model = null, effort = null;
        JsonElement? info = null;
        long updatedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeMilliseconds();
        for (var i = lines.Length - 1; i >= 1 && (info is null || model is null); i--)
        {
            var line = lines[i];
            if (!line.Contains("\"token_count\"") && !line.Contains("\"turn_context\"")) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("payload", out var payload)) continue;
                if (info is null && Str(payload, "type") == "token_count"
                    && payload.TryGetProperty("info", out var tokenInfo) && tokenInfo.ValueKind == JsonValueKind.Object)
                {
                    info = tokenInfo.Clone();
                    if (DateTimeOffset.TryParse(Str(root, "timestamp"), out var timestamp)) updatedAt = timestamp.ToUnixTimeMilliseconds();
                }
                if (model is null && root.TryGetProperty("type", out var type) && type.GetString() == "turn_context")
                {
                    model = Str(payload, "model");
                    effort = Str(payload, "effort");
                }
            }
            catch (JsonException) { /* partial line at the start of the tail */ }
        }
        if (info is null && model is null) return null;
        var token = info ?? default;
        var total = token.ValueKind == JsonValueKind.Object && token.TryGetProperty("total_token_usage", out var t) ? t : default;
        var last = token.ValueKind == JsonValueKind.Object && token.TryGetProperty("last_token_usage", out var l) ? l : default;
        return new CodexUsage(model, effort, Long(total, "input_tokens"), Long(total, "cached_input_tokens"),
            Long(total, "output_tokens"), Long(total, "reasoning_output_tokens"), Long(total, "total_tokens"),
            Long(last, "input_tokens"), Long(last, "output_tokens"), Long(token, "model_context_window"), updatedAt);
    }

    /// <summary>Reads the last 1 MB of a file and returns the last line containing the given text.</summary>
    private static string? LastLineWith(string path, string needle)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var len = Math.Min(fs.Length, 1 << 20);
        fs.Seek(-len, SeekOrigin.End);
        var buf = new byte[len];
        fs.ReadExactly(buf);
        var lines = Encoding.UTF8.GetString(buf).Split('\n');
        for (var i = lines.Length - 1; i >= 1; i--) // the first line may be partial
            if (lines[i].Contains(needle) && lines[i].Contains("\"primary\"")) return lines[i];
        return null;
    }

    // ------------------------------------------------------------------ CodexBar
    private async Task<bool> RefreshFromCodexBar(string path, CancellationToken ct)
    {
        try
        {
            var provTask = RunCodexBarAsync(path, "config providers --json-only", ct);
            var usageTask = RunCodexBarAsync(path, "usage --json-only", ct);
            var costTask = RunCodexBarAsync(path, "cost --json-only", ct);
            await Task.WhenAll(provTask, usageTask, costTask);

            var provJson = await provTask;
            var usageJson = await usageTask;
            var costJson = await costTask;

            var nameMap = new Dictionary<string, string>(KnownNames, StringComparer.OrdinalIgnoreCase);
            var configuredList = new List<ConfiguredProviderInfo>();
            var disabledList = new List<ConfiguredProviderInfo>();
            var configuredSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(provJson))
            {
                try
                {
                    using var provDoc = JsonDocument.Parse(provJson);
                    if (provDoc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var p in provDoc.RootElement.EnumerateArray())
                        {
                            var id = Str(p, "provider")?.Trim();
                            var disp = Str(p, "displayName")?.Trim();
                            var enabled = p.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True;
                            if (string.IsNullOrEmpty(id)) continue;
                            if (!string.IsNullOrEmpty(disp)) nameMap[id] = disp;
                            if (configuredSet.Add(id))
                            {
                                var info = new ConfiguredProviderInfo(id, DisplayNameFor(id, disp), enabled);
                                if (enabled) configuredList.Add(info);
                                else disabledList.Add(info);
                            }
                        }
                    }
                }
                catch { }
            }

            foreach (var lp in ReadLocalCodexBarProviders(onlyEnabled: false))
            {
                if (configuredSet.Add(lp.Id))
                {
                    var info = new ConfiguredProviderInfo(lp.Id, nameMap.GetValueOrDefault(lp.Id, lp.Name), lp.Enabled);
                    if (lp.Enabled) configuredList.Add(info);
                    else disabledList.Add(info);
                }
            }

            var rawCosts = ParseCodexBarCosts(costJson, nameMap, configuredSet, configuredList);

            var any = false;
            var seenProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(usageJson))
            {
                using var doc = JsonDocument.Parse(usageJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        var prov = Str(item, "provider")?.Trim();
                        if (string.IsNullOrEmpty(prov)) continue;
                        seenProviders.Add(prov);

                        var name = nameMap.TryGetValue(prov, out var nm) ? nm : DisplayNameFor(prov);
                        if (configuredSet.Add(prov))
                            configuredList.Add(new ConfiguredProviderInfo(prov, name, true));

                        var source = Str(item, "source");
                        string? errMsg = null;
                        if (item.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.Object)
                            errMsg = Str(errEl, "message");

                        if (!item.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object)
                        {
                            rawCosts.TryGetValue(prov, out var errCost);
                            var fallbackCost = errCost is not null ? BuildCostSummary(errCost, []) : (_providers.TryGetValue(prov, out var oldP) ? oldP.Cost : null);
                            if (_providers.TryGetValue(prov, out var prevP) && prevP.Limits.Count > 0)
                                _providers[prov] = prevP with { Error = errMsg, Cost = fallbackCost };
                            else
                                _providers[prov] = new QuotaProvider(prov, name, name, [], null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), errMsg, null, source ?? "codexbar", fallbackCost);
                            continue;
                        }

                        var limits = new List<QuotaLimit>();
                        var rateLabels = item.TryGetProperty("rateWindowLabels", out var rl) && rl.ValueKind == JsonValueKind.Object ? rl : default;
                        var paceObj = item.TryGetProperty("pace", out var poEl) && poEl.ValueKind == JsonValueKind.Object ? poEl : default;

                        QuotaPace? ReadPace(string key)
                        {
                            if (paceObj.ValueKind != JsonValueKind.Object || !paceObj.TryGetProperty(key, out var pEl) || pEl.ValueKind != JsonValueKind.Object)
                                return null;
                            var willLast = pEl.TryGetProperty("willLastToReset", out var wl)
                                ? (wl.ValueKind == JsonValueKind.True ? true : wl.ValueKind == JsonValueKind.False ? false : (bool?)null)
                                : null;
                            var eta = Num(pEl, "etaSeconds") is { } es ? (long?)Math.Round(es) : null;
                            return new QuotaPace(
                                Num(pEl, "deltaPercent"),
                                Num(pEl, "expectedUsedPercent"),
                                Str(pEl, "stage"),
                                Str(pEl, "summary"),
                                willLast,
                                eta);
                        }

                        void AddWindow(JsonElement w, string defaultLabel)
                        {
                            if (w.ValueKind != JsonValueKind.Object) return;
                            var mins = (long)(Num(w, "windowMinutes") ?? 0);
                            var label = defaultLabel;
                            if (rateLabels.ValueKind == JsonValueKind.Object && Str(rateLabels, defaultLabel.ToLowerInvariant()) is { } customLabel && !string.IsNullOrWhiteSpace(customLabel))
                                label = customLabel;
                            else if (defaultLabel == "primary")
                                label = mins == 300 ? "5-hour session" : mins == 10080 ? "Weekly" : (mins > 0 ? $"{mins / 60}-hour" : "Session");
                            else if (defaultLabel == "secondary")
                                label = mins == 10080 ? "Weekly" : mins == 300 ? "5-hour session" : (mins > 0 ? $"{mins / 1440}-day" : "Weekly");

                            var pct = Num(w, "usedPercent") ?? 0;
                            var resets = Iso(Str(w, "resetsAt"));
                            var resetDesc = Str(w, "resetDescription");
                            var windowSec = mins > 0 ? mins * 60 : (long?)null;
                            var sev = pct >= 95 ? "critical" : pct >= 80 ? "warning" : "normal";
                            var pace = ReadPace(defaultLabel.ToLowerInvariant());
                            limits.Add(new QuotaLimit(label, pct, resets, windowSec, sev, resetDesc, pace));
                        }

                        var hasExtraWindows = u.TryGetProperty("extraRateWindows", out var extraWindows)
                            && extraWindows.ValueKind == JsonValueKind.Array
                            && extraWindows.GetArrayLength() > 0;

                        if (string.Equals(prov, "antigravity", StringComparison.OrdinalIgnoreCase) && hasExtraWindows)
                        {
                            var idx = 0;
                            foreach (var ew in extraWindows.EnumerateArray())
                            {
                                var title = Str(ew, "title") ?? "Limit";
                                if (ew.TryGetProperty("window", out var winObj) && winObj.ValueKind == JsonValueKind.Object)
                                {
                                    var mins = (long)(Num(winObj, "windowMinutes") ?? 0);
                                    var pct = Num(winObj, "usedPercent") ?? 0;
                                    var resets = Iso(Str(winObj, "resetsAt"));
                                    var resetDesc = Str(winObj, "resetDescription");
                                    var pace = idx == 0 ? ReadPace("primary") : idx == 1 ? ReadPace("secondary") : null;
                                    limits.Add(new QuotaLimit(title, pct, resets, mins > 0 ? mins * 60 : null,
                                        pct >= 95 ? "critical" : pct >= 80 ? "warning" : "normal", resetDesc, pace));
                                    idx++;
                                }
                            }
                        }
                        else
                        {
                            if (u.TryGetProperty("primary", out var prim)) AddWindow(prim, "primary");
                            if (u.TryGetProperty("secondary", out var sec)) AddWindow(sec, "secondary");
                            if (u.TryGetProperty("tertiary", out var tert)) AddWindow(tert, "tertiary");

                            if (hasExtraWindows)
                            {
                                foreach (var ew in extraWindows.EnumerateArray())
                                {
                                    var title = Str(ew, "title") ?? "Limit";
                                    if (ew.TryGetProperty("window", out var winObj) && winObj.ValueKind == JsonValueKind.Object)
                                    {
                                        var pct = Num(winObj, "usedPercent") ?? 0;
                                        if (limits.Count > 0 && pct <= 0) continue;
                                        var mins = (long)(Num(winObj, "windowMinutes") ?? 0);
                                        var resets = Iso(Str(winObj, "resetsAt"));
                                        var resetDesc = Str(winObj, "resetDescription");
                                        limits.Add(new QuotaLimit(title, pct, resets, mins > 0 ? mins * 60 : null,
                                            pct >= 95 ? "critical" : pct >= 80 ? "warning" : "normal", resetDesc, null));
                                    }
                                }
                            }
                        }

                        string? extra = null;
                        if (item.TryGetProperty("credits", out var cr) && cr.ValueKind == JsonValueKind.Object)
                        {
                            if (Num(cr, "remaining") is { } rem) extra = $"Credits: {rem:0}";
                        }

                        var rawPlan = Str(u, "loginMethod")
                            ?? (u.TryGetProperty("planObservation", out var po) && po.ValueKind == JsonValueKind.Object ? Str(po, "rawPlan") : null)
                            ?? prov;
                        var plan = rawPlan.Length > 0 ? char.ToUpper(rawPlan[0]) + rawPlan[1..] : name;
                        var accountEmail = Str(u, "accountEmail")
                            ?? (u.TryGetProperty("identity", out var idEl) && idEl.ValueKind == JsonValueKind.Object ? Str(idEl, "accountEmail") : null);
                        var updatedAt = Iso(Str(u, "updatedAt")) ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                        rawCosts.TryGetValue(prov, out var rawCost);
                        var costSummary = rawCost is not null
                            ? BuildCostSummary(rawCost, limits)
                            : (_providers.TryGetValue(prov, out var oldProv) ? oldProv.Cost : null);

                        var providerObj = new QuotaProvider(prov, name, plan, limits, extra, updatedAt, errMsg, accountEmail, source ?? "codexbar", costSummary);
                        _providers[prov] = providerObj;
                        any = true;
                    }
                }
            }

            // Include providers that have cost data even if they weren't in usage output
            foreach (var (prov, rawCost) in rawCosts)
            {
                if (seenProviders.Contains(prov)) continue;
                var name = nameMap.TryGetValue(prov, out var nm) ? nm : DisplayNameFor(prov);
                var costSummary = BuildCostSummary(rawCost, []);
                if (_providers.TryGetValue(prov, out var existing))
                    _providers[prov] = existing with { Cost = costSummary };
                else
                    _providers[prov] = new QuotaProvider(prov, name, name, [], null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null, null, "local", costSummary);
                any = true;
            }

            if (configuredList.Count > 0 || disabledList.Count > 0)
                _configured = [.. configuredList, .. disabledList];

            _overview = BuildOverview(Snapshot());
            return any;
        }
        catch (Exception ex)
        {
            _log.LogDebug("codexbar error: {Msg}", ex.Message);
            return false;
        }
    }

    private sealed record RawProviderCost(
        string Provider,
        double Last30DaysCostUsd,
        long Last30DaysTokens,
        double SessionCostUsd,
        long SessionTokens,
        int HistoryDays,
        List<QuotaDayCost> Daily,
        List<QuotaProjectCost> Projects);

    private static Dictionary<string, RawProviderCost> ParseCodexBarCosts(
        string? costJson,
        Dictionary<string, string> nameMap,
        HashSet<string> configuredSet,
        List<ConfiguredProviderInfo> configuredList)
    {
        var map = new Dictionary<string, RawProviderCost>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(costJson)) return map;

        try
        {
            using var doc = JsonDocument.Parse(costJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return map;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var prov = Str(item, "provider")?.Trim();
                if (string.IsNullOrEmpty(prov)) continue;
                var name = nameMap.TryGetValue(prov, out var nm) ? nm : DisplayNameFor(prov);
                if (configuredSet.Add(prov))
                    configuredList.Add(new ConfiguredProviderInfo(prov, name, true));

                var daily = new List<QuotaDayCost>();
                if (item.TryGetProperty("daily", out var dailyArr) && dailyArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var d in dailyArr.EnumerateArray())
                    {
                        var date = Str(d, "date") ?? "";
                        if (string.IsNullOrEmpty(date)) continue;
                        var dCost = Num(d, "totalCost") ?? 0;
                        var dTok = (long)(Num(d, "totalTokens") ?? 0);
                        var models = new List<QuotaModelCost>();
                        if (d.TryGetProperty("modelBreakdowns", out var mbArr) && mbArr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var m in mbArr.EnumerateArray())
                            {
                                var mName = Str(m, "modelName") ?? "model";
                                var mCost = Num(m, "cost") ?? 0;
                                var mTok = (long)(Num(m, "totalTokens") ?? 0);
                                models.Add(new QuotaModelCost(mName, mCost, mTok));
                            }
                        }
                        daily.Add(new QuotaDayCost(date, dCost, dTok, models));
                    }
                }

                var projects = new List<QuotaProjectCost>();
                if (item.TryGetProperty("projects", out var projArr) && projArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in projArr.EnumerateArray())
                    {
                        var pName = Str(p, "name") ?? "";
                        if (string.IsNullOrEmpty(pName)) continue;
                        var pPath = Str(p, "path");
                        var pCost = Num(p, "totalCost") ?? 0;
                        var pTok = (long)(Num(p, "totalTokens") ?? 0);
                        projects.Add(new QuotaProjectCost(pName, pPath, pCost, pTok));
                    }
                }

                var last30Cost = Num(item, "last30DaysCostUSD") ?? (item.TryGetProperty("totals", out var tot) && tot.ValueKind == JsonValueKind.Object ? Num(tot, "totalCost") ?? 0 : 0);
                var last30Tok = (long)(Num(item, "last30DaysTokens") ?? (item.TryGetProperty("totals", out var tot2) && tot2.ValueKind == JsonValueKind.Object ? Num(tot2, "totalTokens") ?? 0 : 0));
                var sessCost = Num(item, "sessionCostUSD") ?? 0;
                var sessTok = (long)(Num(item, "sessionTokens") ?? 0);
                var histDays = (int)(Num(item, "historyDays") ?? 30);

                if (last30Cost <= 0 && last30Tok <= 0 && daily.Count == 0) continue;

                map[prov] = new RawProviderCost(prov, last30Cost, last30Tok, sessCost, sessTok, histDays, daily, projects);
            }
        }
        catch { }

        return map;
    }

    private static QuotaCostSummary BuildCostSummary(RawProviderCost raw, IReadOnlyList<QuotaLimit> limits)
    {
        var sortedDaily = raw.Daily.OrderBy(d => d.Date, StringComparer.Ordinal).ToList();
        var latestDay = sortedDaily.LastOrDefault();
        var latestModels = latestDay?.Models
            .OrderByDescending(m => m.Cost)
            .ThenByDescending(m => m.TotalTokens)
            .Take(5)
            .ToList() ?? [];

        var modelMap = new Dictionary<string, (double Cost, long Tokens)>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in sortedDaily)
        {
            foreach (var m in d.Models)
            {
                modelMap.TryGetValue(m.ModelName, out var cur);
                modelMap[m.ModelName] = (cur.Cost + m.Cost, cur.Tokens + m.TotalTokens);
            }
        }
        var topModels30d = modelMap
            .Select(kv => new QuotaModelCost(kv.Key, kv.Value.Cost, kv.Value.Tokens))
            .OrderByDescending(m => m.Cost)
            .ThenByDescending(m => m.TotalTokens)
            .Take(6)
            .ToList();

        var topProjects = raw.Projects
            .OrderByDescending(p => p.TotalCost)
            .ThenByDescending(p => p.TotalTokens)
            .Take(5)
            .ToList();

        // Compute current window cost & tokens if a weekly/secondary/primary window with ResetsAt & WindowSec exists
        double? winCost = null;
        long? winTokens = null;
        var winLimit = limits.FirstOrDefault(l => l.ResetsAt is not null && l.WindowSec is >= 86400)
            ?? limits.FirstOrDefault(l => l.ResetsAt is not null && l.WindowSec is > 0);

        if (winLimit is { ResetsAt: { } winEndMs, WindowSec: { } winSec } && sortedDaily.Count > 0)
        {
            var winStartMs = winEndMs - winSec * 1000L;
            double cSum = 0;
            double tSum = 0;
            foreach (var d in sortedDaily)
            {
                if (!DateTime.TryParseExact(d.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                    continue;
                var localStart = new DateTimeOffset(dt, TimeZoneInfo.Local.GetUtcOffset(dt));
                var dayStartMs = localStart.ToUnixTimeMilliseconds();
                var dayEndMs = dayStartMs + 86_400_000L;
                if (dayEndMs <= winStartMs || dayStartMs >= winEndMs) continue;
                var frac = Math.Clamp((Math.Min(dayEndMs, winEndMs) - Math.Max(dayStartMs, winStartMs)) / 86_400_000.0, 0.0, 1.0);
                cSum += d.TotalCost * frac;
                tSum += d.TotalTokens * frac;
            }
            winCost = cSum;
            winTokens = (long)Math.Round(tSum);
        }

        return new QuotaCostSummary(
            TodayCostUsd: raw.SessionCostUsd,
            TodayTokens: raw.SessionTokens,
            WindowCostUsd: winCost,
            WindowTokens: winTokens,
            Last30DaysCostUsd: raw.Last30DaysCostUsd,
            Last30DaysTokens: raw.Last30DaysTokens,
            HistoryDays: raw.HistoryDays,
            LatestDayDate: latestDay?.Date,
            LatestDayCostUsd: latestDay?.TotalCost,
            LatestDayTokens: latestDay?.TotalTokens,
            LatestDayModels: latestModels,
            TopModels30d: topModels30d,
            Daily: sortedDaily,
            TopProjects: topProjects);
    }

    private static QuotaOverview BuildOverview(IReadOnlyList<QuotaProvider> providers)
    {
        long totalTokens = 0;
        double totalCost = 0;
        int maxDays = 30;
        var subs = new List<QuotaSubscriptionItem>();
        var familyMap = new Dictionary<string, (string FamilyName, string ProvId, string ProvName, double Cost, long Tokens, HashSet<string> SubModels)>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in providers)
        {
            var hasLimits = p.Limits.Count > 0;
            var hasCost = p.Cost is { Last30DaysTokens: > 0 } or { Last30DaysCostUsd: > 0 };
            if (!hasLimits && !hasCost && string.IsNullOrEmpty(p.Extra)) continue;

            var tok30 = p.Cost?.Last30DaysTokens ?? 0;
            var cost30 = p.Cost?.Last30DaysCostUsd ?? 0;
            if (p.Cost?.HistoryDays is > 0 and var hd) maxDays = Math.Max(maxDays, hd);

            totalTokens += tok30;
            totalCost += cost30;

            var prim = p.Limits.FirstOrDefault();
            subs.Add(new QuotaSubscriptionItem(
                p.Id,
                p.Name,
                p.Plan,
                tok30,
                cost30,
                prim?.Percent,
                prim is not null ? Math.Max(0, 100 - prim.Percent) : null,
                prim?.Label));

            if (p.Cost?.TopModels30d is { Count: > 0 } models)
            {
                foreach (var m in models)
                {
                    var family = ClassifyModelFamily(m.ModelName, p.Name);
                    var key = $"{p.Id}:{family}";
                    if (!familyMap.TryGetValue(key, out var cur))
                        cur = (family, p.Id, p.Name, 0, 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    cur.Cost += m.Cost;
                    cur.Tokens += m.TotalTokens;
                    cur.SubModels.Add(m.ModelName);
                    familyMap[key] = cur;
                }
            }
        }

        var sortedSubs = subs
            .OrderByDescending(s => s.Cost30dUsd)
            .ThenByDescending(s => s.Tokens30d)
            .ThenByDescending(s => s.PrimaryRemainingPercent ?? -1)
            .ToList();

        var topModels = familyMap.Values
            .OrderByDescending(x => x.Cost)
            .ThenByDescending(x => x.Tokens)
            .Take(5)
            .Select(x => new QuotaTopModelItem(
                x.FamilyName,
                x.ProvId,
                x.ProvName,
                x.Cost,
                x.Tokens,
                string.Join(" · ", x.SubModels.Take(2))))
            .ToList();

        return new QuotaOverview(
            TotalTokens30d: totalTokens,
            TotalCost30dUsd: totalCost,
            HistoryDays: maxDays,
            ConnectedCount: sortedSubs.Count,
            UpdatedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Subscriptions: sortedSubs,
            TopModels: topModels);
    }

    private static string ClassifyModelFamily(string modelName, string fallbackProviderName)
    {
        var lower = modelName.ToLowerInvariant();
        if (lower.StartsWith("claude")) return "Claude";
        if (lower.StartsWith("gpt-") || lower.StartsWith("o1") || lower.StartsWith("o3") || lower.StartsWith("o4")) return "GPT";
        if (lower.StartsWith("codex")) return "Codex";
        if (lower.StartsWith("gemini")) return "Gemini";
        if (lower.StartsWith("qwen")) return "Qwen";
        if (lower.StartsWith("deepseek")) return "DeepSeek";
        if (lower.StartsWith("grok")) return "Grok";
        if (lower.StartsWith("kimi")) return "Kimi";
        if (lower.StartsWith("minimax")) return "MiniMax";
        return fallbackProviderName;
    }

    private static async Task<string?> RunCodexBarAsync(string path, string arguments, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(path, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = AppPaths.NeutralCwd
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var outTask = p.StandardOutput.ReadToEndAsync(ct);
            _ = p.StandardError.ReadToEndAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(25));
            await p.WaitForExitAsync(cts.Token);
            var result = await outTask;
            if (string.IsNullOrWhiteSpace(result)) return null;
            var start = result.IndexOf('[');
            var end = result.LastIndexOf(']');
            return start >= 0 && end > start ? result[start..(end + 1)] : null;
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ helpers
    private static QuotaProvider Fail(string id, string name, string msg) => new(id, name, name, [], null, null, msg);
    private static string? Str(JsonElement e, string p) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static double? Num(JsonElement e, string p) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;
    private static long? Long(JsonElement e, string p) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
    private static long? Iso(string? s) => DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.ToUnixTimeMilliseconds() : null;
}
