using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Whetstone.Services;

/// <summary>
/// A restart of the live board that waits for the fleet: a pending stop, the drain, the restart
/// through <c>Start-Whetstone.bat</c> (<c>Start-Whetstone.command</c> on a Mac), and the heartbeat
/// started again.
/// <para>
/// The board cannot do the middle of that itself: whatever stops the app has to be outside it. On
/// Windows <c>dotnet run</c> holds the app, the heartbeat and every worker in one kill-on-close job,
/// so the work, <c>data/restart-board.ps1</c>, is launched through <c>explorer.exe</c>, which no
/// process here owns. On a Mac it is started under <c>nohup</c> by a shell that exits at once, so
/// it is handed to launchd and is in no tree the script stops - and ignores the hangup the board's
/// Terminal window sends when the board goes. Elsewhere there is no launcher, and it is refused. The two talk through one file, <c>data/board.restart</c>: the board writes it, the
/// script writes its phase into it and deletes it when done, and the board deleting it is how a
/// restart still waiting on the drain is called off.
/// </para>
/// </summary>
public class BoardRestart
{
    public const string MarkerName = "board.restart";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _marker;
    private readonly string? _launcher;
    private readonly Action<string> _launch;

    public BoardRestart(DashboardPaths paths) : this(paths, Launch) { }

    /// <param name="launch">Starts the launcher at the given path; replaced in tests.</param>
    /// <param name="mac">Which machine's launcher to use; null asks this one, as does <paramref name="windows"/>.</param>
    public BoardRestart(DashboardPaths paths, Action<string> launch, bool? mac = null, bool? windows = null)
    {
        _marker = Path.Combine(paths.DataRoot, MarkerName);
        _launcher = LauncherFor(paths.DataRoot, mac ?? OperatingSystem.IsMacOS(), windows ?? OperatingSystem.IsWindows());
        _launch = launch;
    }

    /// <summary>
    /// What starts the restart: the .bat that launches the script on Windows, the script itself on
    /// a Mac (see <see cref="Launch"/>), and nothing on any other machine.
    /// </summary>
    public static string? LauncherFor(string dataRoot, bool mac, bool windows) =>
        mac ? Path.Combine(dataRoot, "restart-board.ps1")
        : windows ? Path.Combine(dataRoot, "restart-board.bat")
        : null;

    /// <summary>Where a restart stands. Phases: draining, restarting, starting, resuming, failed.</summary>
    public record State(
        [property: JsonPropertyName("requestedAt")] DateTime RequestedAt,
        [property: JsonPropertyName("resume")] bool Resume,
        [property: JsonPropertyName("port")] int Port,
        [property: JsonPropertyName("phase")] string Phase,
        [property: JsonPropertyName("error")] string? Error = null);

    /// <summary>The restart in hand, or null when there is none.</summary>
    public State? Read()
    {
        try
        {
            if (!File.Exists(_marker)) return null;
            return JsonSerializer.Deserialize<State>(File.ReadAllText(_marker), Json);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Ask for a restart. <paramref name="resume"/> is whether the heartbeat was running, and so
    /// whether it is started again afterwards. The caller sets the pending stop first.
    /// </summary>
    public (bool Ok, string Message) Request(bool resume, int port)
    {
        if (Read() is { Phase: not "failed" } current)
            return (false, $"A restart is already under way ({current.Phase}).");
        if (_launcher is null)
            return (false, "Restarting the board from here works on Windows and macOS only.");
        if (!File.Exists(_launcher))
            return (false, $"The restart script is missing: {_launcher}.");

        var state = new State(DateTime.UtcNow, resume, port, resume ? "draining" : "restarting");
        File.WriteAllText(_marker, JsonSerializer.Serialize(state, Json));
        try
        {
            _launch(_launcher);
        }
        catch (Exception e)
        {
            TryDelete();
            return (false, $"Could not start the restart: {e.Message}");
        }
        return (true, resume
            ? "Whetstone will restart once the running agents finish, then start the heartbeat again."
            : "Whetstone is restarting.");
    }

    /// <summary>
    /// Call off a restart still waiting on the drain, or clear one that failed. Past the drain the
    /// board is already on its way down, and there is nothing left here to cancel.
    /// </summary>
    public (bool Ok, string Message) Cancel()
    {
        var s = Read();
        if (s is null) return (true, "No restart is pending.");
        if (s.Phase is not ("draining" or "failed"))
            return (false, $"Too late to cancel: the restart is {s.Phase}.");
        TryDelete();
        return (true, s.Phase == "failed" ? "Cleared the failed restart." : "Restart cancelled.");
    }

    private void TryDelete()
    {
        try { File.Delete(_marker); } catch { /* best effort */ }
    }

    /// <summary>
    /// Never as a child that stays one: the first thing the script does is stop the board. On
    /// Windows a child of this process is in the board's job, so it goes through explorer. On a Mac
    /// the shell started here backgrounds the script under nohup and exits, which leaves it to
    /// launchd.
    /// </summary>
    private static void Launch(string launcher)
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{launcher}\"") { UseShellExecute = false })?.Dispose();
            return;
        }
        var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        foreach (var a in new[]
                 {
                     "-c", "nohup \"$0\" -NoProfile -ExecutionPolicy Bypass -File \"$1\" >/dev/null 2>&1 &",
                     PowerShellHost.Exe, launcher,
                 })
            psi.ArgumentList.Add(a);
        using var sh = Process.Start(psi);
        sh?.WaitForExit(5000);
    }
}
