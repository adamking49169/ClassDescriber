using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClassDescriber
{
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

        public AiClient(HttpClient httpClient, string apiKey = null, string baseUrl = null, string model = "gpt-4o-mini")
        {
            _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _apiKey = apiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty;
            _baseUri = new Uri(string.IsNullOrWhiteSpace(baseUrl) ? "https://api.openai.com/v1/" : baseUrl);
            _model = model ?? "gpt-4o-mini";
        }

        public bool IsConfigured
        {
            get { return !string.IsNullOrWhiteSpace(_apiKey); }
        }

        public async Task<string> ExplainAsync(string code, string language, string filePath, CancellationToken ct)
        {
            if (!IsConfigured)
            {
                return "AI is not configured. Set the OPENAI_API_KEY environment variable and try again.";
            }

            var requestUri = new Uri(_baseUri, "chat/completions");
            using (var req = new HttpRequestMessage(HttpMethod.Post, requestUri))
            {
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

                var json = JsonSerializer.Serialize(body);
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");

                using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();

                    // Compatible with net472-style VSIX: read as string, then Parse
                    var jsonText = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                    using (var doc = JsonDocument.Parse(jsonText))
                    {
                        var root = doc.RootElement;

                        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                            return "no response";

                        var first = choices[0];

                        if (!first.TryGetProperty("message", out var message) ||
                            !message.TryGetProperty("content", out var content) ||
                            content.ValueKind != JsonValueKind.String)
                            return "no response";

                        var text = content.GetString();
                        return string.IsNullOrWhiteSpace(text) ? "no response" : text;
                    }
                }
            }
        }
    }
}
