using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

public sealed record SpeechActionResult(bool Ok, int StatusCode, string? Error, SpeechStateDto State);

public sealed record SpeechSetupStatusDto(
    string Platform,
    bool AppleSupported,
    AppleSpeechStatus? AppleStatus,
    bool FfmpegFound,
    bool MicCaptureAvailable,
    string DefaultTranscriptDir,
    bool SaveTranscripts,
    string TranscriptDir);

public sealed record SpeechDirectoryEntry(string Name, string Path);

public sealed record SpeechBrowseResult(
    bool Ok,
    string CurrentPath,
    string? ParentPath,
    string DefaultPath,
    bool Writable,
    IReadOnlyList<SpeechDirectoryEntry> Presets,
    IReadOnlyList<SpeechDirectoryEntry> Entries,
    string? Error = null);

public sealed record SpeechTestResult(bool Ok, string Provider, string Message);

/// <summary>Coordinates microphone capture, local speech transcription, Note widget appending, and transcript disk storage.</summary>
internal sealed partial class SpeechService : IDisposable
{
    private const int FastStopWaitMs = 1200;
    private static readonly Regex LocalPathRegex = LocalPathPattern();
    private static readonly JsonSerializerOptions CompactJsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static readonly JsonSerializerOptions CaseInsensitiveReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly ConfigStore _store;
    private readonly PushStore _push;
    private readonly AgentEndpoint _endpoint;
    private readonly ISpeechAppleClient _appleHelper;
    private readonly ISpeechAudioCapture _capture;
    private readonly SpeechRateLimiter _rateLimiter;
    private readonly string _tempRoot;
    private readonly Func<bool, IReadOnlyCollection<int>, TimeSpan, HttpClient> _httpFactory;

    private readonly SemaphoreSlim _opGate = new(1, 1);
    private readonly SemaphoreSlim _diskGate = new(1, 1);
    private readonly Lock _stateLock = new();

    private string _status = "idle"; // idle | recording | transcribing
    private long _activeOpId;
    private string? _activeWidgetId;
    private string? _activeProvider;
    private DateTimeOffset _startedAt;
    private string? _lastError;
    private IActiveRecording? _activeRecording;
    private Task? _transcriptionTask;
    private CancellationTokenSource? _activeCts;
    private readonly List<SpeechEntryDto> _entries = [];

    public SpeechService(ConfigStore store, PushStore push, AgentEndpoint endpoint)
        : this(store, push, endpoint, new SpeechAppleHelper(), null, null, null, null)
    {
    }

    internal SpeechService(
        ConfigStore store,
        PushStore push,
        AgentEndpoint endpoint,
        ISpeechAppleClient appleHelper,
        ISpeechAudioCapture? capture,
        SpeechRateLimiter? rateLimiter,
        string? tempRoot,
        Func<bool, IReadOnlyCollection<int>, TimeSpan, HttpClient>? httpFactory = null)
    {
        _store = store;
        _push = push;
        _endpoint = endpoint;
        _appleHelper = appleHelper;
        _capture = capture ?? new PlatformSpeechAudioCapture(appleHelper);
        _rateLimiter = rateLimiter ?? new SpeechRateLimiter();
        _tempRoot = tempRoot ?? Path.Combine(AppPaths.LocalDir, "speech-tmp");
        _httpFactory = httpFactory ?? SafeSpeechHttp.CreateClient;
    }

    internal bool TryConsumeActionSlot(string clientKey) => _rateLimiter.TryAcquire(clientKey);

    internal static bool HasSpeechWidget(IEnumerable<PageConfig> pages)
    {
        foreach (var page in pages)
        {
            if (!page.Enabled) continue;
            foreach (var w in page.Widgets ?? [])
            {
                if (w.Type == "speech") return true;
                if (w.Type == "stack" && StackWidgets.Children(w).Any(c => c.Enabled && c.Type == "speech"))
                    return true;
            }
        }
        return false;
    }

