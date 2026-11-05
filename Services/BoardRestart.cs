using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Whetstone.Services;

/// <summary>
/// A restart of the live board that waits for the fleet: a pending stop, the drain, the restart
/// through <c>Start-Whetstone.bat</c>, and the heartbeat started again.
/// <para>
/// The board cannot do the middle of that itself. <c>dotnet run</c> holds the app, the heartbeat
/// and every worker in one kill-on-close job, so whatever stops the app has to be outside it. The
/// work is <c>data/restart-board.ps1</c>, launched through <c>explorer.exe</c>, which no process
/// here owns. The two talk through one file, <c>data/board.restart</c>: the board writes it, the
/// script writes its phase into it and deletes it when done, and the board deleting it is how a
/// restart still waiting on the drain is called off.
/// </para>
/// </summary>
public class BoardRestart
{
    public const string MarkerName = "board.restart";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _marker;
    private readonly string _launcher;
    private readonly Action<string> _launch;

    public BoardRestart(DashboardPaths paths) : this(paths, Launch) { }

    /// <param name="launch">Starts the launcher at the given path; replaced in tests.</param>
    public BoardRestart(DashboardPaths paths, Action<string> launch)
    {
        _marker = Path.Combine(paths.DataRoot, MarkerName);
        _launcher = Path.Combine(paths.DataRoot, "restart-board.bat");
        _launch = launch;
    }

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
    /// Through explorer, never as a child: a child of this process is in the board's job, and the
    /// first thing the script does is stop the board.
    /// </summary>
    private static void Launch(string bat) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{bat}\"") { UseShellExecute = false })?.Dispose();
}
