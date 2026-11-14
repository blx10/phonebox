using System.Net.Http.Headers;
using System.Text.Json;

namespace PhoneBox.Agent;

internal sealed partial class SpeechService
{
    private static readonly string[] OpenAiCredentialHosts = ["api.openai.com"];
    private static readonly Uri OpenAiTranscribeUri = new("https://api.openai.com/v1/audio/transcriptions");
    private static readonly string OpenAiModelsUrl = "https://api.openai.com/v1/models";

    private Task<string> TranscribeOpenAiAsync(string wavPath, string language, SpeechConfig cfg, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.OpenAiApiKey))
            throw new InvalidOperationException("OpenAI API key is required.");

        return PostAudioMultipartAsync(
            OpenAiTranscribeUri,
            cfg.OpenAiApiKey,
            cfg.OpenAiModel,
            language,
            wavPath,
            allowLoopback: false,
            OpenAiCredentialHosts,
            ct);
    }

    private Task<SpeechTestResult> TestOpenAiAsync(SpeechConfig cfg, CancellationToken ct) =>
        TestBearerModelsEndpointAsync("openai", OpenAiModelsUrl, cfg.OpenAiApiKey, allowLoopback: false, OpenAiCredentialHosts, ct);

    private IReadOnlyCollection<int> GetBlockedPorts()
    {
        var lanPort = _store.Get().Lan?.Port ?? 0;
        return lanPort > 0 && lanPort != _endpoint.Port ? [_endpoint.Port, lanPort] : [_endpoint.Port];
    }

    private async Task<string> PostAudioMultipartAsync(
        Uri endpointUri,
        string? bearerToken,
        string model,
        string language,
        string wavPath,
        bool allowLoopback,
        IReadOnlyCollection<string>? allowedCredentialHosts,
        CancellationToken ct)
    {
        var bytes = await SafeSpeechHttp.ReadBoundedAudioBytesAsync(wavPath, ct);
        var blocked = GetBlockedPorts();
        var langIso2 = ToIso2Language(language);

        using var client = _httpFactory(allowLoopback, blocked, TimeSpan.FromSeconds(120));
        using var res = await SafeSpeechHttp.SendWithSafeRedirectsAsync(client, (uri, sendAuth) =>
        {
            var form = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(bytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(fileContent, "file", "speech.wav");
            form.Add(new StringContent(model), "model");
            if (!string.IsNullOrWhiteSpace(langIso2))
                form.Add(new StringContent(langIso2), "language");

            var req = new HttpRequestMessage(HttpMethod.Post, uri) { Content = form };
            if (sendAuth && !string.IsNullOrWhiteSpace(bearerToken))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            return req;
        }, endpointUri, allowLoopback, blocked, allowedCredentialHosts, ct);

        var body = await SafeSpeechHttp.ReadBoundedStringAsync(res.Content, ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Transcription failed (HTTP {(int)res.StatusCode}): {ExtractErrorMessage(body)}");

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                return t.GetString() ?? "";
        }
        catch (JsonException) { }

        return body.Trim();
    }

    private async Task<SpeechTestResult> TestBearerModelsEndpointAsync(
        string provider,
        string url,
        string apiKey,
        bool allowLoopback,
        IReadOnlyCollection<string>? allowedCredentialHosts,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return new SpeechTestResult(false, provider, $"{provider} API key is missing.");

        var blocked = GetBlockedPorts();
        using var client = _httpFactory(allowLoopback, blocked, TimeSpan.FromSeconds(10));
        using var res = await SafeSpeechHttp.SendWithSafeRedirectsAsync(client, (u, auth) =>
        {
            var r = new HttpRequestMessage(HttpMethod.Get, u);
            if (auth) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            return r;
        }, new Uri(url), allowLoopback, blocked, allowedCredentialHosts, ct);

        return res.IsSuccessStatusCode
            ? new SpeechTestResult(true, provider, $"{provider} API key verified.")
            : new SpeechTestResult(false, provider, $"{provider} API returned HTTP {(int)res.StatusCode}.");
    }

    private static string? ToIso2Language(string? language)
    {
        var s = (language ?? "").Trim();
        if (string.IsNullOrEmpty(s)) return null;
        var sep = s.IndexOfAny(['-', '_']);
        var code = (sep > 0 ? s[..sep] : s).ToLowerInvariant();
        return code.Length is >= 2 and <= 3 ? code : null;
    }

    private static string ExtractErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "empty response";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.String) return err.GetString() ?? body;
                if (err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                    return m.GetString() ?? body;
            }
        }
        catch { }
        return body.Length <= 220 ? body.Trim() : body[..220].Trim();
    }
}
