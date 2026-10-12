using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Whetstone.Services;

/// <summary>
/// Notifications the dashboard itself has to send, as opposed to the heartbeat's.
/// <para>
/// Until a steward could ask its owner a question, every notification began in the orchestrator
/// script and the dashboard never needed to raise one. This does not add a second sender: it runs
/// <c>data/notify-send.ps1</c>, which goes through <c>notifiers.ps1</c> exactly as the heartbeat
/// and the Settings test do, so the channel, its settings and which events are ticked are read in
/// one place and a test that passes there is a test of this too.
/// </para>
/// <para>
/// Never throws and never holds a caller for long. A notification is the least important thing
/// any request does; whatever it was about is already recorded, and the answer says what happened
/// to the notice so the caller can put that on the record instead of guessing.
/// </para>
/// </summary>
public class NotificationSender
{
    private readonly DashboardPaths _paths;
    private readonly ILogger<NotificationSender> _log;

    public NotificationSender(DashboardPaths paths, ILogger<NotificationSender> log) => (_paths, _log) = (paths, log);

    /// <summary>What became of one notice: whether it left, and why not when it did not.</summary>
    public record Outcome(bool Sent, string Message);

    public virtual async Task<Outcome> SendAsync(string eventKey, string title, string message, CancellationToken ct = default)
    {
        var script = Path.Combine(_paths.DataRoot, "notify-send.ps1");
        if (!File.Exists(script)) return new(false, $"Script not found: {script}");

        var temp = Path.Combine(Path.GetTempPath(), $"whetstone-notice-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(temp, message, new UTF8Encoding(false), ct);
            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script,
                                         "-EventKey", eventKey, "-Title", title, "-MessageFile", temp })
                psi.ArgumentList.Add(arg);

            using var proc = Process.Start(psi)!;
            // Every channel times out at 10s; this is that plus PowerShell's own start-up.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            var stdout = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = proc.StandardError.ReadToEndAsync(cts.Token);
            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return new(false, "Timed out waiting for the channel to answer.");
            }

            var verdict = (await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('{'));
            if (verdict is not null)
            {
                try
                {
                    var v = JsonNode.Parse(verdict)!;
                    return new(v["sent"]?.GetValue<bool>() ?? false, v["message"]?.GetValue<string>() ?? "");
                }
                catch (System.Text.Json.JsonException) { }
            }
            var err = (await stderr).Trim();
            return new(false, err.Length > 0 ? err : "The notification script said nothing.");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not send the '{EventKey}' notification", eventKey);
            return new(false, ex.Message);
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }
}
