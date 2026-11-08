using System.Text.Json;

namespace IncidentCommand.Client;

/// One model loaded in LM Studio. Id must match LM Studio exactly (run `Agent.exe test` to list loaded IDs).
public sealed class AiModelOption
{
    public string Name { get; set; } = "";
    public string Id { get; set; } = "";

    /// "json" (default): LM Studio's structured output. The reply is grammar-constrained to valid JSON in our
    ///   schema, so small models can't produce broken tool calls. Works with any GGUF model.
    /// "native": OpenAI-style tool calls. Only for models whose LM Studio card shows tool-use support.
    public string ToolMode { get; set; } = "json";
}

/// The local LM Studio server (OpenAI-compatible). Everything runs on this laptop.
/// Models listed here appear in Settings > AI; each agent's choice is stored in the database.
public sealed class AiSettings
{
    public string BaseUrl { get; set; } = "http://127.0.0.1:1234/v1";
    public string ApiKey { get; set; } = "lm-studio";

    public List<AiModelOption> Models { get; set; } = new()
    {
        new() { Name = "Nemotron 3.5 Bolt E4B", Id = "nvidia-nemotron-3.5-bolt-e4b-4gb" },
        new() { Name = "Nemotron 3 Nano 4B", Id = "nvidia-nemotron-3-nano-4b" },
        new() { Name = "Qwen3 4B Instruct 2507", Id = "qwen3-4b-instruct-2507" },
    };

    /// Used when the commander hasn't picked a model for that agent yet.
    public string DefaultAgentModel { get; set; } = "nvidia-nemotron-3.5-bolt-e4b-4gb";
    public string DefaultReviewerModel { get; set; } = "qwen3-4b-instruct-2507";
    public string DefaultParserModel { get; set; } = "qwen3-4b-instruct-2507";

    /// The responder's own footprint on the hosts, so the agent doesn't mistake it for the attacker.
    /// Connections from these addresses are the executor's SSH sessions (in the Docker sim: Docker's gateway).
    public List<string> ManagementAddresses { get; set; } = new() { "172.18.0.1" };
    /// Process lines containing any of these are the executor's own commands or the host's normal baseline.
    public List<string> BaselineProcesses { get; set; } = new()
    {
        "tail -n 0 -F", "sshd: root@notty", "sshd: root", "sshd: [accepted]", "sshd: [net]", "sshd: /usr/sbin/sshd",
        "ps -eo pid", "ss -tunp", "/entrypoint.sh", "sleep 45",
    };

    /// Per-agent output cap (tokens). Small caps keep the demo fast.
    public int AgentMaxTokens { get; set; } = 900;
    public int ReviewerMaxTokens { get; set; } = 200;
    public int ParserMaxTokens { get; set; } = 1200;
}

/// Local settings, stored in %LOCALAPPDATA%\IncidentCommand\settings.json. Re-saved on load so new fields appear.
public sealed class Settings
{
    public string ServerUri { get; set; } = "http://127.0.0.1:3000";
    public string DatabaseName { get; set; } = "incident-command";

    /// Which model each agent uses is stored in the database (commander switches it live).
    /// This file only holds where LM Studio is and what each model is called there. Local only.
    public AiSettings Ai { get; set; } = new();

    /// Executor: private key used for SSH into the network (break-glass key, rotate after the incident).
    public string SshUser { get; set; } = "root";
    public string SshPrivateKeyPath { get; set; } = Path.Combine(AppPaths.Root, "keys", "breakglass_ed25519");

    /// Folder holding SpacetimeDB's JWT signing keys (checked by the posture panel). Fill in after first start.
    public string SpacetimeKeyDir { get; set; } = "";

    static Settings? _current;
    public static Settings Current => _current ??= Load();
    public static string FilePath => Path.Combine(AppPaths.Root, "settings.json");

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,          // hand edits with different casing still load
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// An empty or broken settings.json never stops the app: it's set aside as settings.json.bad-<time>
    /// and replaced with defaults, so you can copy your values back from it.
    static Settings Load()
    {
        Directory.CreateDirectory(AppPaths.Root);
        Settings? s = null;
        if (File.Exists(FilePath))
        {
            var text = File.ReadAllText(FilePath);
            if (!string.IsNullOrWhiteSpace(text))
            {
                try { s = JsonSerializer.Deserialize<Settings>(text, Json); }
                catch (JsonException) { /* handled below */ }
            }
            if (s == null)
            {
                try { File.Copy(FilePath, $"{FilePath}.bad-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true); } catch { }
            }
        }
        s ??= new Settings();
        s.Ai ??= new AiSettings();
        s.Ai.Models ??= new AiSettings().Models;
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(s, Json)); } catch { /* read-only is fine */ }
        return s;
    }
}

public static class AppPaths
{
    public static string Root { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IncidentCommand");
    public static string Vaults => Ensure(Path.Combine(Root, "vaults"));
    public static string Services => Ensure(Path.Combine(Root, "services"));
    public static string PostureUndo => Path.Combine(Ensure(Root), "posture-undo.json");

    static string Ensure(string p) { Directory.CreateDirectory(p); return p; }
}

/// Model names and per-agent defaults, shared by the app and the agents.
public static class AiNames
{
    /// The friendly name from settings.json, or the raw ID if it isn't listed.
    public static string Name(string id) =>
        Settings.Current.Ai.Models.FirstOrDefault(m => m.Id == id)?.Name ?? (string.IsNullOrEmpty(id) ? "(none)" : id);

    /// The configured model for an agent, falling back to that agent's default in settings.json.
    public static string Resolve(string configured, string agent) =>
        !string.IsNullOrWhiteSpace(configured) ? configured : agent switch
        {
            "reviewer" => Settings.Current.Ai.DefaultReviewerModel,
            "parser" => Settings.Current.Ai.DefaultParserModel,
            _ => Settings.Current.Ai.DefaultAgentModel,
        };
}
