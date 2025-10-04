using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClassDescriber.Vsix;

/// <summary>
/// Minimal OpenAI client. Reads API key from the OPENAI_API_KEY environment variable.
/// Swap baseUri/model to your provider as needed.
/// </summary>
internal sealed class AiClient
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly Uri _baseUri;
    private readonly string _model;

    public AiClient(HttpClient httpClient, string? apiKey = null, string? baseUrl = null, string model = "gpt-4o-mini")
    {
        _http = httpClient;
        _apiKey = apiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty;
        _baseUri = new Uri(string.IsNullOrWhiteSpace(baseUrl) ? "https://api.openai.com/v1/" : baseUrl);
        _model = model;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    public async Task<string> ExplainAsync(string code, string language, string filePath, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            return "AI is not configured. Set the OPENAI_API_KEY environment variable and try again.";
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, "chat/completions"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        var sys = "You are a senior C#/.NET engineer. Explain the selected code in plain English. " +
                  "Summarize purpose, key inputs/outputs, side effects, exceptions, and any risks. " +
                  "If code is incomplete, infer intent cautiously. Keep it concise with bullet points.";

        var user = $"File: {filePath}\nLanguage: {language}\n\n```{language}\n{code}\n```";

        var body = new
        {
            model = _model,
            messages = new object[]
            {
                new { role = "system", content = sys },
                new { role = "user", content = user }
            },
            temperature = 0.2
        };

        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        return content ?? "(no response)";
    }
}
