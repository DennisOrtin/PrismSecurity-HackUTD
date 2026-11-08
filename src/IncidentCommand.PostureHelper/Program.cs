using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;

var outIdx = Array.IndexOf(args, "--out");
var outFile = outIdx >= 0 && outIdx + 1 < args.Length ? args[outIdx + 1] : null;
var op = args.FirstOrDefault() ?? "";
var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IncidentCommand");
var undoPath = Path.Combine(root, "posture-undo.json");
var undo = File.Exists(undoPath)
    ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(undoPath)) ?? new()
    : new Dictionary<string, string>();

var results = new List<Result>();

switch (op)
{
    case "check-all":
        foreach (var c in Checks.All) results.Add(Evaluate(c));
        break;

    case "fix" or "undo":
        var id = args.ElementAtOrDefault(1) ?? "";
        var check = Checks.All.FirstOrDefault(c => c.Id == id);
        if (check == null) { Write(new() { new Result(id, id, false, "Unknown check.", "", false, false) }); return 2; }
        if (!IsAdmin()) { Write(new() { new Result(id, check.Name, false, "Needs administrator rights.", check.Markers, false, false) }); return 3; }

        if (op == "fix" && check.Fix != null)
        {
            var before = Ps(check.Read);
            if (!undo.ContainsKey(id)) undo[id] = before;          // keep the ORIGINAL value, not an intermediate one
            File.WriteAllText(undoPath, JsonSerializer.Serialize(undo));
            Ps(check.Fix);
        }
        else if (op == "undo" && check.Undo != null && undo.TryGetValue(id, out var original))
        {
            Ps(check.Undo(original));
            undo.Remove(id);
            File.WriteAllText(undoPath, JsonSerializer.Serialize(undo));
        }
        results.Add(Evaluate(check));
        break;

    default:
        Console.Error.WriteLine("Usage: PostureHelper check-all | fix <id> | undo <id>  [--out file]");
        return 1;
}

Write(results);
return 0;

Result Evaluate(Check c)
{
    string state;
    try { state = Ps(c.Read).Trim(); } catch (Exception ex) { state = "error: " + ex.Message; }
    bool pass = !state.StartsWith("error") && c.Pass(state);
    var detail = pass ? c.PassText : $"{c.FailText} (now: {(state.Length > 140 ? state[..140] + "…" : state)})";
    return new Result(c.Id, c.Name, pass, detail, c.Markers, c.Fix != null, undo.ContainsKey(c.Id) && c.Undo != null);
}

void Write(List<Result> r)
{
    var json = JsonSerializer.Serialize(r);
    if (outFile != null) File.WriteAllText(outFile, json); else Console.WriteLine(json);
}

static bool IsAdmin() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

static string Ps(string script)
{
    var psi = new ProcessStartInfo("powershell.exe")
    {
        RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
    };
    foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script }) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    var output = p.StandardOutput.ReadToEnd();
    var err = p.StandardError.ReadToEnd();
    p.WaitForExit();
    if (p.ExitCode != 0 && output.Trim().Length == 0) throw new InvalidOperationException(err.Trim());
    return output;
}

public record Result(string Id, string Name, bool Pass, string Detail, string Markers, bool CanFix, bool CanUndo);
