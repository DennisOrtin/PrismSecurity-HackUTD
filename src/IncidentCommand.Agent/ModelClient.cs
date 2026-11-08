using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using IncidentCommand.Client;
using SpacetimeDB.Types;

namespace IncidentCommand.Agent;

public sealed record ToolCall(string Name, JsonObject Args);
public sealed record ModelReply(string? Content, List<ToolCall> ToolCalls);

/// Talks to the local LM Studio server (OpenAI-compatible). Local only: there is no cloud path.
/// Which model to use is decided per call (each agent reads its model from the database every time).
public sealed class ModelClient
{
    readonly HttpClient _http;

    public ModelClient()
    {
        var ai = Settings.Current.Ai;
        _http = new HttpClient { BaseAddress = new Uri(ai.BaseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(3) };
        if (!string.IsNullOrEmpty(ai.ApiKey)) _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ai.ApiKey);
    }

    // Reasoning models may wrap their thinking in <think>…</think>; never show or parse that.
    static readonly Regex Think = new(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase);

    /// modelId: the LM Studio model ID (see AiNames.Resolve).
    public async Task<ModelReply> ChatAsync(string modelId, string system, string user,
                                            JsonArray? tools = null, double temperature = 0.2, int maxTokens = 2048)
    {
        var body = new JsonObject
        {
            ["model"] = modelId,
            ["temperature"] = temperature,
            ["max_tokens"] = maxTokens,
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
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"LM Studio returned {(int)resp.StatusCode}: {text[..Math.Min(300, text.Length)]}");

        var msg = JsonNode.Parse(text)!["choices"]![0]!["message"]!;
        var calls = new List<ToolCall>();
        if (msg["tool_calls"] is JsonArray arr)
            foreach (var c in arr)
            {
                var fn = c!["function"]!;
                calls.Add(new ToolCall(fn["name"]!.GetValue<string>(), ParseArgs(fn["arguments"])));
            }
        var content = msg["content"]?.GetValue<string>();
        if (content != null) content = Think.Replace(content, "").Trim();
        return new ModelReply(content, calls);
    }

    public static JsonObject ParseArgs(JsonNode? node)
    {
        try
        {
            return node is JsonValue v && v.TryGetValue<string>(out var str)
                ? JsonNode.Parse(str) as JsonObject ?? new JsonObject()
                : node as JsonObject ?? new JsonObject();
        }
        catch { return new JsonObject(); }
    }

    /// Small models sometimes write tool calls as JSON in the message text. Accepts
    /// {"tool": "...", "arguments": {...}}, {"calls": [ ... ]}, or a bare plan {"reasoning": ..., "steps": [...]}.
    public static List<ToolCall> ToolCallsFromContent(string? content, string? bareStepsTool = null)
    {
        var found = new List<ToolCall>();
        if (string.IsNullOrWhiteSpace(content)) return found;
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start) return found;
        try
        {
            if (JsonNode.Parse(content[start..(end + 1)]) is not JsonObject o) return found;
            void One(JsonObject c)
            {
                var name = c["tool"]?.ToString() ?? c["name"]?.ToString();
                var args = c["arguments"] ?? c["args"] ?? c["parameters"];
                if (name != null) found.Add(new ToolCall(name, ParseArgs(args?.DeepClone())));
            }
            if (o["calls"] is JsonArray many) { foreach (var c in many) if (c is JsonObject co) One(co); }
            else if (o["tool"] != null || o["name"] != null) One(o);
            else if (bareStepsTool != null && o["steps"] is JsonArray) found.Add(new ToolCall(bareStepsTool, o));
        }
        catch { /* not JSON: caller treats it as plain text */ }
        return found;
    }

    /// GET /models: what LM Studio has available.
    public async Task<string> ListModelsAsync()
    {
        var text = await _http.GetStringAsync("models");
        var ids = JsonNode.Parse(text)?["data"]?.AsArray().Select(m => m?["id"]?.ToString()).Where(x => x != null);
        return ids == null ? text : string.Join(", ", ids);
    }

    public static JsonObject Tool(string name, string description, JsonObject parameters) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject { ["name"] = name, ["description"] = description, ["parameters"] = parameters },
    };

    public static JsonObject Str(string d) => new() { ["type"] = "string", ["description"] = d };

    public static JsonObject Obj(JsonObject props, params string[] required) => new()
    {
        ["type"] = "object", ["properties"] = props,
        ["required"] = new JsonArray(required.Select(r => (JsonNode)r!).ToArray()),
    };
}
