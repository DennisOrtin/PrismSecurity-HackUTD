using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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
    /// tools: the tools this agent may call. In "json" mode (the default) they become a JSON schema the reply is
    ///   constrained to ({"calls": [...]}); in "native" mode they're sent as OpenAI tool definitions.
    /// responseSchema: constrain a plain reply (no tools) to this JSON schema, e.g. the reviewer's verdict.
    public async Task<ModelReply> ChatAsync(string modelId, string system, string user,
                                            JsonArray? tools = null, double temperature = 0.2, int maxTokens = 2048,
                                            JsonObject? responseSchema = null)
    {
        var mode = Settings.Current.Ai.Models.FirstOrDefault(m => m.Id == modelId)?.ToolMode ?? "json";
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
        if (tools != null && mode == "native") { body["tools"] = tools; body["tool_choice"] = "auto"; }
        else if (tools != null) body["response_format"] = Format("tool_calls", CallsSchema(tools));
        else if (responseSchema != null) body["response_format"] = Format("reply", responseSchema);

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

    static JsonObject Format(string name, JsonObject schema) => new()
    {
        ["type"] = "json_schema",
        ["json_schema"] = new JsonObject { ["name"] = name, ["strict"] = true, ["schema"] = schema },
    };

    /// {"calls": [ {"tool": "<name>", "arguments": {<that tool's parameters>}} , ... ]}
    /// built from the same tool definitions, so both modes share one source of truth.
    static JsonObject CallsSchema(JsonArray tools)
    {
        var variants = new JsonArray();
        foreach (var t in tools)
        {
            var fn = t!["function"]!;
            variants.Add(new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["tool"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(fn["name"]!.GetValue<string>()) },
                    ["arguments"] = fn["parameters"]!.DeepClone(),
                },
                ["required"] = new JsonArray("tool", "arguments"),
            });
        }
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["calls"] = new JsonObject
                {
                    ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 6,
                    ["items"] = new JsonObject { ["anyOf"] = variants },
                },
            },
            ["required"] = new JsonArray("calls"),
        };
    }

    static readonly JsonDocumentOptions Lenient = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    /// Parses the first JSON object/array in `text`, repairing what small models commonly break:
    /// closing brackets out of order, missing or extra closers, a truncated string, trailing commas.
    public static JsonNode? ParseLenient(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        int a = text.IndexOf('{'), b = text.IndexOf('[');
        int start = a < 0 ? b : b < 0 ? a : Math.Min(a, b);
        if (start < 0) return null;

        var sb = new StringBuilder();
        var stack = new Stack<char>();
        bool inStr = false, esc = false;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (inStr)
            {
                sb.Append(c);
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
                continue;
            }
            switch (c)
            {
                case '"': inStr = true; sb.Append(c); break;
                case '{': stack.Push('}'); sb.Append(c); break;
                case '[': stack.Push(']'); sb.Append(c); break;
                case '}':
                case ']':
                    if (stack.Count == 0) break;                                   // stray closer: drop
                    if (stack.Peek() == c) sb.Append(stack.Pop());
                    else if (stack.Contains(c)) { while (stack.Peek() != c) sb.Append(stack.Pop()); sb.Append(stack.Pop()); }
                    break;                                                         // wrong closer: drop
                default: sb.Append(c); break;
            }
            if (stack.Count == 0 && sb.Length > 0) break;                          // root closed
        }
        if (inStr) sb.Append('"');
        while (stack.Count > 0) sb.Append(stack.Pop());
        try { return JsonNode.Parse(sb.ToString(), documentOptions: Lenient); }
        catch { return null; }
    }

    /// Small models sometimes write tool calls as JSON in the message text. Accepts
    /// {"tool": "...", "arguments": {...}}, {"calls": [ ... ]}, or a bare plan {"reasoning": ..., "steps": [...]}.
    public static List<ToolCall> ToolCallsFromContent(string? content, string? bareStepsTool = null)
    {
        var found = new List<ToolCall>();
        if (string.IsNullOrWhiteSpace(content)) return found;
        try
        {
            if (ParseLenient(content) is not JsonObject o) return found;
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
