using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using IncidentCommand.Client;
using SpacetimeDB.Types;

namespace IncidentCommand.Agent;

public sealed record ToolCall(string Name, JsonObject Args);
public sealed record ModelReply(string? Content, List<ToolCall> ToolCalls);

/// Talks to either provider. Which provider and model to use is decided PER CALL (the commander can
/// switch them live in the app, and a single request can override the model).
public sealed class ModelClient
{
    readonly Dictionary<AiProvider, HttpClient> _clients = new();

    HttpClient ClientFor(AiProvider p)
    {
        if (_clients.TryGetValue(p, out var c)) return c;
        var ep = Endpoint(p);
        c = new HttpClient { BaseAddress = new Uri(ep.BaseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(3) };
        if (!string.IsNullOrEmpty(ep.ApiKey)) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ep.ApiKey);
        return _clients[p] = c;
    }

    static AiEndpoint Endpoint(AiProvider p) => p == AiProvider.Cloud ? Settings.Current.Ai.Cloud : Settings.Current.Ai.Local;

    public static string ModelId(AiProvider p, AiModel m) =>
        m == AiModel.Lightning ? Endpoint(p).LightningModel : Endpoint(p).NanoModel;

    // Reasoning models may wrap their thinking in <think>…</think>; never show or parse that.
    static readonly Regex Think = new(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase);

    public async Task<ModelReply> ChatAsync(AiProvider provider, AiModel model, string system, string user,
                                            JsonArray? tools = null, double temperature = 0.2)
    {
        var body = new JsonObject
        {
            ["model"] = ModelId(provider, model),
            ["temperature"] = temperature,
            ["max_tokens"] = 2048,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user },
            },
        };
        if (tools != null) { body["tools"] = tools; body["tool_choice"] = "auto"; }

        using var resp = await ClientFor(provider).PostAsync("chat/completions",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{provider} model returned {(int)resp.StatusCode}: {text[..Math.Min(300, text.Length)]}");

        var msg = JsonNode.Parse(text)!["choices"]![0]!["message"]!;
        var calls = new List<ToolCall>();
        if (msg["tool_calls"] is JsonArray arr)
            foreach (var c in arr)
            {
                var fn = c!["function"]!;
                var argsNode = fn["arguments"];
                JsonObject args;
                try
                {
                    args = argsNode is JsonValue v && v.TryGetValue<string>(out var str)
                        ? JsonNode.Parse(str) as JsonObject ?? new JsonObject()
                        : argsNode as JsonObject ?? new JsonObject();
                }
                catch { args = new JsonObject(); }
                calls.Add(new ToolCall(fn["name"]!.GetValue<string>(), args));
            }
        var content = msg["content"]?.GetValue<string>();
        if (content != null) content = Think.Replace(content, "").Trim();
        return new ModelReply(content, calls);
    }

    /// GET /models: what the provider says it has loaded.
    public async Task<string> ListModelsAsync(AiProvider provider)
    {
        var text = await ClientFor(provider).GetStringAsync("models");
        var ids = JsonNode.Parse(text)?["data"]?.AsArray().Select(m => m?["id"]?.ToString()).Where(x => x != null);
        return ids == null ? text : string.Join(", ", ids);
    }

    public static JsonObject Tool(string name, string description, JsonObject parameters) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject { ["name"] = name, ["description"] = description, ["parameters"] = parameters },
    };
}
