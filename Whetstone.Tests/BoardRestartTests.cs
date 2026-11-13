using System.Text.Json;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The board's half of restart-after-drain: the marker file it hands the restart through. The
/// script's half (stopping and starting the live board) is not run here; what is pinned is when a
/// restart may be asked for, what the script is told, and when it can still be called off.
/// </summary>
public class BoardRestartTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "whetstone-restart-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _launched = new();

    public BoardRestartTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "restart-board.bat"), "@echo off");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }

    // The Windows launcher on any machine, so these mean the same wherever they run.
    private BoardRestart Restart(Action<string>? launch = null) =>
        new(new DashboardPaths(_root), launch ?? _launched.Add, mac: false, windows: true);

    private string Marker => Path.Combine(_root, BoardRestart.MarkerName);

    private void SetPhase(string phase)
    {
        var s = Restart().Read()!;
        File.WriteAllText(Marker, JsonSerializer.Serialize(s with { Phase = phase }));
    }

    [Fact]
    public void A_restart_tells_the_script_the_port_and_whether_to_resume_and_launches_it_once()
    {
        var (ok, _) = Restart().Request(resume: true, port: 5338);
        Assert.True(ok);
        Assert.Single(_launched);
        Assert.EndsWith("restart-board.bat", _launched[0]);

        var s = Restart().Read()!;
        Assert.True(s.Resume);
        Assert.Equal(5338, s.Port);
        Assert.Equal("draining", s.Phase);
    }

    [Fact]
    public void With_the_heartbeat_stopped_there_is_nothing_to_drain_or_resume()
    {
        Assert.True(Restart().Request(resume: false, port: 5338).Ok);
        var s = Restart().Read()!;
        Assert.False(s.Resume);
        Assert.Equal("restarting", s.Phase);
    }

    [Fact]
    public void A_second_restart_is_refused_while_one_is_under_way_but_not_after_one_failed()
    {
        Assert.True(Restart().Request(true, 5338).Ok);
        Assert.False(Restart().Request(true, 5338).Ok);
        Assert.Single(_launched);

        SetPhase("failed");
        Assert.True(Restart().Request(true, 5338).Ok);
        Assert.Equal(2, _launched.Count);
    }

    [Fact]
    public void A_restart_is_called_off_while_it_waits_for_the_drain_and_not_once_the_board_is_going_down()
    {
        Restart().Request(true, 5338);
        Assert.True(Restart().Cancel().Ok);
        Assert.Null(Restart().Read());

        Restart().Request(true, 5338);
        SetPhase("restarting");
        Assert.False(Restart().Cancel().Ok);
        Assert.NotNull(Restart().Read());
    }

    [Fact]
    public void A_failed_restart_is_cleared_by_cancelling_it()
    {
        Restart().Request(true, 5338);
        SetPhase("failed");
        Assert.True(Restart().Cancel().Ok);
        Assert.Null(Restart().Read());
    }

    [Fact]
    public void A_launch_that_fails_leaves_no_restart_behind()
    {
        var (ok, message) = Restart(_ => throw new InvalidOperationException("no explorer")).Request(true, 5338);
        Assert.False(ok);
        Assert.Contains("no explorer", message);
        Assert.Null(Restart().Read());
    }

    [Fact]
    public void Without_the_script_nothing_is_written()
    {
        File.Delete(Path.Combine(_root, "restart-board.bat"));
        Assert.False(Restart().Request(true, 5338).Ok);
        Assert.Null(Restart().Read());
        Assert.Empty(_launched);
    }

    [Fact]
    public void On_a_mac_the_script_itself_is_launched()
    {
        File.WriteAllText(Path.Combine(_root, "restart-board.ps1"), "# script");
        var mac = new BoardRestart(new DashboardPaths(_root), _launched.Add, mac: true, windows: false);

        Assert.True(mac.Request(false, 5338).Ok);
        Assert.EndsWith("restart-board.ps1", Assert.Single(_launched));
    }

    [Fact]
    public void A_machine_with_no_launcher_is_refused_before_anything_is_written()
    {
        var other = new BoardRestart(new DashboardPaths(_root), _launched.Add, mac: false, windows: false);

        var (ok, message) = other.Request(true, 5338);
        Assert.False(ok);
        Assert.Contains("Windows and macOS", message);
        Assert.Null(other.Read());
        Assert.Empty(_launched);
    }

    [Fact]
    public void A_marker_the_script_rewrote_is_still_read()
    {
        // PowerShell 5.1's Set-Content -Encoding UTF8 writes a byte order mark.
        Restart().Request(true, 5338);
        var json = File.ReadAllText(Marker).Replace("\"draining\"", "\"starting\"");
        File.WriteAllText(Marker, json, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Assert.Equal("starting", Restart().Read()!.Phase);
    }
}
