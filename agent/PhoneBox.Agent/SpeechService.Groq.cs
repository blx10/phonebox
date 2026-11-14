namespace PhoneBox.Agent;

internal sealed partial class SpeechService
{
    private static readonly string[] GroqCredentialHosts = ["api.groq.com"];
    private static readonly Uri GroqTranscribeUri = new("https://api.groq.com/openai/v1/audio/transcriptions");
    private static readonly string GroqModelsUrl = "https://api.groq.com/openai/v1/models";

    private Task<string> TranscribeGroqAsync(string wavPath, string language, SpeechConfig cfg, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.GroqApiKey))
            throw new InvalidOperationException("Groq API key is required.");

        return PostAudioMultipartAsync(
            GroqTranscribeUri,
            cfg.GroqApiKey,
            cfg.GroqModel,
            language,
            wavPath,
            allowLoopback: false,
            GroqCredentialHosts,
            ct);
    }

    private Task<SpeechTestResult> TestGroqAsync(SpeechConfig cfg, CancellationToken ct) =>
        TestBearerModelsEndpointAsync("groq", GroqModelsUrl, cfg.GroqApiKey, allowLoopback: false, GroqCredentialHosts, ct);
}
