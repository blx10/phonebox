using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PhoneBox.Agent;

internal sealed partial class SpeechService
{
    private const string OllamaUnsupportedError =
        "Native Ollama /api/chat does not accept audio input; switch Local HTTP protocol to OpenAI Audio (/audio/transcriptions) or OpenAI Chat (/chat/completions).";

    private async Task<SpeechTestResult> TestLocalHttpAsync(SpeechConfig cfg, CancellationToken ct)
    {
        if (!cfg.AllowLocalServer)
            return new SpeechTestResult(false, "local-http", "Local HTTP server is disabled in Settings (enable Allow connections to local STT server).");

        var blocked = GetBlockedPorts();
        if (!SafeSpeechHttp.TryValidateLocalEndpoint(cfg.LocalBaseUrl, blocked, out var baseUri, out var isLoopback, out var urlErr) || baseUri is null)
            return new SpeechTestResult(false, "local-http", urlErr ?? "Invalid local server URL.");

        var probeUri = cfg.LocalProtocol == "ollama"
            ? BuildProbeEndpoint(baseUri, "/api/tags", "/api/chat", "/api/generate", "/api/tags", "/api")
            : BuildProbeEndpoint(baseUri, "/models", "/audio/transcriptions", "/chat/completions", "/models");

        IReadOnlyCollection<string>? credHosts = isLoopback ? null : [baseUri.Host];
        using var client = _httpFactory(isLoopback, blocked, TimeSpan.FromSeconds(8));
        using var res = await SafeSpeechHttp.SendWithSafeRedirectsAsync(client, (u, auth) =>
        {
            var r = new HttpRequestMessage(HttpMethod.Get, u);
            if (auth && !string.IsNullOrWhiteSpace(cfg.LocalApiKey))
                r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.LocalApiKey);
            return r;
        }, probeUri, isLoopback, blocked, credHosts, ct);

        if (!res.IsSuccessStatusCode)
            return new SpeechTestResult(false, "local-http", $"Server returned HTTP {(int)res.StatusCode} from {probeUri.GetLeftPart(UriPartial.Path)}.");

        if (cfg.LocalProtocol == "ollama")
            return new SpeechTestResult(false, "local-http", OllamaUnsupportedError);

        return new SpeechTestResult(true, "local-http", $"Connected to {probeUri.GetLeftPart(UriPartial.Authority)} ({cfg.LocalProtocol}).");
    }

    private async Task<string> TranscribeLocalHttpAsync(string wavPath, string language, SpeechConfig cfg, CancellationToken ct)
    {
        if (!cfg.AllowLocalServer)
            throw new InvalidOperationException("Local HTTP server is disabled in Settings.");

        var blocked = GetBlockedPorts();
        if (!SafeSpeechHttp.TryValidateLocalEndpoint(cfg.LocalBaseUrl, blocked, out var baseUri, out var isLoopback, out var urlErr) || baseUri is null)
            throw new InvalidOperationException(urlErr ?? "Invalid Local HTTP server URL.");

        IReadOnlyCollection<string>? credHosts = isLoopback ? null : [baseUri.Host];

        if (cfg.LocalProtocol == "openai-audio")
        {
            var uri = BuildRelativeEndpoint(baseUri, "/audio/transcriptions", "/audio/transcriptions", "/chat/completions");
            return await PostAudioMultipartAsync(uri, cfg.LocalApiKey, cfg.LocalModel, language, wavPath, isLoopback, credHosts, ct);
        }

        if (cfg.LocalProtocol == "ollama")
            throw new InvalidOperationException(OllamaUnsupportedError);

        if (cfg.LocalProtocol == "openai-chat")
        {
            var wavBytes = await SafeSpeechHttp.ReadBoundedAudioBytesAsync(wavPath, ct);
            var b64 = Convert.ToBase64String(wavBytes);
            var prompt = BuildSttPrompt(language);
            var chatUri = BuildRelativeEndpoint(baseUri, "/chat/completions", "/chat/completions", "/audio/transcriptions");
            var payload = JsonSerializer.Serialize(new
            {
                model = cfg.LocalModel,
                temperature = 0,
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new { type = "text", text = prompt },
                            new { type = "input_audio", input_audio = new { data = b64, format = "wav" } },
                        },
                    },
                },
            });

            var body = await PostJsonAsync(chatUri, cfg.LocalApiKey, payload, isLoopback, credHosts, ct);
            return ExtractOpenAiChatText(body);
        }

        throw new InvalidOperationException($"Unsupported local HTTP speech protocol '{cfg.LocalProtocol}'.");
    }

    private async Task<string> PostJsonAsync(
        Uri uri,
        string? apiKey,
        string payload,
        bool allowLoopback,
        IReadOnlyCollection<string>? allowedCredentialHosts,
        CancellationToken ct)
    {
        var blocked = GetBlockedPorts();
        using var client = _httpFactory(allowLoopback, blocked, TimeSpan.FromSeconds(120));
        using var res = await SafeSpeechHttp.SendWithSafeRedirectsAsync(client, (u, auth) =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, u)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            if (auth && !string.IsNullOrWhiteSpace(apiKey))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            return req;
        }, uri, allowLoopback, blocked, allowedCredentialHosts, ct);

        var body = await SafeSpeechHttp.ReadBoundedStringAsync(res.Content, ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Local server returned HTTP {(int)res.StatusCode}: {ExtractErrorMessage(body)}");
        return body;
    }

    internal static Uri BuildRelativeEndpoint(Uri baseUri, string relPath, params string[] stripSuffixes)
    {
        var builder = new UriBuilder(baseUri) { Query = "", Fragment = "" };
        var path = builder.Path.TrimEnd('/');
        foreach (var sfx in stripSuffixes)
        {
            if (path.EndsWith(sfx, StringComparison.OrdinalIgnoreCase))
            {
                path = path[..^sfx.Length].TrimEnd('/');
                break;
            }
        }
        builder.Path = path + relPath;
        return builder.Uri;
    }

    internal static Uri BuildProbeEndpoint(Uri baseUri, string probePath, params string[] stripSuffixes)
    {
        var builder = new UriBuilder(baseUri) { Query = "", Fragment = "" };
        var path = builder.Path.TrimEnd('/');
        foreach (var sfx in stripSuffixes)
        {
            if (path.EndsWith(sfx, StringComparison.OrdinalIgnoreCase))
            {
                path = path[..^sfx.Length].TrimEnd('/');
                break;
            }
        }
        if (!path.EndsWith(probePath, StringComparison.OrdinalIgnoreCase))
            path += probePath;
        builder.Path = string.IsNullOrEmpty(path) ? "/" : path;
        return builder.Uri;
    }

    internal static string ExtractOpenAiChatText(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0)
        {
            var msg = choices[0].TryGetProperty("message", out var m) ? m : default;
            if (msg.ValueKind == JsonValueKind.Object && msg.TryGetProperty("content", out var content))
            {
                if (content.ValueKind == JsonValueKind.String) return content.GetString()?.Trim() ?? "";
                if (content.ValueKind == JsonValueKind.Array)
                {
                    var sb = new StringBuilder();
                    foreach (var part in content.EnumerateArray())
                        if (part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String) sb.Append(t.GetString());
                    return sb.ToString().Trim();
                }
            }
        }
        return "";
    }
}
