using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PhoneBox.Agent;

/// <summary>Global settings for the Speech to text widget, speech providers, and transcript disk logging.</summary>
public sealed class SpeechConfig
{
    public static readonly string[] ValidProviders = ["auto", "apple", "openai"];
    public static readonly string[] ValidTranscriptFormats = ["daily-md", "daily-txt", "single-md", "per-item-txt", "jsonl"];

    public bool Enabled { get; set; }
    public string Provider { get; set; } = "auto";
    public string Language { get; set; } = "";
    public int MaxRecordSec { get; set; } = 90;
    public bool AppleOnDevice { get; set; }

    public string OpenAiApiKey { get; set; } = "";
    public string OpenAiModel { get; set; } = "gpt-4o-mini-transcribe";

    public bool SaveTranscripts { get; set; }
    public string TranscriptDir { get; set; } = "";
    public string TranscriptFormat { get; set; } = "daily-md";
    public bool SaveAudioCopy { get; set; }
    public int HistoryLimit { get; set; } = 15;

    public void Normalize()
    {
        Provider = NormalizeEnum(Provider, ValidProviders, "auto");
        Language = TrimMax(Language, 24);
        MaxRecordSec = Math.Clamp(MaxRecordSec == 0 ? 90 : MaxRecordSec, 5, 600);

        OpenAiApiKey = TrimMax(OpenAiApiKey, 512);
        OpenAiModel = DefaultIfEmpty(TrimMax(OpenAiModel, 128), "gpt-4o-mini-transcribe");

        TranscriptDir = CleanPathField(TranscriptDir, 512);
        TranscriptFormat = NormalizeEnum(TranscriptFormat, ValidTranscriptFormats, "daily-md");
        HistoryLimit = Math.Clamp(HistoryLimit == 0 ? 15 : HistoryLimit, 1, 50);
    }

    internal IEnumerable<string?> EnumerateSecrets()
    {
        yield return OpenAiApiKey;
    }

    internal static string NormalizeEnum(string? value, string[] allowed, string fallback)
    {
        var trimmed = (value ?? "").Trim().ToLowerInvariant();
        return Array.IndexOf(allowed, trimmed) >= 0 ? trimmed : fallback;
    }

    internal static string TrimMax(string? value, int maxLen)
    {
        var s = (value ?? "").Trim();
        return s.Length <= maxLen ? s : s[..maxLen];
    }