    private static HashSet<string> CollectSpeechCanonicalIds(IEnumerable<PageConfig> pages)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var page in pages)
        {
            if (!page.Enabled) continue;
            foreach (var w in page.Widgets ?? [])
            {
                if (w.Type == "speech")
                    ids.Add($"{page.Id}/{w.Id}");
                else if (w.Type == "stack")
                {
                    foreach (var child in StackWidgets.Children(w))
                    {
                        if (child.Enabled && child.Type == "speech")
                            ids.Add($"{page.Id}/{w.Id}:{child.Id}");
                    }
                }
            }
        }
        return ids;
    }

    public SpeechStateDto GetState(bool isLan = false, string? deviceId = null)
    {
        var rootCfg = _store.Get();
        var cfg = rootCfg.Speech ?? new SpeechConfig();
        var now = DateTimeOffset.UtcNow;
        var scopedPages = isLan
            ? rootCfg.PagesFor(deviceId).Where(p => p.Enabled).ToList()
            : rootCfg.Pages;
        var allowedIds = isLan ? CollectSpeechCanonicalIds(scopedPages) : null;
        var widgetTargets = BuildWidgetTargets(scopedPages, cfg, now, isLan);
        var resolvedDefaultDir = ResolveTranscriptDirectory(cfg);
        var defaultDirDisplay = isLan ? FormatLanDisplayPath(resolvedDefaultDir) : FormatDisplayPath(resolvedDefaultDir);
        var appleAvailable = IsAppleAutoAvailable();

        lock (_stateLock)
        {
            var elapsed = _status == "recording"
                ? Math.Max(0, (int)(DateTimeOffset.UtcNow - _startedAt).TotalMilliseconds)
                : 0;
            var entriesQuery = allowedIds is null
                ? _entries
                : _entries.Where(e => allowedIds.Contains(e.WidgetId));
            var publicEntries = entriesQuery
                .Take(cfg.HistoryLimit)
                .Select(e => isLan
                    ? e with
                    {
                        SavedFile = null,
                        SavedFileDisplay = string.IsNullOrWhiteSpace(e.SavedFile) ? null : FormatLanDisplayPath(e.SavedFile),
                    }
                    : e)
                .ToList();
            var visibleActiveWidgetId = allowedIds is null || (_activeWidgetId is not null && allowedIds.Contains(_activeWidgetId))
                ? _activeWidgetId
                : null;

            return new SpeechStateDto(
                _status,
                visibleActiveWidgetId,
                _activeProvider,
                _startedAt == default ? 0 : _startedAt.ToUnixTimeMilliseconds(),
                elapsed,
                cfg.MaxRecordSec,
                cfg.Provider,
                appleAvailable,
                _lastError,
                publicEntries,
                cfg.SaveTranscripts,
                defaultDirDisplay,
                widgetTargets);
        }
    }

    private static IReadOnlyDictionary<string, SpeechWidgetSaveInfoDto> BuildWidgetTargets(
        IEnumerable<PageConfig> pages,
        SpeechConfig speechCfg,
        DateTimeOffset now,
        bool isLan)
    {
        var map = new Dictionary<string, SpeechWidgetSaveInfoDto>(StringComparer.Ordinal);
        void AddTarget(string key, Dictionary<string, JsonElement>? opts)
        {
            var ws = SpeechWidgetSettings.Parse(opts);
            var saveOn = ws.SaveTranscript == "always"
                || (ws.SaveTranscript == "default" && speechCfg.SaveTranscripts);
            map[key] = new SpeechWidgetSaveInfoDto(saveOn, PreviewTranscriptTarget(speechCfg, ws.TranscriptDir, now, isLan));
        }

        foreach (var page in pages)
        {
            foreach (var w in page.Widgets ?? [])
            {
                if (w.Type == "speech")
                {
                    AddTarget($"{page.Id}/{w.Id}", w.Options);
                }
                else if (w.Type == "stack")
                {
                    foreach (var child in StackWidgets.Children(w))
                    {
                        if (child.Type == "speech")
                            AddTarget($"{page.Id}/{w.Id}:{child.Id}", child.Options);
                    }
                }
            }
        }
        return map;
    }

    private sealed record ResolvedPhoneWidget(
        bool Ok,
        int ErrorStatus,
        string? ErrorMessage,
        string CanonicalId,
        SpeechWidgetSettings Settings,
        IReadOnlySet<string>? AllowedPageIds);

    private static ResolvedPhoneWidget ResolvePhoneWidget(AgentConfig rootCfg, string? widgetId, bool isLan, string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(widgetId))
            return new ResolvedPhoneWidget(false, 400, "widgetId is required.", "", SpeechWidgetSettings.Parse(null), null);

        List<PageConfig>? scopedPages = null;
        HashSet<string>? allowedPageIds = null;
        if (isLan)
        {
            scopedPages = rootCfg.PagesFor(deviceId).Where(p => p.Enabled).ToList();
            allowedPageIds = scopedPages.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        }

        var res = ResolveWidgetSettings(rootCfg, widgetId.Trim(), scopedPages);
        if (res.Ambiguous)
            return new ResolvedPhoneWidget(false, 400, "Ambiguous speech widget ID; use '<pageId>/<widgetId>'.", "", res.Settings, allowedPageIds);
        if (!res.Found)
        {
            return isLan
                ? new ResolvedPhoneWidget(false, 403, "Speech widget is not assigned to this device.", "", res.Settings, allowedPageIds)
                : new ResolvedPhoneWidget(false, 404, "Speech widget was not found.", "", res.Settings, allowedPageIds);
        }
        if (!res.Enabled || !res.Settings.Control)
            return new ResolvedPhoneWidget(false, 403, "Widget control is disabled in Settings.", res.CanonicalId, res.Settings, allowedPageIds);

        return new ResolvedPhoneWidget(true, 200, null, res.CanonicalId, res.Settings, allowedPageIds);
    }

    public async Task<SpeechActionResult> StartAsync(string? widgetId, bool isLan = false, string? deviceId = null, CancellationToken ct = default)
    {
        var rootCfg = _store.Get();
        var speechCfg = rootCfg.Speech ?? new SpeechConfig();
        if (!speechCfg.Enabled)
            return Fail(404, "Speech to text is disabled in Settings.", isLan, deviceId);

        var target = ResolvePhoneWidget(rootCfg, widgetId, isLan, deviceId);
        if (!target.Ok)
            return Fail(target.ErrorStatus, target.ErrorMessage!, isLan, deviceId);

        var (provider, providerErr) = ResolveProvider(speechCfg, target.Settings);
        if (provider is null)
            return Fail(400, providerErr ?? "No speech provider configured.", isLan, deviceId);

        await _opGate.WaitAsync(ct);
        CancellationTokenSource? cts = null;
        IActiveRecording? rec = null;
        try
        {
            long opId;
            lock (_stateLock)
            {
                if (_status != "idle")
                    return Fail(409, $"Speech capture is already {_status}.", isLan, deviceId);
                opId = ++_activeOpId;
            }

            SpeechStoragePermissions.EnsurePrivateDir(_tempRoot);
            var wavPath = Path.Combine(_tempRoot, $"rec-{Guid.NewGuid():N}.wav");
            cts = new CancellationTokenSource();
            using var linkedStartCts = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);

            rec = await _capture.StartAsync(
                wavPath,
                speechCfg.MaxRecordSec,
                onMaxReached: () => _ = Task.Run(() => StopForMaxDurationAsync(target.CanonicalId, opId)),
                linkedStartCts.Token);

            lock (_stateLock)
            {
                _status = "recording";
                _activeWidgetId = target.CanonicalId;
                _activeProvider = provider;
                _startedAt = DateTimeOffset.UtcNow;
                _lastError = null;
                _activeRecording = rec;
                _activeCts = cts;
            }
            cts = null;
            rec = null;

            EmitSpeechUpdate();
            return Ok(isLan, deviceId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            cts?.Dispose();
            if (rec is not null)
            {
                try { await rec.CancelAsync(); } catch { }
                try { await rec.DisposeAsync(); } catch { }
            }
            var safeErr = SanitizePhoneError(ex.Message, speechCfg, "Could not start speech recording.");
            lock (_stateLock)
            {
                _lastError = safeErr;
            }
            EmitSpeechUpdate();
            return Fail(400, safeErr, isLan, deviceId);
        }
        catch
        {
            cts?.Dispose();
            if (rec is not null)
            {
                try { await rec.CancelAsync(); } catch { }
                try { await rec.DisposeAsync(); } catch { }
            }
            throw;
        }
        finally
        {
            _opGate.Release();
        }
    }

    public Task<SpeechActionResult> StopAsync(string? widgetId, bool isLan = false, string? deviceId = null, CancellationToken ct = default) =>
        StopInternalAsync(widgetId, expectedOpId: null, isLan, deviceId, ct);

    private async Task StopForMaxDurationAsync(string widgetId, long expectedOpId)
    {
        try { await StopInternalAsync(widgetId, expectedOpId, isLan: false, deviceId: null, CancellationToken.None); }
        catch { }
    }

    private async Task<SpeechActionResult> StopInternalAsync(
        string? widgetId,
        long? expectedOpId,
        bool isLan,
        string? deviceId,
        CancellationToken ct)
    {
        var rootCfg = _store.Get();
        var speechCfg = rootCfg.Speech ?? new SpeechConfig();
        if (!speechCfg.Enabled)
            return Fail(404, "Speech to text is disabled in Settings.", isLan, deviceId);

        var target = ResolvePhoneWidget(rootCfg, widgetId, isLan, deviceId);
        if (!target.Ok)
            return Fail(target.ErrorStatus, target.ErrorMessage!, isLan, deviceId);

        var rawCallerId = widgetId!.Trim();
        Task? pendingJob = null;

        await _opGate.WaitAsync(ct);
        try
        {
            IActiveRecording? rec;
            CancellationTokenSource? cts;
            long opId;
            string currentWidgetId;
            string provider;
            DateTimeOffset started;

            lock (_stateLock)
            {
                if (expectedOpId is not null && _activeOpId != expectedOpId.Value)
                    return Fail(409, "Recording session has already changed.", isLan, deviceId);

                if (_status == "transcribing" && _transcriptionTask is not null)
                {
                    if (!WidgetIdMatches(_activeWidgetId, rawCallerId, target.CanonicalId))
                        return Fail(409, "Another widget is currently transcribing.", isLan, deviceId);
                    pendingJob = _transcriptionTask;
                    rec = null;
                    cts = null;
                    opId = _activeOpId;
                    currentWidgetId = _activeWidgetId ?? target.CanonicalId;
                    provider = _activeProvider ?? "";
                    started = _startedAt;
                }
                else if (_status != "recording" || _activeRecording is null)
                {
                    return Fail(409, "No active recording to stop.", isLan, deviceId);
                }
                else if (!WidgetIdMatches(_activeWidgetId, rawCallerId, target.CanonicalId))
                {
                    return Fail(409, "Another widget is currently recording.", isLan, deviceId);
                }
                else
                {
                    rec = _activeRecording;
                    cts = _activeCts;
                    opId = _activeOpId;
                    _activeRecording = null;
                    currentWidgetId = _activeWidgetId ?? target.CanonicalId;
                    provider = _activeProvider ?? "auto";
                    started = _startedAt;
                    _status = "transcribing";
                }
            }

            if (rec is not null)
            {
                var durationMs = Math.Max(0, (int)(DateTimeOffset.UtcNow - started).TotalMilliseconds);
                string? stopErr;
                try
                {
                    stopErr = await rec.StopAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    TryDeleteFile(rec.WavPath);
                    lock (_stateLock)
                    {
                        if (_activeOpId == opId)
                        {
                            _status = "idle";
                            _activeWidgetId = null;
                            _activeProvider = null;
                            _startedAt = default;
                            _activeCts = null;
                        }
                    }
                    try { cts?.Cancel(); } catch { }
                    try { cts?.Dispose(); } catch { }
                    EmitSpeechUpdate();
                    throw;
                }
                catch (Exception ex)
                {
                    TryDeleteFile(rec.WavPath);
                    var errMsg = SanitizePhoneError(ex.Message, speechCfg, "Audio capture stopped with an error.");
                    lock (_stateLock)
                    {
                        if (_activeOpId == opId)
                        {
                            _status = "idle";
                            _activeWidgetId = null;
                            _activeProvider = null;
                            _startedAt = default;
                            _lastError = errMsg;
                            _activeCts = null;
                        }
                    }
                    try { cts?.Cancel(); } catch { }
                    try { cts?.Dispose(); } catch { }
                    EmitSpeechUpdate();
                    return Fail(500, errMsg, isLan, deviceId);
                }
                finally
                {
                    await rec.DisposeAsync();
                }

                if (stopErr is not null)
                {
                    TryDeleteFile(rec.WavPath);
                    var safeStopErr = SanitizePhoneError(stopErr, speechCfg, "No audio was captured.");
                    lock (_stateLock)
                    {
                        if (_activeOpId == opId)
                        {
                            _status = "idle";
                            _activeWidgetId = null;
                            _activeProvider = null;
                            _startedAt = default;
                            _lastError = safeStopErr;
                            _activeCts = null;
                        }
                    }
                    try { cts?.Cancel(); } catch { }
                    try { cts?.Dispose(); } catch { }
                    EmitSpeechUpdate();
                    return Fail(400, safeStopErr, isLan, deviceId);
                }

                EmitSpeechUpdate();
                pendingJob = RunTranscriptionJobAsync(
                    rec.WavPath,
                    currentWidgetId,
                    provider,
                    durationMs,
                    speechCfg,
                    target.Settings,
                    target.AllowedPageIds,
                    opId,
                    cts);

                lock (_stateLock)
                {
                    if (_activeOpId == opId)
                        _transcriptionTask = pendingJob;
                }
            }
        }
        finally
        {
            _opGate.Release();
        }

        if (pendingJob is not null)
        {
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            await Task.WhenAny(pendingJob, Task.Delay(FastStopWaitMs, waitCts.Token));
        }

        return Ok(isLan, deviceId);
    }

    public async Task<SpeechActionResult> CancelAsync(string? widgetId, bool isLan = false, string? deviceId = null, CancellationToken ct = default)
    {
        var rootCfg = _store.Get();
        var speechCfg = rootCfg.Speech ?? new SpeechConfig();
        if (!speechCfg.Enabled)
            return Fail(404, "Speech to text is disabled in Settings.", isLan, deviceId);

        var target = ResolvePhoneWidget(rootCfg, widgetId, isLan, deviceId);
        if (!target.Ok)
            return Fail(target.ErrorStatus, target.ErrorMessage!, isLan, deviceId);

        var rawCallerId = widgetId!.Trim();
        IActiveRecording? rec;
        CancellationTokenSource? cts;
        bool wasTranscribing;

        await _opGate.WaitAsync(ct);
        try
        {
            lock (_stateLock)
            {
                if (_status != "idle" && !WidgetIdMatches(_activeWidgetId, rawCallerId, target.CanonicalId))
                    return Fail(409, "Another widget is currently active.", isLan, deviceId);

                ++_activeOpId;
                rec = _activeRecording;
                cts = _activeCts;
                wasTranscribing = _transcriptionTask is not null && rec is null;
                _activeRecording = null;
                _activeCts = null;
                _transcriptionTask = null;
                _status = "idle";
                _activeWidgetId = null;
                _activeProvider = null;
                _startedAt = default;
                _lastError = null;
            }
        }
        finally
        {
            _opGate.Release();
        }

        await CleanupCancelledSessionAsync(rec, cts, wasTranscribing);
        EmitSpeechUpdate();
        return Ok(isLan, deviceId);
    }

    internal async Task CancelAllForShutdownAsync()
    {
        IActiveRecording? rec;
        CancellationTokenSource? cts;
        bool wasTranscribing;
        await _opGate.WaitAsync();
        try
        {
            lock (_stateLock)
            {
                ++_activeOpId;
                rec = _activeRecording;
                cts = _activeCts;
                wasTranscribing = _transcriptionTask is not null && rec is null;
                _activeRecording = null;
                _activeCts = null;
                _transcriptionTask = null;
                _status = "idle";
                _activeWidgetId = null;
                _activeProvider = null;
                _startedAt = default;
                _lastError = null;
            }
        }
        finally
        {
            _opGate.Release();
        }

        await CleanupCancelledSessionAsync(rec, cts, wasTranscribing);
        EmitSpeechUpdate();
    }

    private static async Task CleanupCancelledSessionAsync(IActiveRecording? rec, CancellationTokenSource? cts, bool wasTranscribing)
    {
        try { cts?.Cancel(); } catch { }
        if (rec is not null)
        {
            try { cts?.Dispose(); } catch { }
            await rec.CancelAsync();
            await rec.DisposeAsync();
        }
        else if (!wasTranscribing)
        {
            try { cts?.Dispose(); } catch { }
        }
    }

    public SpeechActionResult Clear(string? widgetId, bool isLan = false, string? deviceId = null)
    {
        var rootCfg = _store.Get();
        var speechCfg = rootCfg.Speech ?? new SpeechConfig();
        if (!speechCfg.Enabled)
            return Fail(404, "Speech to text is disabled in Settings.", isLan, deviceId);

        var target = ResolvePhoneWidget(rootCfg, widgetId, isLan, deviceId);
        if (!target.Ok)
            return Fail(target.ErrorStatus, target.ErrorMessage!, isLan, deviceId);

        var filterRaw = widgetId!.Trim();
        lock (_stateLock)
        {
            _entries.RemoveAll(e => WidgetIdMatches(e.WidgetId, filterRaw, target.CanonicalId));
            _lastError = null;
        }

        EmitSpeechUpdate();
        return Ok(isLan, deviceId);
    }

    public SpeechActionResult AppendEntryToNote(
        string? entryId,
        string? widgetId,
        string? targetOverride = null,
        bool isLan = false,
        string? deviceId = null)
    {
        var rootCfg = _store.Get();
        var speechCfg = rootCfg.Speech ?? new SpeechConfig();
        if (!speechCfg.Enabled)
            return Fail(404, "Speech to text is disabled in Settings.", isLan, deviceId);

        if (string.IsNullOrWhiteSpace(entryId))
            return Fail(400, "entryId is required.", isLan, deviceId);

        var target = ResolvePhoneWidget(rootCfg, widgetId, isLan, deviceId);
        if (!target.Ok)
            return Fail(target.ErrorStatus, target.ErrorMessage!, isLan, deviceId);

        var rawCallerId = widgetId!.Trim();
        SpeechEntryDto? entry;
        lock (_stateLock)
        {
            entry = _entries.FirstOrDefault(e =>
                string.Equals(e.Id, entryId.Trim(), StringComparison.Ordinal)
                && WidgetIdMatches(e.WidgetId, rawCallerId, target.CanonicalId));
        }
        if (entry is null)
            return Fail(404, "Transcript entry not found.", isLan, deviceId);

        var spec = !string.IsNullOrWhiteSpace(targetOverride)
            ? targetOverride.Trim()
            : target.Settings.NoteTarget;
        if (string.IsNullOrWhiteSpace(spec))
            return Fail(400, "No target Note widget is configured.", isLan, deviceId);

        var (ok, resolvedTarget, err) = AppendToNoteWidget(
            entry.Text,
            spec,
            target.CanonicalId,
            target.Settings.NoteMode,
            entry.CreatedAt,
            target.AllowedPageIds);
        if (!ok)
            return Fail(400, err ?? "Could not append transcript to Note widget.", isLan, deviceId);

        lock (_stateLock)
        {
            var idx = _entries.FindIndex(e => e.Id == entry.Id);
            if (idx >= 0)
                _entries[idx] = _entries[idx] with { AppendedToNote = true, NoteTarget = resolvedTarget };
        }

        EmitSpeechUpdate();
        return Ok(isLan, deviceId);
    }

    public async Task<SpeechSetupStatusDto> GetSetupStatusAsync(CancellationToken ct = default)
    {
        var cfg = _store.Get().Speech ?? new SpeechConfig();
        AppleSpeechStatus? apple = null;
        if (_appleHelper.IsSupported)
        {
            try { apple = await _appleHelper.GetStatusAsync(cfg.Language, ct); }
            catch (Exception ex) { apple = new AppleSpeechStatus(false, false, "error", "error", ex.Message); }
        }

        return new SpeechSetupStatusDto(
            OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : "linux",
            OperatingSystem.IsMacOS(),
            apple,
            ToolProcess.Find("ffmpeg") is not null,
            _capture.HasPlatformCapture,
            GetDefaultTranscriptDirectory(),
            cfg.SaveTranscripts,
            ResolveTranscriptDirectory(cfg));
    }

    private bool IsOpActive(long opId)
    {
        lock (_stateLock)
            return _activeOpId == opId;
    }

    private async Task RunTranscriptionJobAsync(
        string wavPath,
        string widgetId,
        string provider,
        int durationMs,
        SpeechConfig cfg,
        SpeechWidgetSettings wSettings,
        IReadOnlySet<string>? allowedPageIds,
        long opId,
        CancellationTokenSource? jobCts)
    {
        var ct = jobCts?.Token ?? CancellationToken.None;
        var effectiveLang = !string.IsNullOrWhiteSpace(wSettings.Language) ? wSettings.Language : cfg.Language;

        try
        {
            var text = await TranscribeWithProviderAsync(provider, wavPath, effectiveLang, cfg, ct);
            ct.ThrowIfCancellationRequested();
            if (!IsOpActive(opId)) return;

            var trimmed = text.Trim();
            var now = DateTimeOffset.UtcNow;
            var entryId = Guid.NewGuid().ToString("N")[..12];

            string? savedFile = null;
            string? postWarning = null;
            var appendedToNote = false;
            string? resolvedNoteTarget = string.IsNullOrWhiteSpace(wSettings.NoteTarget) ? null : wSettings.NoteTarget;

            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                var shouldSave = wSettings.SaveTranscript == "always"
                    || (wSettings.SaveTranscript == "default" && cfg.SaveTranscripts);
                if (shouldSave && IsOpActive(opId))
                {
                    try
                    {
                        savedFile = await SaveTranscriptToDiskAsync(
                            entryId, trimmed, provider, effectiveLang, durationMs, now, cfg, wavPath, ct, wSettings.TranscriptDir);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        postWarning = "Transcript kept in memory, but disk save failed.";
                    }
                }
            }
            else
            {
                postWarning = "No speech detected in recording.";
            }

            if (!IsOpActive(opId))
            {
                if (!string.IsNullOrWhiteSpace(savedFile) && cfg.TranscriptFormat == "per-item-txt")
                    TryDeleteFile(savedFile);
                return;
            }

            try
            {
                await _opGate.WaitAsync(CancellationToken.None);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                if (!IsOpActive(opId))
                {
                    if (!string.IsNullOrWhiteSpace(savedFile) && cfg.TranscriptFormat == "per-item-txt")
                        TryDeleteFile(savedFile);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(trimmed)
                    && wSettings.AutoAppendNote
                    && !string.IsNullOrWhiteSpace(wSettings.NoteTarget))
                {
                    var (ok, actualTarget, noteErr) = AppendToNoteWidget(
                        trimmed, wSettings.NoteTarget, widgetId, wSettings.NoteMode, now, allowedPageIds);
                    if (ok)
                    {
                        appendedToNote = true;
                        resolvedNoteTarget = actualTarget;
                    }
                    else if (!string.IsNullOrWhiteSpace(noteErr))
                    {
                        postWarning = string.IsNullOrWhiteSpace(postWarning)
                            ? noteErr
                            : $"{postWarning} ({noteErr})";
                    }
                }

                var savedFileDisplay = string.IsNullOrWhiteSpace(savedFile) ? null : FormatDisplayPath(savedFile);
                var entry = new SpeechEntryDto(
                    entryId, trimmed, provider, effectiveLang, now, durationMs,
                    savedFile, resolvedNoteTarget, appendedToNote, widgetId, savedFileDisplay);

                var maxKeep = Math.Clamp(cfg.HistoryLimit, 1, 50);
                lock (_stateLock)
                {
                    if (_activeOpId != opId) return;

                    if (!string.IsNullOrWhiteSpace(trimmed))
                    {
                        _entries.Insert(0, entry);
                        if (_entries.Count > maxKeep)
                            _entries.RemoveRange(maxKeep, _entries.Count - maxKeep);
                    }
                    if (!string.IsNullOrWhiteSpace(postWarning))
                        _lastError = postWarning;
                    _status = "idle";
                    _activeWidgetId = null;
                    _activeProvider = null;
                    _startedAt = default;
                    _transcriptionTask = null;
                    _activeCts = null;
                }
            }
            finally
            {
                _opGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            lock (_stateLock)
            {
                if (_activeOpId == opId)
                {
                    _status = "idle";
                    _activeWidgetId = null;
                    _activeProvider = null;
                    _startedAt = default;
                    _transcriptionTask = null;
                    _activeCts = null;
                }
            }
        }
        catch (Exception ex)
        {
            var errMsg = SanitizePhoneError(ex.Message, cfg, "Speech transcription failed.");
            lock (_stateLock)
            {
                if (_activeOpId == opId)
                {
                    _status = "idle";
                    _activeWidgetId = null;
                    _activeProvider = null;
                    _startedAt = default;
                    _lastError = errMsg;
                    _transcriptionTask = null;
                    _activeCts = null;
                }
            }
        }
        finally
        {
            try { jobCts?.Dispose(); } catch { }
            TryDeleteFile(wavPath);
            EmitSpeechUpdate();
        }
    }

    internal static (bool Found, bool Ambiguous, bool Enabled, string CanonicalId, string PageId, SpeechWidgetSettings Settings) ResolveWidgetSettings(
        AgentConfig cfg,
        string widgetId,
        IReadOnlyList<PageConfig>? scopedPages = null)
    {
        var trimmed = (widgetId ?? "").Trim();
        if (string.IsNullOrEmpty(trimmed))
            return (false, false, false, "", "", SpeechWidgetSettings.Parse(null));

        var slashIdx = trimmed.IndexOf('/');
        string? wantedPageId = slashIdx > 0 ? trimmed[..slashIdx] : null;
        var localId = slashIdx > 0 ? trimmed[(slashIdx + 1)..] : trimmed;
        var colonIdx = localId.IndexOf(':');

        var sourcePages = scopedPages ?? cfg.Pages;
        var pages = wantedPageId is not null
            ? sourcePages.Where(p => string.Equals(p.Id, wantedPageId, StringComparison.Ordinal))
            : sourcePages.OrderByDescending(p => p.Enabled);

        (bool Enabled, string CanonicalId, string PageId, SpeechWidgetSettings Settings)? firstMatch = null;

        foreach (var page in pages)
        {
            foreach (var w in page.Widgets ?? [])
            {
                if (w.Type == "speech" && string.Equals(w.Id, localId, StringComparison.Ordinal))
                {
                    var match = (page.Enabled, $"{page.Id}/{w.Id}", page.Id, SpeechWidgetSettings.Parse(w.Options));
                    if (wantedPageId is not null)
                        return (true, false, match.Item1, match.Item2, match.Item3, match.Item4);
                    if (firstMatch is not null)
                        return (false, true, false, "", "", SpeechWidgetSettings.Parse(null));
                    firstMatch = match;
                }

                if (w.Type == "stack" && colonIdx > 0 && string.Equals(w.Id, localId[..colonIdx], StringComparison.Ordinal))
                {
                    var wantedChild = localId[(colonIdx + 1)..];
                    foreach (var child in StackWidgets.Children(w))
                    {
                        if (child.Type == "speech" && string.Equals(child.Id, wantedChild, StringComparison.Ordinal))
                        {
                            var match = (page.Enabled && child.Enabled, $"{page.Id}/{w.Id}:{child.Id}", page.Id, SpeechWidgetSettings.Parse(child.Options));
                            if (wantedPageId is not null)
                                return (true, false, match.Item1, match.Item2, match.Item3, match.Item4);
                            if (firstMatch is not null)
                                return (false, true, false, "", "", SpeechWidgetSettings.Parse(null));
                            firstMatch = match;
                        }
                    }
                }
            }
        }

        if (firstMatch is { } found)
            return (true, false, found.Enabled, found.CanonicalId, found.PageId, found.Settings);

        return (false, false, false, trimmed, wantedPageId ?? "", SpeechWidgetSettings.Parse(null));
    }

    internal static bool WidgetIdMatches(string? storedWidgetId, string callerWidgetId, string? callerCanonicalId)
    {
        if (string.IsNullOrWhiteSpace(storedWidgetId))
            return false;
        if (!string.IsNullOrWhiteSpace(callerCanonicalId))
            return string.Equals(storedWidgetId, callerCanonicalId, StringComparison.Ordinal);
        return !callerWidgetId.Contains('/')
            && string.Equals(storedWidgetId, callerWidgetId, StringComparison.Ordinal);
    }

    internal (string? Provider, string? Error) ResolveProvider(SpeechConfig cfg, SpeechWidgetSettings w)
    {
        var requested = !string.Equals(w.Provider, "default", StringComparison.Ordinal) ? w.Provider : cfg.Provider;

        switch (requested)
        {
            case "apple":
                if (!_appleHelper.IsSupported)
                    return (null, "Apple Speech Recognition is only supported on macOS.");
                return ("apple", null);

            case "openai":
                if (string.IsNullOrWhiteSpace(cfg.OpenAiApiKey))
                    return (null, "OpenAI API key is not configured.");
                return ("openai", null);

            case "auto":
                if (IsAppleAutoAvailable()) return ("apple", null);
                if (!string.IsNullOrWhiteSpace(cfg.OpenAiApiKey)) return ("openai", null);
                return (null, "No speech provider configured (enable Apple Speech on macOS or add an OpenAI API key in Settings).");

            default:
                return (null, $"Unsupported provider '{requested}'.");
        }
    }

    public async Task<SpeechTestResult> TestProviderAsync(SpeechConfig? candidate, CancellationToken ct = default)
    {
        var cfg = candidate ?? _store.Get().Speech ?? new SpeechConfig();
        cfg.Normalize();

        var dummyWidget = new SpeechWidgetSettings("Speech", "default", cfg.Language, "toggle", "", "append", false, "default", true);
        var (provider, err) = ResolveProvider(cfg, dummyWidget);
        if (provider is null)
            return new SpeechTestResult(false, cfg.Provider, err ?? "No provider configured.");

        try
        {
            return provider switch
            {
                "apple" => await TestAppleAsync(cfg, ct),
                "openai" => await TestOpenAiAsync(cfg, ct),
                _ => new SpeechTestResult(false, provider, $"Unsupported provider '{provider}'."),
            };
        }
        catch (Exception ex)
        {
            return new SpeechTestResult(false, provider, SanitizePhoneError(ex.Message, cfg));
        }
    }

    internal Task<string> TranscribeWithProviderAsync(
        string provider,
        string wavPath,
        string language,
        SpeechConfig cfg,
        CancellationToken ct)
    {
        return provider switch
        {
            "apple" => TranscribeAppleAsync(wavPath, language, cfg, ct),
            "openai" => TranscribeOpenAiAsync(wavPath, language, cfg, ct),
            _ => throw new InvalidOperationException($"Unsupported speech provider '{provider}'."),
        };
    }

    internal static string SanitizePhoneError(string? message, SpeechConfig cfg, string fallback = "Speech request failed.")
    {
        var result = string.IsNullOrWhiteSpace(message) ? fallback : message.Trim();
        foreach (var secret in cfg.EnumerateSecrets())
        {
            if (!string.IsNullOrWhiteSpace(secret) && result.Contains(secret, StringComparison.Ordinal))
                result = result.Replace(secret, "***", StringComparison.Ordinal);
        }
        result = LocalPathRegex.Replace(result, "[path]");
        if (!string.IsNullOrWhiteSpace(cfg.TranscriptDir))
            result = result.Replace(cfg.TranscriptDir, "[path]", StringComparison.OrdinalIgnoreCase);
        return result.Length <= 220 ? result : result[..220];
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private void EmitSpeechUpdate() => _push.Emit("speech");

    private SpeechActionResult Ok(bool isLan = false, string? deviceId = null) =>
        new(true, 200, null, GetState(isLan, deviceId));

    private SpeechActionResult Fail(int statusCode, string error, bool isLan = false, string? deviceId = null) =>
        new(false, statusCode, error, GetState(isLan, deviceId));

    public void Dispose()
    {
        CancellationTokenSource? cts;
        IActiveRecording? rec;
        bool wasTranscribing;
        lock (_stateLock)
        {
            ++_activeOpId;
            cts = _activeCts;
            rec = _activeRecording;
            wasTranscribing = _transcriptionTask is not null && rec is null;
            _activeCts = null;
            _activeRecording = null;
            _transcriptionTask = null;
        }
        try { cts?.Cancel(); } catch { }
        if (rec is not null || !wasTranscribing)
        {
            try { cts?.Dispose(); } catch { }
        }
        if (rec is not null)
        {
            try { rec.CancelAsync().GetAwaiter().GetResult(); } catch { }
            try { rec.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        }
        _opGate.Dispose();
        _diskGate.Dispose();
    }

    [GeneratedRegex(@"(?:[A-Za-z]:\\|/(?:Users|home|var|tmp|private|etc|System|Library)/)[^\s'""<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex LocalPathPattern();
}
