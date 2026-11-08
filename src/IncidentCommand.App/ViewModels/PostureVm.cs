using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace IncidentCommand.App.ViewModels;

public sealed record PostureResult(string Id, string Name, bool Pass, string Detail, string Markers, bool CanFix, bool CanUndo);

public sealed record PostureRow(string Id, string Name, string State, Brush StateBrush, string Detail, string Markers,
                                bool CanFix, bool CanUndo);

/// Talks to PostureHelper.exe. "check" runs unelevated; "fix"/"undo" run elevated with one UAC prompt each.
/// An elevated process can't hand stdout back to us, so it writes its result to a temp file.
public partial class PostureVm : PageVm
{
    public ObservableCollection<PostureRow> Checks { get; } = new();
    [ObservableProperty] string status = "Press Check now to scan this laptop.";
    public bool CanAct => Session.CanAct;

    static string HelperPath => Path.Combine(AppContext.BaseDirectory, "PostureHelper.exe");

    public override void Refresh() { /* posture isn't in the database; refreshed on demand */ }

    [RelayCommand]
    async Task CheckNow()
    {
        Status = "Checking…";
        var results = await RunHelper(elevated: false, "check-all");
        Checks.Clear();
        foreach (var r in results)
            Checks.Add(new PostureRow(r.Id, r.Name, r.Pass ? "PASS" : "FAIL", r.Pass ? Ok : Danger, r.Detail, r.Markers, r.CanFix && !r.Pass, r.CanUndo));
        Status = $"{results.Count(r => r.Pass)} of {results.Count} checks pass.";
    }

    [RelayCommand] Task Fix(PostureRow row) => Change(row, "fix");
    [RelayCommand] Task Undo(PostureRow row) => Change(row, "undo");

    async Task Change(PostureRow row, string op)
    {
        var results = await RunHelper(elevated: true, op, row.Id);
        var r = results.FirstOrDefault();
        if (r != null) Conn.Reducers.RecordPosture(row.Id, op, r.Pass, "", r.Detail);
        await CheckNow();
    }

    static async Task<List<PostureResult>> RunHelper(bool elevated, params string[] args)
    {
        var outFile = Path.Combine(Path.GetTempPath(), $"posture-{Guid.NewGuid():N}.json");
        var psi = new ProcessStartInfo(HelperPath)
        {
            UseShellExecute = elevated,                // required for Verb = runas
            Verb = elevated ? "runas" : "",
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("--out");
        psi.ArgumentList.Add(outFile);
        try
        {
            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync();
            if (!File.Exists(outFile)) return new();
            return JsonSerializer.Deserialize<List<PostureResult>>(await File.ReadAllTextAsync(outFile)) ?? new();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Session.ReportError("The change was cancelled at the Windows prompt.");
            return new();
        }
        finally { if (File.Exists(outFile)) File.Delete(outFile); }
    }
}
