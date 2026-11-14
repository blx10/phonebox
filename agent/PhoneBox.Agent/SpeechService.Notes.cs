using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PhoneBox.Agent;

internal sealed partial class SpeechService
{
    internal (bool Ok, string? ResolvedTarget, string? Error) AppendToNoteWidget(
        string text,
        string targetSpec,
        string sourceWidgetId,
        string noteMode,
        DateTimeOffset when,
        IReadOnlySet<string>? allowedPageIds = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (false, null, "Transcript is empty.");

        if (string.IsNullOrWhiteSpace(targetSpec))
            return (false, null, "No target Note widget is configured.");

        var formattedChunk = FormatNoteChunk(text.Trim(), noteMode, when);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var version = _store.Version;
            var currentJson = JsonSerializer.Serialize(_store.Get(), ConfigStore.WriteOpts);
            var copy = JsonSerializer.Deserialize<AgentConfig>(currentJson, CaseInsensitiveReadOpts);
            if (copy is null)
                return (false, null, "Could not clone config.");

            var sourcePageId = FindContainingPageId(copy, sourceWidgetId);
            var match = FindTargetNote(copy, targetSpec, sourcePageId, allowedPageIds);
            if (match is null)
                return (false, null, "Target Note widget not found.");

            var nextText = ApplyNoteMode(match.Value.ExistingText, formattedChunk, noteMode);
            match.Value.Apply(nextText);

            var err = _store.Save(JsonSerializer.Serialize(copy, ConfigStore.WriteOpts), version);
            if (err is null)
            {
                _push.Emit("refresh");
                return (true, match.Value.CanonicalTarget, null);
            }
            if (!string.Equals(err, ConfigStore.Conflict, StringComparison.Ordinal))
                return (false, null, "Could not save Note widget update.");
        }

