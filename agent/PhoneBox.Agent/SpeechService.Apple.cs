namespace PhoneBox.Agent;

internal sealed partial class SpeechService
{
    private bool IsAppleAutoAvailable() =>
        _appleHelper.IsSupported
        && (_appleHelper is not SpeechAppleHelper || File.Exists("/usr/bin/swiftc") || ToolProcess.Find("swiftc") is not null);

    private async Task<SpeechTestResult> TestAppleAsync(SpeechConfig cfg, CancellationToken ct)
    {
        var st = await _appleHelper.GetStatusAsync(cfg.Language, ct);
        if (!string.IsNullOrWhiteSpace(st.Error))
            return new SpeechTestResult(false, "apple", st.Error);
        if (!st.Available)
            return new SpeechTestResult(false, "apple", $"Apple Speech is currently unavailable (speech auth: {st.SpeechAuth}, mic auth: {st.MicAuth}).");
        var onDevNote = cfg.AppleOnDevice && !st.OnDeviceSupported ? " (note: on-device model missing for this locale)" : "";
        return new SpeechTestResult(cfg.AppleOnDevice ? st.OnDeviceSupported : true, "apple", $"Apple Speech ready (speech: {st.SpeechAuth}, mic: {st.MicAuth}{onDevNote}).");
    }

    private async Task<string> TranscribeAppleAsync(string wavPath, string language, SpeechConfig cfg, CancellationToken ct)
    {
        var (appleText, appleErr) = await _appleHelper.TranscribeAsync(wavPath, language, cfg.AppleOnDevice, ct);
        if (appleErr is not null) throw new InvalidOperationException(appleErr);
        return appleText ?? "";
    }
}
