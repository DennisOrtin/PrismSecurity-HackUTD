using System.Text.Json;

namespace IncidentCommand.Client;

/// One AI endpoint (OpenAI-compatible /chat/completions) and the model ID each Nemotron model has there.
/// Model IDs differ per provider: copy them from LM Studio's server tab, or from your Brev deployment.
public sealed class AiEndpoint
{
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string NanoModel { get; set; } = "";
    public string LightningModel { get; set; } = "";
}

public sealed class AiSettings
{
    /// LM Studio on this laptop. Final build: this is the only provider used.
    public AiEndpoint Local { get; set; } = new()
    {
        BaseUrl = "http://127.0.0.1:1234/v1",
        ApiKey = "lm-studio",
        NanoModel = "nvidia-nemotron-3.5-nano-4b",
        LightningModel = "nvidia-nemotron-3.5-lightning",
    };

    /// NVIDIA Brev deployment (OpenAI-compatible). DEVELOPMENT/TESTING ONLY: incident data leaves the laptop.
    public AiEndpoint Cloud { get; set; } = new()
    {
        BaseUrl = "https://YOUR-BREV-DEPLOYMENT/v1",
        ApiKey = "",
        NanoModel = "nvidia/nemotron-3.5-nano-4b",
        LightningModel = "nvidia/nemotron-3.5-lightning",
    };
}

/// Local settings, stored in %LOCALAPPDATA%\IncidentCommand\settings.json. Re-saved on load so new fields appear.
public sealed class Settings
{
    public string ServerUri { get; set; } = "http://127.0.0.1:3000";
    public string DatabaseName { get; set; } = "incident-command";

    /// Which provider and models are ACTIVE is stored in the database (commander switches it live).
    /// This file only holds where each provider lives and what each model is called there.
    public AiSettings Ai { get; set; } = new();

    /// Executor: private key used for SSH into the network (break-glass key, rotate after the incident).
    public string SshUser { get; set; } = "root";
    public string SshPrivateKeyPath { get; set; } = Path.Combine(AppPaths.Root, "keys", "breakglass_ed25519");

    /// Folder holding SpacetimeDB's JWT signing keys (checked by the posture panel). Fill in after first start.
    public string SpacetimeKeyDir { get; set; } = "";

    static Settings? _current;
    public static Settings Current => _current ??= Load();
    public static string FilePath => Path.Combine(AppPaths.Root, "settings.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    static Settings Load()
    {
        Directory.CreateDirectory(AppPaths.Root);
        var s = File.Exists(FilePath)
            ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings()
            : new Settings();
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

/// Display names shared by the app and the agent.
public static class AiNames
{
    public static string Model(SpacetimeDB.Types.AiModel m) => m switch
    {
        SpacetimeDB.Types.AiModel.Lightning => "Nemotron 3.5 Lightning",
        _ => "Nemotron 3.5 Nano 4B",
    };

    public static string Label(SpacetimeDB.Types.AiModel m, SpacetimeDB.Types.AiProvider p) =>
        $"{Model(m)} · {(p == SpacetimeDB.Types.AiProvider.Cloud ? "cloud" : "local")}";
}
