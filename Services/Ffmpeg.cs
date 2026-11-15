using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Whetstone.Services;

/// <summary>
/// The board's own use of ffmpeg: reading a discovery recording's length and cutting a proposal's
/// clip out of it (docs/discovery-track-plan.md, step 3). A missing ffmpeg is not an error anywhere:
/// a discovery shows its screenshots and says clips are not set up, the narrator's shape.
/// <para>
/// Not the ffmpeg the browser records with - Playwright brings its own, installed beside its
/// browsers (<c>playwright install ffmpeg</c>), and the Setup check asks after both.
/// </para>
/// </summary>
public static partial class Ffmpeg
{
    /// <summary>
    /// Where ffmpeg is: <c>ffmpegPath</c> in the global config when it names a file, else the board's
    /// own PATH - which the heartbeat and every worker inherit, so it is the one the Setup check reads.
    /// </summary>
    public static string? Find(Dictionary<string, JsonElement>? global)
    {
        if (global is not null && global.TryGetValue("ffmpegPath", out var v) && v.ValueKind == JsonValueKind.String
            && v.GetString() is { Length: > 0 } configured && File.Exists(configured))
            return configured;
        return SetupCheckService.FindOnPath("ffmpeg", Environment.GetEnvironmentVariable("PATH"));
    }

    [GeneratedRegex(@"Duration:\s*(\d+):(\d{2}):(\d{2}(?:\.\d+)?)")]
    private static partial Regex DurationLine();

    /// <summary>A video's length in seconds, read from ffmpeg's own description of it. Null when it cannot tell.</summary>
    public static async Task<double?> SecondsAsync(string ffmpeg, string file, CancellationToken ct = default)
    {
        var r = await RunAsync(ffmpeg, new[] { "-hide_banner", "-i", file }, TimeSpan.FromSeconds(30), ct);
        var m = DurationLine().Match(r.Output);
        if (!m.Success) return null;
        return int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60
            + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The arguments that cut <paramref name="spans"/> (seconds) out of <paramref name="input"/> and join
    /// them into one silent clip, no wider than 1280. H.264 in MP4 plays on every browser a phone has;
    /// <paramref name="webm"/> is the fallback for an ffmpeg built without libx264.
    /// </summary>
    public static IReadOnlyList<string> CutArgs(string input, IReadOnlyList<(double From, double To)> spans, string output, bool webm = false)
    {
        if (spans.Count == 0) throw new ArgumentException("Nothing to cut.", nameof(spans));
        static string S(double x) => x.ToString("0.###", CultureInfo.InvariantCulture);
        const string scale = "scale='trunc(min(1280,iw)/2)*2':-2";
        var args = new List<string> { "-hide_banner", "-nostdin", "-y" };
        if (spans.Count == 1)
        {
            args.AddRange(new[] { "-ss", S(spans[0].From), "-to", S(spans[0].To), "-i", input, "-an", "-vf", scale });
        }
        else
        {
            var parts = spans.Select((s, i) => $"[0:v]trim=start={S(s.From)}:end={S(s.To)},setpts=PTS-STARTPTS,{scale}[v{i}]");
            var joined = string.Concat(spans.Select((_, i) => $"[v{i}]")) + $"concat=n={spans.Count}:v=1:a=0[out]";
            args.AddRange(new[] { "-i", input, "-an", "-filter_complex", string.Join(";", parts) + ";" + joined, "-map", "[out]" });
        }
        args.AddRange(webm
            ? new[] { "-c:v", "libvpx", "-b:v", "800k", "-deadline", "realtime", "-cpu-used", "8", "-f", "webm", output }
            : new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "30", "-pix_fmt", "yuv420p", "-movflags", "+faststart", "-f", "mp4", output });
        return args;
    }

    /// <summary>
    /// Cut a clip to <paramref name="outputStem"/> plus <c>.mp4</c> (or <c>.webm</c> when this ffmpeg has
    /// no libx264). Written beside itself first and moved into place, so a half-cut file is never
    /// served. Returns the file written, or null with ffmpeg's last words in <paramref name="error"/>.
    /// </summary>
    public static async Task<(string? File, string? Error)> CutAsync(string ffmpeg, string input,
        IReadOnlyList<(double From, double To)> spans, string outputStem, CancellationToken ct = default)
    {
        string? last = null;
        foreach (var webm in new[] { false, true })
        {
            var final = outputStem + (webm ? ".webm" : ".mp4");
            var tmp = final + ".part";
            var r = await RunAsync(ffmpeg, CutArgs(input, spans, tmp, webm), TimeSpan.FromMinutes(3), ct);
            if (r.Code == 0 && File.Exists(tmp) && new FileInfo(tmp).Length > 0)
            {
                File.Move(tmp, final, overwrite: true);
                return (final, null);
            }
            try { File.Delete(tmp); } catch { /* best effort */ }
            last = Tail(r.Output);
        }
        return (null, last);
    }

    private static string Tail(string s)
    {
        var lines = s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" | ", lines.TakeLast(3));
    }

    public sealed record Run(int Code, string Output);

    private static async Task<Run> RunAsync(string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync(ct);
            var outp = p.StandardOutput.ReadToEndAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* gone */ }
                return new Run(-1, "ffmpeg timed out");
            }
            return new Run(p.ExitCode, (await outp) + (await err));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new Run(-1, ex.Message);
        }
    }
}