    internal static string DefaultIfEmpty(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    internal static string CleanPathField(string? value, int maxLen)
    {
        var s = (value ?? "").Trim();
        if (s.IndexOf('\0') >= 0 || s.Any(char.IsControl)) return "";
        return s.Length <= maxLen ? s : s[..maxLen];
    }
}

/// <summary>Normalized per-widget Speech to text options.</summary>
public sealed record SpeechWidgetSettings(
    string Title,
    string Provider,
    string Language,
    string Mode,
    string NoteTarget,
    string NoteMode,
    bool AutoAppendNote,
    string SaveTranscript,
    bool Control,
    string TranscriptDir = "")
{
    public static readonly string[] ValidWidgetProviders = ["default", "auto", "apple", "openai"];
    public static readonly string[] ValidModes = ["toggle", "hold"];
    public static readonly string[] ValidNoteModes = ["append", "prepend", "bullet", "timestamp", "replace"];
    public static readonly string[] ValidSavePolicies = ["default", "always", "never"];

    private static readonly HashSet<string> AllowedPublicKeys =
    [
        "title",
        "provider",
        "language",
        "mode",
        "noteTarget",
        "noteMode",
        "autoAppendNote",
        "saveTranscript",
        "control",
    ];

    public static SpeechWidgetSettings Parse(Dictionary<string, JsonElement>? options)
    {
        var title = SpeechConfig.DefaultIfEmpty(SpeechConfig.TrimMax(GetString(options, "title"), 80), "Speech to text");
        var provider = SpeechConfig.NormalizeEnum(GetString(options, "provider"), ValidWidgetProviders, "default");
        var language = SpeechConfig.TrimMax(GetString(options, "language"), 24);
        var mode = SpeechConfig.NormalizeEnum(GetString(options, "mode"), ValidModes, "toggle");
        var noteTarget = SpeechConfig.TrimMax(GetString(options, "noteTarget"), 160);
        var noteMode = SpeechConfig.NormalizeEnum(GetString(options, "noteMode"), ValidNoteModes, "append");
        var autoAppend = GetBool(options, "autoAppendNote", fallback: false);
        var saveTranscript = SpeechConfig.NormalizeEnum(GetString(options, "saveTranscript"), ValidSavePolicies, "default");
        var control = GetBool(options, "control", fallback: false);
        var transcriptDir = SpeechConfig.CleanPathField(GetString(options, "transcriptDir"), 512);

        return new SpeechWidgetSettings(title, provider, language, mode, noteTarget, noteMode, autoAppend, saveTranscript, control, transcriptDir);
    }

    /// <summary>Allow-lists only panel-facing Speech widget options before exposing /api/config to phones.</summary>
    public static Dictionary<string, JsonElement>? ScrubPublicWidget(WidgetConfig w, Dictionary<string, JsonElement>? options)
    {
        if (options is null) return null;

        if (w.Type == "speech")
        {
            return options
                .Where(kv => AllowedPublicKeys.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value.Clone());
        }

        if (w.Type != "stack" || !options.TryGetValue("widgets", out var raw) || raw.ValueKind != JsonValueKind.Array)
            return options;

        if (JsonNode.Parse(raw.GetRawText()) is not JsonArray arr)
            return options;

        var changed = false;
        foreach (var item in arr)
        {
            if (item is not JsonObject obj) continue;
            if (obj["type"] is not JsonValue tv || !tv.TryGetValue<string>(out var type) || !string.Equals(type, "speech", StringComparison.Ordinal))
                continue;
            if (obj["options"] is not JsonObject childOpts) continue;

            var kept = new JsonObject();
            foreach (var kv in childOpts)
            {
                if (AllowedPublicKeys.Contains(kv.Key))
                    kept[kv.Key] = kv.Value?.DeepClone();
                else
                    changed = true;
            }
            if (changed)
                obj["options"] = kept;
        }

        if (!changed) return options;
        var cloned = options.ToDictionary(kv => kv.Key, kv => kv.Value.Clone());
        cloned["widgets"] = JsonSerializer.SerializeToElement(arr);
        return cloned;
    }

    /// <summary>Normalizes speech widget options on standalone widgets and Smart Stack children without stripping unknown target IDs.</summary>
    public static void NormalizeWidget(WidgetConfig w)
    {
        if (w.Type == "speech")
        {
            w.Options ??= new Dictionary<string, JsonElement>();
            NormalizeOptionDict(w.Options);
            return;
        }

        if (w.Type != "stack" || w.Options is null || !w.Options.TryGetValue("widgets", out var raw)) return;

        JsonNode? root;
        try
        {
            root = raw.ValueKind == JsonValueKind.String
                ? JsonNode.Parse(raw.GetString() ?? "[]")
                : JsonNode.Parse(raw.GetRawText());
        }
        catch (JsonException)
        {
            return;
        }

        if (root is not JsonArray arr) return;
        var changed = false;
        foreach (var item in arr)
        {
            if (item is not JsonObject obj) continue;
            if (obj["type"] is not JsonValue tv || !tv.TryGetValue<string>(out var type)) continue;
            if (!string.Equals(type, "speech", StringComparison.Ordinal)) continue;

            var dict = obj["options"] is JsonObject optsNode
                ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(optsNode.ToJsonString()) ?? []
                : [];
            NormalizeOptionDict(dict);
            obj["options"] = JsonSerializer.SerializeToNode(dict);
            changed = true;
        }

        if (changed)
            w.Options["widgets"] = JsonSerializer.SerializeToElement(arr);
    }

    private static void NormalizeOptionDict(Dictionary<string, JsonElement> options)
    {
        var parsed = Parse(options);
        options["title"] = JsonSerializer.SerializeToElement(parsed.Title);
        options["provider"] = JsonSerializer.SerializeToElement(parsed.Provider);
        options["language"] = JsonSerializer.SerializeToElement(parsed.Language);
        options["mode"] = JsonSerializer.SerializeToElement(parsed.Mode);
        options["noteTarget"] = JsonSerializer.SerializeToElement(parsed.NoteTarget);
        options["noteMode"] = JsonSerializer.SerializeToElement(parsed.NoteMode);
        options["autoAppendNote"] = JsonSerializer.SerializeToElement(parsed.AutoAppendNote);
        options["saveTranscript"] = JsonSerializer.SerializeToElement(parsed.SaveTranscript);
        options["transcriptDir"] = JsonSerializer.SerializeToElement(parsed.TranscriptDir);
        options["control"] = JsonSerializer.SerializeToElement(parsed.Control);
    }

    private static string? GetString(Dictionary<string, JsonElement>? options, string key)
    {
        if (options is null || !options.TryGetValue(key, out var val)) return null;
        return val.ValueKind == JsonValueKind.String ? val.GetString() : null;
    }

    private static bool GetBool(Dictionary<string, JsonElement>? options, string key, bool fallback)
    {
        if (options is null || !options.TryGetValue(key, out var val)) return fallback;
        return val.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        };
    }
}

public sealed record SpeechWidgetSaveInfoDto(
    bool SaveEnabled,
    string SaveTargetDisplay);

public sealed record SpeechEntryDto(
    string Id,
    string Text,
    string Provider,
    string Language,
    DateTimeOffset CreatedAt,
    int DurationMs,
    [property: JsonIgnore] string? SavedFile,
    string? NoteTarget,
    bool AppendedToNote,
    string WidgetId,
    string? SavedFileDisplay = null);

public sealed record SpeechStateDto(
    string Status,
    string? ActiveWidgetId,
    string? ActiveProvider,
    long StartedAtUnixMs,
    int ElapsedMs,
    int MaxRecordSec,
    string DefaultProvider,
    bool AppleAvailable,
    string? LastError,
    IReadOnlyList<SpeechEntryDto> Entries,
    bool DefaultSaveTranscripts = false,
    string DefaultTranscriptDir = "",
    IReadOnlyDictionary<string, SpeechWidgetSaveInfoDto>? WidgetTargets = null);

public sealed record SpeechBrowseRequest(string? Path);

public sealed record SpeechTestRequest(SpeechConfig? Config);