        return (false, null, "Concurrent config updates prevented updating the Note widget.");
    }

    private readonly record struct NoteTargetMatch(
        string CanonicalTarget,
        string ExistingText,
        Action<string> Apply);

    private static NoteTargetMatch? FindTargetNote(
        AgentConfig cfg,
        string targetSpec,
        string? sourcePageId,
        IReadOnlySet<string>? allowedPageIds)
    {
        var spec = targetSpec.Trim();
        if (string.IsNullOrEmpty(spec))
            return null;

        IEnumerable<PageConfig> scopedPages = allowedPageIds is not null
            ? cfg.Pages.Where(p => p.Enabled && allowedPageIds.Contains(p.Id))
            : cfg.Pages.Where(p => p.Enabled);

        if (string.Equals(spec, "first", StringComparison.OrdinalIgnoreCase))
        {
            if (sourcePageId is not null)
            {
                var samePage = FindFirstNoteInPages(scopedPages.Where(p => string.Equals(p.Id, sourcePageId, StringComparison.Ordinal)));
                if (samePage is not null) return samePage;
            }
            return FindFirstNoteInPages(scopedPages);
        }

        var slashIdx = spec.IndexOf('/');
        string? wantedPage = slashIdx > 0 ? spec[..slashIdx] : null;
        string wantedWidget = slashIdx > 0 ? spec[(slashIdx + 1)..] : spec;

        var pages = wantedPage is not null
            ? scopedPages.Where(p => string.Equals(p.Id, wantedPage, StringComparison.Ordinal))
            : scopedPages;

        foreach (var page in pages)
        {
            foreach (var w in page.Widgets ?? [])
            {
                if (w.Type == "note" && string.Equals(w.Id, wantedWidget, StringComparison.Ordinal))
                    return MakeStandaloneNoteMatch(page.Id, w);

                if (w.Type == "stack" && wantedWidget.Contains(':'))
                {
                    var colon = wantedWidget.IndexOf(':');
                    var stackId = wantedWidget[..colon];
                    var childId = wantedWidget[(colon + 1)..];
                    if (string.Equals(w.Id, stackId, StringComparison.Ordinal))
                    {
                        var childMatch = FindStackNoteChild(page.Id, w, childId);
                        if (childMatch is not null) return childMatch;
                    }
                }
            }
        }

        return null;
    }

    private static NoteTargetMatch? FindFirstNoteInPages(IEnumerable<PageConfig> pages)
    {
        foreach (var page in pages)
        {
            if (!page.Enabled) continue;
            foreach (var w in page.Widgets ?? [])
            {
                if (w.Type == "note") return MakeStandaloneNoteMatch(page.Id, w);
                if (w.Type == "stack")
                {
                    var childMatch = FindStackNoteChild(page.Id, w, childId: null);
                    if (childMatch is not null) return childMatch;
                }
            }
        }
        return null;
    }

    private static NoteTargetMatch MakeStandaloneNoteMatch(string pageId, WidgetConfig w)
    {
        var existing = "";
        if (w.Options is not null && w.Options.TryGetValue("text", out var raw) && raw.ValueKind == JsonValueKind.String)
            existing = raw.GetString() ?? "";

        return new NoteTargetMatch($"{pageId}/{w.Id}", existing, next =>
        {
            w.Options ??= new Dictionary<string, JsonElement>();
            w.Options["text"] = JsonSerializer.SerializeToElement(next);
        });
    }

    private static string? TryJsonString(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool JsonBoolOrTrue(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : true;

    private static NoteTargetMatch? FindStackNoteChild(string pageId, WidgetConfig stack, string? childId)
    {
        if (stack.Options is null || !stack.Options.TryGetValue("widgets", out var raw)) return null;

        JsonNode? root;
        try
        {
            root = raw.ValueKind == JsonValueKind.String
                ? JsonNode.Parse(raw.GetString() ?? "[]")
                : JsonNode.Parse(raw.GetRawText());
        }
        catch (JsonException) { return null; }

        if (root is not JsonArray arr) return null;

        for (var i = 0; i < arr.Count; i++)
        {
            if (arr[i] is not JsonObject obj) continue;
            var type = TryJsonString(obj["type"]);
            if (!string.Equals(type, "note", StringComparison.Ordinal)) continue;

            if (!JsonBoolOrTrue(obj["enabled"]) || !JsonBoolOrTrue(obj["active"])) continue;

            var id = TryJsonString(obj["id"]) ?? $"card-{i}";
            if (childId is not null && !string.Equals(id, childId, StringComparison.Ordinal)) continue;

            var existing = TryJsonString(obj["options"]?["text"]) ?? "";
            var capturedIndex = i;
            return new NoteTargetMatch($"{pageId}/{stack.Id}:{id}", existing, next =>
            {
                if (arr[capturedIndex] is JsonObject targetObj)
                {
                    if (targetObj["options"] is not JsonObject opts)
                    {
                        opts = new JsonObject();
                        targetObj["options"] = opts;
                    }
                    opts["text"] = next;
                    stack.Options["widgets"] = JsonSerializer.SerializeToElement(arr);
                }
            });
        }

        return null;
    }

    internal static string FormatNoteChunk(string text, string noteMode, DateTimeOffset when) => noteMode switch
    {
        "bullet" => $"• {text}",
        "timestamp" => $"[{when.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}] {text}",
        _ => text,
    };

    internal static string ApplyNoteMode(string existing, string chunk, string noteMode)
    {
        var cleanExisting = IsPlaceholderNoteText(existing) ? "" : existing.TrimEnd();
        if (string.Equals(noteMode, "replace", StringComparison.Ordinal) || string.IsNullOrEmpty(cleanExisting))
            return chunk;

        return noteMode switch
        {
            "prepend" => $"{chunk}\n{cleanExisting}",
            _ => $"{cleanExisting}\n{chunk}",
        };
    }

    private static bool IsPlaceholderNoteText(string s)
    {
        var t = (s ?? "").Trim();
        return string.IsNullOrEmpty(t)
            || string.Equals(t, "Write something here…", StringComparison.Ordinal)
            || string.Equals(t, "Write something here...", StringComparison.Ordinal);
    }

    private static string? FindContainingPageId(AgentConfig cfg, string widgetId)
    {
        var trimmed = widgetId.Trim();
        var slashIdx = trimmed.IndexOf('/');
        if (slashIdx > 0)
        {
            var pid = trimmed[..slashIdx];
            return cfg.Pages.FirstOrDefault(p => string.Equals(p.Id, pid, StringComparison.Ordinal))?.Id ?? pid;
        }
        var topId = trimmed.Contains(':') ? trimmed[..trimmed.IndexOf(':')] : trimmed;
        return cfg.Pages
            .OrderByDescending(p => p.Enabled)
            .FirstOrDefault(p => (p.Widgets ?? []).Any(w => string.Equals(w.Id, topId, StringComparison.Ordinal)))?.Id;
    }
}
