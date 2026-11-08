using System.Text.Json;

namespace IncidentCommand.Client;

/// Local settings, stored next to the vaults. Everything defaults to loopback.
public sealed class Settings
{
    public string ServerUri { get; set; } = "http://127.0.0.1:3000";
    public string DatabaseName { get; set; } = "incident-command";

    /// Any OpenAI-compatible endpoint. FINAL BUILD: must be loopback (the posture panel checks this).
    /// During development only, this may point at a Brev-hosted endpoint.
    public string ModelBaseUrl { get; set; } = "http://127.0.0.1:8000/v1";
    public string ModelName { get; set; } = "nemotron-3.5-nano-4b";
    public string ModelApiKey { get; set; } = "";

    /// Executor: private key used for SSH into the network (break-glass key, rotate after the incident).
    public string SshUser { get; set; } = "root";
    public string SshPrivateKeyPath { get; set; } = Path.Combine(AppPaths.Root, "keys", "breakglass_ed25519");

    /// Folder holding SpacetimeDB's JWT signing keys (checked by the posture panel). Fill in after first start.
    public string SpacetimeKeyDir { get; set; } = "";

    static Settings? _current;
    public static Settings Current => _current ??= Load();

    static Settings Load()
    {
        var path = Path.Combine(AppPaths.Root, "settings.json");
        if (File.Exists(path))
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new Settings();
        var s = new Settings();
        Directory.CreateDirectory(AppPaths.Root);
        File.WriteAllText(path, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
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
