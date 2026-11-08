using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using IncidentCommand.Client;

namespace IncidentCommand.Agent;

public sealed record ToolCall(string Name, JsonObject Args);
public sealed record ModelReply(string? Content, List<ToolCall> ToolCalls);

/// Minimal client for any OpenAI-compatible /chat/completions endpoint (local Nemotron server, or Brev in dev).
public sealed class ModelClient
{
    readonly HttpClient _http;
    readonly Settings _s;

    public ModelClient(Settings s)
    {
        _s = s;
        _http = new HttpClient { BaseAddress = new Uri(s.ModelBaseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(3) };
        if (!string.IsNullOrEmpty(s.ModelApiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.ModelApiKey);
    }

    public async Task<ModelReply> ChatAsync(string system, string user, JsonArray? tools = null, double temperature = 0.2)
    {
        var body = new JsonObject
        {
            ["model"] = _s.ModelName,
            ["temperature"] = temperature,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user },
            },
        };
        if (tools != null) { body["tools"] = tools; body["tool_choice"] = "auto"; }

        using var resp = await _http.PostAsync("chat/completions",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Model returned {(int)resp.StatusCode}: {text[..Math.Min(400, text.Length)]}");

        var msg = JsonNode.Parse(text)!["choices"]![0]!["message"]!;
        var calls = new List<ToolCall>();
        if (msg["tool_calls"] is JsonArray arr)
            foreach (var c in arr)
            {
                var fn = c!["function"]!;
                var argsText = fn["arguments"]?.GetValue<string>() ?? "{}";
                JsonObject args;
                try { args = JsonNode.Parse(argsText) as JsonObject ?? new JsonObject(); }
                catch { args = new JsonObject(); }
                calls.Add(new ToolCall(fn["name"]!.GetValue<string>(), args));
            }
        return new ModelReply(msg["content"]?.GetValue<string>(), calls);
    }

    public static JsonObject Tool(string name, string description, JsonObject parameters) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject { ["name"] = name, ["description"] = description, ["parameters"] = parameters },
    };
}
