using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Whetstone.Services.Walkthrough;

/// <summary>
/// Voices walkthrough lines with Kokoro-82M, entirely on this machine, by running
/// <c>data/narrator/narrate.mjs</c> under Node.
/// <para>
/// Kokoro runs in Node (<c>kokoro-js</c>), not .NET, so this is a subprocess the way git is. Every
/// line is stored under a hash of voice and spoken text: a line that has been voiced before - in
/// this deck's last version, or anywhere - is never synthesised again, which matters because
/// synthesis runs at roughly half real time on a CPU.
/// </para>
/// </summary>
public class Narrator
{
    public const string DefaultVoice = "af_heart";
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(20);

    private readonly DashboardPaths _paths;
    private readonly ILogger<Narrator> _log;

    public Narrator(DashboardPaths paths, ILogger<Narrator> log)
    {
        _paths = paths;
        _log = log;
    }

    /// <summary>One line to voice: its sentences, already in spoken form (see <see cref="Pronunciation"/>).</summary>
    public record Line(IReadOnlyList<string> Sentences)
    {
        public string Id(string voice) => ClipId(voice, Sentences);
    }

    /// <summary>What happened to one line: its clip and length, or why there is none.</summary>
    public record Voiced(string? Clip, double Seconds, string? Error);

    public static string ClipId(string voice, IEnumerable<string> sentences) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(voice + "\n" + string.Join("\n", sentences)))).ToLowerInvariant();

    public string ClipPath(string clip) => Path.Combine(_paths.WalkthroughClipsDir, clip + ".wav");

    /// <summary>Why the narrator cannot run on this machine, or null when it can.</summary>
    public string? Unavailable()
    {
        var script = Path.Combine(_paths.NarratorDir, "narrate.mjs");
        if (!File.Exists(script)) return $"the narrator script is missing ({script})";
        if (!Directory.Exists(Path.Combine(_paths.NarratorDir, "node_modules", "kokoro-js")))
            return $"the narrator is not installed - run `npm install` in {_paths.NarratorDir}";
        return null;
    }

    /// <summary>
    /// Voice every line not already on disk. Never throws for a line that fails - that line comes
    /// back with its error and the rest are still voiced; a walkthrough with one silent beat is
    /// still a walkthrough.
    /// </summary>
    public async Task<IReadOnlyList<Voiced>> VoiceAsync(IReadOnlyList<Line> lines, string voice,
        IRunProgress progress, CancellationToken ct)
    {
        var ids = lines.Select(l => l.Id(voice)).ToList();
        var errors = new Dictionary<string, string>();
        var missing = lines.Select((l, i) => (l, id: ids[i])).Where(x => !File.Exists(ClipPath(x.id)))
            .GroupBy(x => x.id).Select(g => g.First()).ToList();

        if (missing.Count > 0)
        {
            if (Unavailable() is { } why) return lines.Select(_ => new Voiced(null, 0, why)).ToList();
            progress.Step($"Voicing {missing.Count} line{(missing.Count == 1 ? "" : "s")}");
            var request = JsonSerializer.Serialize(new
            {
                voice,
                outDir = _paths.WalkthroughClipsDir,
                items = missing.Select(m => new { id = m.id, sentences = m.l.Sentences }),
            });
            var failed = await RunNodeAsync(request, missing.Count, errors, progress, ct);
            if (failed is not null)
                foreach (var m in missing) errors.TryAdd(m.id, failed);
        }

        return ids.Select(id =>
        {
            if (errors.TryGetValue(id, out var e)) return new Voiced(null, 0, e);
            var seconds = WavSeconds(ClipPath(id));
            return seconds is double s ? new Voiced(id, s, null) : new Voiced(null, 0, "the voiced clip is missing or unreadable");
        }).ToList();
    }

    /// <returns>A sentence when the whole run failed; null when it ran (per-line errors land in <paramref name="errors"/>).</returns>
    private async Task<string?> RunNodeAsync(string request, int count, Dictionary<string, string> errors,
        IRunProgress progress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("node")
        {
            WorkingDirectory = _paths.NarratorDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = ProcessText.Utf8,
            StandardOutputEncoding = ProcessText.Utf8,
            StandardErrorEncoding = ProcessText.Utf8,
        };
        psi.ArgumentList.Add("narrate.mjs");

        using var proc = new Process { StartInfo = psi };
        var stderr = new StringBuilder();
        var done = 0;
        var finished = false;
        proc.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            try
            {
                using var doc = JsonDocument.Parse(e.Data);
                var r = doc.RootElement;
                if (r.TryGetProperty("done", out JsonElement _)) { finished = true; return; }
                var id = r.GetProperty("id").GetString()!;
                if (r.TryGetProperty("error", out var err)) errors[id] = err.GetString() ?? "the voice could not read this line";
                progress.Step($"Voiced {Interlocked.Increment(ref done)} of {count}");
            }
            catch (Exception ex) { _log.LogDebug(ex, "Unreadable narrator line: {Line}", e.Data); }
        };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };

        try { proc.Start(); }
        catch (Exception ex) { return $"could not start node to voice the walkthrough: {ex.Message}"; }

        await proc.StandardInput.WriteAsync(request);
        proc.StandardInput.Close();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            return $"voicing timed out after {Timeout.TotalMinutes:0} minutes";
        }

        if (proc.ExitCode != 0 || !finished)
        {
            var err = stderr.ToString().Trim();
            _log.LogError("The narrator exited with code {Code}: {Stderr}", proc.ExitCode, err);
            return $"the narrator exited with code {proc.ExitCode}. {(err.Length > 300 ? "..." + err[^300..] : err)}".TrimEnd();
        }
        return null;
    }

    /// <summary>A WAV's length from its header: the data chunk's size over the byte rate.</summary>
    public static double? WavSeconds(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            using var r = new BinaryReader(f);
            if (new string(r.ReadChars(4)) != "RIFF") return null;
            r.ReadInt32();
            if (new string(r.ReadChars(4)) != "WAVE") return null;
            int byteRate = 0;
            while (f.Position + 8 <= f.Length)
            {
                var id = new string(r.ReadChars(4));
                var size = r.ReadInt32();
                if (id == "fmt ")
                {
                    r.ReadInt16(); r.ReadInt16(); r.ReadInt32();
                    byteRate = r.ReadInt32();
                    f.Position += size - 12;
                }
                else if (id == "data") return byteRate > 0 ? size / (double)byteRate : null;
                else f.Position += size;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }
}
