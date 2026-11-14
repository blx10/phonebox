namespace PhoneBox.Agent;

internal sealed partial class SpeechService
{
    internal (string? Provider, string? Error) ResolveProvider(SpeechConfig cfg, SpeechWidgetSettings w)
    {
        var requested = !string.Equals(w.Provider, "default", StringComparison.Ordinal) ? w.Provider : cfg.Provider;

        if (string.Equals(requested, "apple", StringComparison.Ordinal))
        {
            if (!_appleHelper.IsSupported)
                return (null, "Apple Speech Recognition is only supported on macOS.");
            return ("apple", null);
        }

        if (string.Equals(requested, "auto", StringComparison.Ordinal))
        {
            if (_appleHelper.IsSupported && (_appleHelper is not SpeechAppleHelper || File.Exists("/usr/bin/swiftc") || ToolProcess.Find("swiftc") is not null))
                return ("apple", null);
            return (null, "Apple Speech Recognition is not available on this computer.");
        }

        return (null, $"Unsupported provider '{requested}'.");
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
            if (provider == "apple")
            {
                var st = await _appleHelper.GetStatusAsync(cfg.Language, ct);
                if (!string.IsNullOrWhiteSpace(st.Error))
                    return new SpeechTestResult(false, "apple", st.Error);
                if (!st.Available)
                    return new SpeechTestResult(false, "apple", $"Apple Speech is currently unavailable (speech auth: {st.SpeechAuth}, mic auth: {st.MicAuth}).");
                var onDevNote = cfg.AppleOnDevice && !st.OnDeviceSupported ? " (note: on-device model missing for this locale)" : "";
                return new SpeechTestResult(cfg.AppleOnDevice ? st.OnDeviceSupported : true, "apple", $"Apple Speech ready (speech: {st.SpeechAuth}, mic: {st.MicAuth}{onDevNote}).");
            }

            return new SpeechTestResult(false, provider, $"Unsupported provider '{provider}'.");
        }
        catch (Exception ex)
        {
            return new SpeechTestResult(false, provider, SanitizePhoneError(ex.Message, cfg));
        }
    }

    internal async Task<string> TranscribeWithProviderAsync(
        string provider,
        string wavPath,
        string language,
        SpeechConfig cfg,
        CancellationToken ct)
    {
        if (provider == "apple")
        {
            var (appleText, appleErr) = await _appleHelper.TranscribeAsync(wavPath, language, cfg.AppleOnDevice, ct);
            if (appleErr is not null) throw new InvalidOperationException(appleErr);
            return appleText ?? "";
        }

        throw new InvalidOperationException($"Unsupported speech provider '{provider}'.");
    }
}
