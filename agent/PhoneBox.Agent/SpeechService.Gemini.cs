using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

internal sealed partial class SpeechService
{
    private static readonly string[] GeminiCredentialHosts = ["generativelanguage.googleapis.com"];
    private static readonly Regex SafeModelIdRegex = SafeModelIdPattern();

    private async Task<SpeechTestResult> TestGeminiAsync(SpeechConfig cfg, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.GeminiApiKey))
            return new SpeechTestResult(false, "gemini", "Gemini API key is missing.");
        if (!SafeModelIdRegex.IsMatch(cfg.GeminiModel))
            return new SpeechTestResult(false, "gemini", "Invalid Gemini model identifier.");

        var uri = new Uri($"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(cfg.GeminiModel)}");
        var blocked = GetBlockedPorts();
        using var client = _httpFactory(false, blocked, TimeSpan.FromSeconds(10));
        using var res = await SafeSpeechHttp.SendWithSafeRedirectsAsync(client, (u, auth) =>
        {
            var r = new HttpRequestMessage(HttpMethod.Get, u);
            if (auth) r.Headers.TryAddWithoutValidation("x-goog-api-key", cfg.GeminiApiKey);
            return r;
        }, uri, false, blocked, GeminiCredentialHosts, ct);

        return res.IsSuccessStatusCode
            ? new SpeechTestResult(true, "gemini", $"Gemini model '{cfg.GeminiModel}' reachable.")
            : new SpeechTestResult(false, "gemini", $"Gemini returned HTTP {(int)res.StatusCode}.");
    }

    private async Task<string> TranscribeGeminiAsync(string wavPath, string language, SpeechConfig cfg, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.GeminiApiKey))
            throw new InvalidOperationException("Gemini API key is required.");
        if (!SafeModelIdRegex.IsMatch(cfg.GeminiModel))
            throw new InvalidOperationException("Invalid Gemini model name.");

        var wavBytes = await SafeSpeechHttp.ReadBoundedAudioBytesAsync(wavPath, ct);
        var b64 = Convert.ToBase64String(wavBytes);
        var prompt = BuildSttPrompt(language);

        var payload = JsonSerializer.Serialize(new
        {
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new { text = prompt },
                        new { inlineData = new { mimeType = "audio/wav", data = b64 } },
                    },
                },
            },
            generationConfig = new { temperature = 0 },
        });

        var uri = new Uri($"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(cfg.GeminiModel)}:generateContent");
        var blocked = GetBlockedPorts();

        using var client = _httpFactory(false, blocked, TimeSpan.FromSeconds(120));
        using var res = await SafeSpeechHttp.SendWithSafeRedirectsAsync(client, (u, auth) =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, u)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            if (auth) req.Headers.TryAddWithoutValidation("x-goog-api-key", cfg.GeminiApiKey);
            return req;
        }, uri, false, blocked, GeminiCredentialHosts, ct);

        var body = await SafeSpeechHttp.ReadBoundedStringAsync(res.Content, ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Gemini error (HTTP {(int)res.StatusCode}): {ExtractErrorMessage(body)}");

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("candidates", out var cands) && cands.ValueKind == JsonValueKind.Array)
        {
            foreach (var cand in cands.EnumerateArray())
            {
                if (!cand.TryGetProperty("content", out var content)
                    || !content.TryGetProperty("parts", out var parts)
                    || parts.ValueKind != JsonValueKind.Array) continue;

                var sb = new StringBuilder();
                foreach (var p in parts.EnumerateArray())
                {
                    if (p.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                        sb.Append(t.GetString());
                }
                if (sb.Length > 0) return sb.ToString().Trim();
            }
        }
        return "";
    }

    private static string BuildSttPrompt(string language) => string.IsNullOrWhiteSpace(language)
        ? "Transcribe the spoken audio verbatim. Return only the transcribed text with no commentary, timestamps, or markdown formatting."
        : $"Transcribe the spoken audio verbatim in language '{language}'. Return only the transcribed text with no commentary or markdown formatting.";

    [GeneratedRegex(@"^[a-zA-Z0-9._-]+$")]
    private static partial Regex SafeModelIdPattern();
}
