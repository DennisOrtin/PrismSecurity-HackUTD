using System.Text.Json.Nodes;

namespace IncidentCommand.Agent;

/// A model reply stored in the database while it waits behind the Continue / Stop prompt.
/// On Continue it's applied exactly as if it had just arrived, so it shows up instantly.
public static class HeldReply
{
    public static string Write(ModelReply reply, string label)
    {
        var calls = new JsonArray();
        foreach (var c in reply.ToolCalls)
            calls.Add(new JsonObject { ["name"] = c.Name, ["args"] = c.Args.DeepClone() });
        return new JsonObject { ["label"] = label, ["content"] = reply.Content, ["calls"] = calls }.ToJsonString();
    }

    public static (ModelReply Reply, string Label) Read(string? stored)
    {
        try
        {
            if (JsonNode.Parse(stored ?? "") is JsonObject o)
            {
                var calls = new List<ToolCall>();
                foreach (var c in o["calls"]?.AsArray() ?? new JsonArray())
                    if (c?["name"]?.ToString() is { } name)
                        calls.Add(new ToolCall(name, c["args"] as JsonObject is { } a ? (JsonObject)a.DeepClone() : new JsonObject()));
                return (new ModelReply(o["content"]?.ToString(), calls), o["label"]?.ToString() ?? "agent");
            }
        }
        catch { /* fall through */ }
        return (new ModelReply(stored, new List<ToolCall>()), "agent");
    }
}
