using System.Diagnostics;
using System.Text;

namespace Whetstone.Services;

/// <summary>
/// One read-only, schema-constrained agent run: start the provider's CLI, hand it the prompt on
/// stdin, narrate its tool calls as they happen, stop it at a timeout or a cancel, and unwrap the
/// structured result.
/// <para>
/// Shared by the Classroom analysis and the walkthrough script written from it, which differ only
/// in prompt and schema. Lifted out of <see cref="AnalysisRunner"/> unchanged, comments and all:
/// every branch below is there because a run once failed that way.
/// </para>
/// </summary>
public static class AgentJsonRunner
{
    /// <param name="Outcome">The provider's unwrapped answer, or null when the run did not finish cleanly.</param>
    /// <param name="Error">Why there is no outcome, as a sentence for the reader. Null on success.</param>
    public record Result(AgentOutcome? Outcome, string? Error, int Steps, int DurationMs);

    /// <summary>
    /// How much of the run's own output is worth keeping.
    /// <para>
    /// A streamed run says everything out loud — every tool call and every tool RESULT, which
    /// includes the text of files it read. Holding all of that to reach the one closing line at the
    /// end would put a whole read of a repo in memory for the length of the run. Only the tail is
    /// kept, and the tail is where the answer is: both providers close with the line the document
    /// and the accounting are read out of.
    /// </para>
    /// </summary>
    private const int StdoutTailBytes = 1_000_000;

    /// <param name="what">What this run is, for the timeout sentence ("the analysis timed out...").</param>
    /// <exception cref="OperationCanceledException">Only when <paramref name="ct"/> was cancelled -
    /// a person who stopped the run is not told their own click failed.</exception>
    public static async Task<Result> RunAsync(AgentProvider provider, string workDir, string scratch,
        string prompt, string jsonSchema, IRunProgress progress, TimeSpan timeout, string what,
        ILogger log, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        // Written from the stdout reader thread, read once the process has exited.
        var steps = 0;
        Result Done(AgentOutcome? outcome, string? error) =>
            new(outcome, error, Volatile.Read(ref steps), (int)sw.ElapsedMilliseconds);

        // Recreated per run: whatever the last one left is never what this one should read.
        try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); } catch { /* best effort */ }
        Directory.CreateDirectory(scratch);

        AgentRun? agentRun = null;
        try
        {
            agentRun = provider.BuildAnalysisRun(workDir, jsonSchema, scratch);

            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = CliPath.Resolve(agentRun.FileName),
                    WorkingDirectory = workDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    // The document is read back out of this stream. Left unset, it is decoded in
                    // the console's code page and every dash the run wrote is stored as mojibake.
                    StandardOutputEncoding = ProcessText.Utf8,
                    StandardErrorEncoding = ProcessText.Utf8,
                    // And the prompt goes out the same way: a ticket title with an em dash in it
                    // should reach the agent as one, not as a question mark.
                    StandardInputEncoding = ProcessText.Utf8,
                },
                EnableRaisingEvents = true,
            };

            foreach (var arg in agentRun.Arguments) proc.StartInfo.ArgumentList.Add(arg);
            foreach (var (k, v) in agentRun.Environment) proc.StartInfo.Environment[k] = v;

            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                stdout.AppendLine(e.Data);
                TrimToTail(stdout);

                // The live half. Everything else about this run is invisible until the process
                // exits, so a line the provider recognises as a tool call is the only evidence
                // anyone waiting has that the run is moving rather than wedged.
                try
                {
                    if (provider.ReadProgress(e.Data) is { } step)
                    {
                        Interlocked.Increment(ref steps);
                        progress.Step(step.ToString());
                    }
                }
                catch (Exception ex)
                {
                    // Narration is not the work. A provider that chokes on one odd line loses that
                    // line from the feed and nothing else.
                    log.LogDebug(ex, "Could not read a progress line from the {What} run", what);
                }
            };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

            proc.Start();
            // Through the provider on the way out: one whose CLI cannot be handed a schema as a
            // flag has to ask for the shape here instead, and this is the only place that knows
            // both the prompt and the schema.
            await proc.StandardInput.WriteAsync(provider.DecorateAnalysisPrompt(prompt, jsonSchema));
            proc.StandardInput.Close();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            // One token for both ways this run can end early. The timeout is ours; the outer token
            // is the person clicking "stop" on the queue panel — and linking them means a cancel
            // walks out through the same kill path the timeout already used, rather than needing a
            // second handle on the process.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                return Done(null, $"the {what} timed out after {timeout.TotalMinutes:0} minutes");
            }

            if (proc.ExitCode != 0)
            {
                // A CLI that fails without writing to stderr - an expired login, a model it will
                // not serve - still explains itself on STDOUT, in the same envelope a successful
                // run answers in. Reporting the exit code alone throws away the only sentence that
                // says what actually went wrong, which is the whole of what the reader needs.
                var err = Tail(stderr);
                var reason = err.Length > 0
                    ? err
                    : provider.ReadAnalysis(agentRun, stdout.ToString()).Error ?? "";

                // What the reader gets is a sentence, and some failures do not fit in one. A run
                // that could not satisfy the schema spent its last minute having tool calls rejected
                // one after another, and every word of that is in the stream - which is discarded
                // the moment this returns, leaving the CLI's own transcript as the only surviving
                // copy. Logged here so the next one of these costs a look at the log instead.
                log.LogError(
                    "{Executable} exited with code {Code} during the {What}: {Reason}\nstderr: {Stderr}\nstdout tail: {Stdout}",
                    provider.Executable, proc.ExitCode, what, reason, err, Tail(stdout, 4000));

                return Done(null, $"{provider.Executable} exited with code {proc.ExitCode}. {reason}".TrimEnd());
            }

            return Done(provider.ReadAnalysis(agentRun, stdout.ToString()), null);
        }
        finally
        {
            // The scratch directory can hold a copy of the CLI's login, so it does not outlive the
            // run that needed it.
            if (agentRun != null) AgentProvider.Cleanup(agentRun);
        }
    }

    /// <summary>
    /// Drop the front of the buffer once it has grown well past the tail worth keeping. In chunks
    /// rather than per line: this runs on the stdout reader thread for every line a chatty run
    /// prints, and trimming one line at a time would copy the whole buffer each time.
    /// </summary>
    private static void TrimToTail(StringBuilder sb)
    {
        if (sb.Length <= StdoutTailBytes * 2) return;
        // The cut lands mid-line, which costs nothing: every reader of this buffer parses line by
        // line and steps over the ones it cannot read.
        sb.Remove(0, sb.Length - StdoutTailBytes);
    }

    public static string Tail(StringBuilder sb, int max = 400)
    {
        var s = sb.ToString().Trim();
        return s.Length <= max ? s : "..." + s[^max..];
    }
}
