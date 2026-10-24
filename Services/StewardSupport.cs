using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Whetstone.Models;
using Whetstone.Persistence;

namespace Whetstone.Services;

/// <summary>
/// The git a steward reaches, as an interface so its rules can be tested without a repository.
/// <see cref="TicketGitService"/> is the one real implementation.
/// </summary>
public interface IStewardGit
{
    Task<string?> BaseHeadAsync(string projectKey, CancellationToken ct = default);
    Task<IReadOnlyList<PlanUnit>> PlanAsync(string projectKey, CancellationToken ct = default);
    Task<IReadOnlyList<PlanUnit>> PlanOfAsync(string projectKey, StewardPlan plan, CancellationToken ct = default);
    Task<string?> FileAtBaseAsync(string projectKey, string path, CancellationToken ct = default);
    Task<BranchChange?> BranchChangeAsync(string projectKey, string branch, CancellationToken ct = default);
    Task<(string Text, bool Cut, int Length)?> BranchDiffAsync(string projectKey, string branch, string? path, int maxChars, CancellationToken ct = default);
    Task<int> AheadOfBaseAsync(string projectKey, string from, CancellationToken ct = default);
    Task<SyncFromResult> SyncFromAsync(string projectKey, string from, bool dryRun, CancellationToken ct = default);
    Task<(string? Sha, bool Unchanged, string? Refusal)> CommitFileAsync(string projectKey, string relativePath, string content, string message, CancellationToken ct = default);
    Task<bool> WorktreeAtTipAsync(string projectKey, string worktree, string branch, CancellationToken ct = default);
    Task<CheckRun> RunCheckAsync(string dir, string command, TimeSpan timeout, CancellationToken ct = default);
    Task<MergeResult> MergeAsync(string projectKey, string branch, string? title, string? description, CancellationToken ct = default);
    Task<RebaseResult> RebaseAsync(string projectKey, string branch, CancellationToken ct = default);
}

public partial class TicketGitService : IStewardGit { }

/// <summary>
/// Something about a finished ticket that the steward's merge has to weigh.
/// </summary>
/// <param name="Flag">A short name for it, stable enough to act on.</param>
/// <param name="Owner">It is the owner's to decide. The steward asks, and the ticket waits unmerged.</param>
/// <param name="Blocks">The work is not proved yet. The steward sends it back; it does not ask.</param>
/// <param name="Say">What to tell whoever acts on it, in a sentence.</param>
/// <param name="Detail">The files, packages or items it is about.</param>
public record StewardFlag(string Flag, bool Owner, bool Blocks, string? Say = null, IReadOnlyList<string>? Detail = null);

/// <summary>What a project's own flag script is told about the ticket it is asked to look at.</summary>
public record StewardFlagInput(
    string Project, int TicketId, string Branch, string BaseRef, string Repo, string? Worktree,
    IReadOnlyList<string> Files, bool SkipQaTesting, IReadOnlyList<string> QaShots, IReadOnlyList<string> AgreedSpecFiles);

/// <summary>A project's own reasons a merge should wait, beyond the ones every project shares.</summary>
public interface IStewardProjectFlags
{
    Task<IReadOnlyList<StewardFlag>> RunAsync(string projectKey, StewardFlagInput input, CancellationToken ct = default);
}

/// <summary>
/// Runs <c>data/projects/&lt;key&gt;/steward-flags.ps1</c>, the steward's counterpart to a project's
/// <c>guard.ps1</c>: what only that repo makes worth stopping a merge for - a migration that rewrites
/// data, a dependency nobody agreed to, a screen never shown at phone width.
/// <para>
/// Like the guard, it can only narrow. Its flags are added to the shared ones and can never remove
/// one, and a script that is present but will not run is itself a flag for the owner: told nothing
/// reliable about a merge, the steward does not make it. A project with no script simply has none.
/// </para>
/// <para>
/// The script reads one JSON object on stdin (<see cref="StewardFlagInput"/>) and writes a JSON
/// array of flags on stdout: <c>[{ "flag": "...", "owner": true, "blocks": false, "say": "...",
/// "detail": ["..."] }]</c>.
/// </para>
/// </summary>
public class StewardFlagScript : IStewardProjectFlags
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly DashboardPaths _paths;

    public StewardFlagScript(DashboardPaths paths) => _paths = paths;

    public async Task<IReadOnlyList<StewardFlag>> RunAsync(string projectKey, StewardFlagInput input, CancellationToken ct = default)
    {
        var script = Path.Combine(_paths.ProjectDir(projectKey), "steward-flags.ps1");
        if (!File.Exists(script)) return Array.Empty<StewardFlag>();

        StewardFlag Broken(string why) => new("project-flags-did-not-run", Owner: true, Blocks: false,
            Say: $"This project's own merge flags ({script}) could not be read, so nothing it guards against was checked: {why}");

        try
        {
            var psi = new ProcessStartInfo("powershell")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script }) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi)!;
            await proc.StandardInput.WriteAsync(JsonSerializer.Serialize(input, Json));
            proc.StandardInput.Close();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(2));
            var stdout = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = proc.StandardError.ReadToEndAsync(cts.Token);
            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return new[] { Broken("it did not finish within two minutes.") };
            }
            if (proc.ExitCode != 0) return new[] { Broken((await stderr).Trim() is { Length: > 0 } e ? e : $"exit code {proc.ExitCode}.") };

            var text = (await stdout).Trim();
            var at = text.IndexOf('[');
            if (at < 0) return text.Length == 0 ? Array.Empty<StewardFlag>() : new[] { Broken("it did not answer with a list.") };
            var flags = JsonSerializer.Deserialize<List<StewardFlag>>(text[at..], Json) ?? new();
            return flags.Where(f => !string.IsNullOrWhiteSpace(f.Flag)).ToList();
        }
        catch (Exception ex)
        {
            return new[] { Broken(ex.Message) };
        }
    }
}

/// <summary>The reasons a merge should wait that are true of every project.</summary>
public static class StewardFlags
{
    public static List<StewardFlag> BuiltIn(StewardConfig cfg, Ticket t, BranchChange change, DateTime? lastBackwardAt)
    {
        var flags = new List<StewardFlag>();

        var never = change.Files.Where(cfg.IsNeverMerged).ToList();
        if (never.Count > 0)
            flags.Add(new("never-merged-here", true, false,
                "These paths are never merged unattended: how the project deploys, what the fleet obeys, the plan itself.", never));

        // .env.example is documentation and holds no values; any other env file is a secret on its way in.
        var env = change.Files.Where(p =>
        {
            var name = p[(p.LastIndexOf('/') + 1)..];
            return (name == ".env" || name.StartsWith(".env.")) && !name.EndsWith(".example");
        }).ToList();
        if (env.Count > 0) flags.Add(new("env-file", true, false, "An env file is a secret on its way into the repo.", env));

        var expansions = t.WorkItems.Where(w => w.Scope == WorkItemScope.Expansion).Select(w => w.Text).ToList();
        if (expansions.Count > 0)
            flags.Add(new("behaviour-not-asked-for", true, false, "The plan carried work beyond what the ticket asked for.", expansions));

        var inspected = t.QaItems.Where(q => q.PassedByInspection).Select(q => q.Text).ToList();
        if (inspected.Count > 0)
            flags.Add(new("passed-by-inspection", false, false, "These checks passed by reading the code, not by running it.", inspected));

        if (!t.SkipQaTesting)
        {
            var unverified = t.QaItems.Where(q => !HandoffRequirements.IsVerified(q, lastBackwardAt))
                .Select(q => $"{q.Status}: {q.Text}").ToList();
            if (unverified.Count > 0)
                flags.Add(new("checks-not-passed", false, false, "These checks do not carry a current pass.", unverified));
        }

        var defects = t.GateFindings.Where(f => f.Ruling != GateRuling.NotADefect).Select(f => f.Title).ToList();
        if (defects.Count > 0)
            flags.Add(new("independent-findings-open", false, false, "The independent analysis raised these and they were not ruled out.", defects));

        return flags;
    }
}

/// <summary>
/// How far a unit of the plan is, as one figure a person can read at a glance.
/// <para>
/// Every outcome weighs the same. One its steward has called done is whole; one in progress is the
/// share of its tickets that is behind them, with one more counted while a ticket for it is still
/// to be written; anything else is nothing. It is an estimate for a person to read and decides
/// nothing - which is why it lives here and no rule consults it.
/// </para>
/// </summary>
public static class StewardProgress
{
    /// <summary>How much of a ticket is behind it at each phase: a guess at the share of its runs.</summary>
    private static double Worth(TicketPhase phase) => phase switch
    {
        TicketPhase.NeedsSpeculation => 0.05,
        TicketPhase.Speculating => 0.1,
        TicketPhase.NeedsApproval or TicketPhase.NeedsImplementation => 0.2,
        TicketPhase.Implementing => 0.4,
        TicketPhase.NeedsImplementationReview or TicketPhase.NeedsQa => 0.6,
        TicketPhase.QaTesting => 0.7,
        TicketPhase.NeedsRefinement => 0.8,
        TicketPhase.Refining or TicketPhase.NeedsPeerReview or TicketPhase.PeerReviewing => 0.85,
        TicketPhase.NeedsReview => 0.9,
        TicketPhase.Done => 1,
        _ => 0,
    };

    /// <summary>An outcome nobody has called done is never shown as done.</summary>
    private const double UnfinishedMax = 0.95;

    public record Estimate(int Unit, int Percent, int Done, int InProgress, int NotStarted, int Outcomes,
        IReadOnlyList<string> InProgressDetail, IReadOnlyList<string> NotStartedIds);

    public static IReadOnlyList<string> OutcomesOf(Ticket t) =>
        (t.PlanOutcomes ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static Estimate For(PlanUnit unit, IReadOnlyCollection<StewardOutcome> states, IReadOnlyCollection<Ticket> tickets)
    {
        var mine = tickets.Where(t => t.PlanUnit == unit.Number).ToList();
        double sum = 0;
        int done = 0;
        var inProgress = new List<string>();
        var notStarted = new List<string>();

        foreach (var o in unit.Outcomes)
        {
            var state = states.FirstOrDefault(s => s.Unit == unit.Number && s.Outcome == o);
            if (state?.State == StewardOutcomeState.Done) { sum += 1; done++; continue; }

            var its = mine.Where(t => OutcomesOf(t).Contains(o)).ToList();
            if (its.Count == 0) { notStarted.Add(o); continue; }

            // A blocked ticket is worth the phase it stopped in.
            var behind = its.Sum(t => Worth(t.Phase == TicketPhase.Blocked ? t.PhaseBeforeBlock ?? TicketPhase.Backlog : t.Phase));
            var share = Math.Min(UnfinishedMax, behind / (its.Count + (state?.MoreToFile == true ? 1 : 0)));
            sum += share;
            var open = its.Where(t => t.Phase != TicketPhase.Done).Select(t => $"#{t.Id} {t.Phase}").ToList();
            inProgress.Add($"{o} {Math.Round(share * 100)}%{(open.Count > 0 ? $" ({string.Join(", ", open)})" : "")}");
        }

        var percent = unit.Outcomes.Count == 0 ? 0 : (int)Math.Round(sum / unit.Outcomes.Count * 100);
        return new Estimate(unit.Number, percent, done, inProgress.Count, notStarted.Count, unit.Outcomes.Count, inProgress, notStarted);
    }

    /// <summary>The unit being worked: the first agreed one with an outcome not yet done, else the last agreed.</summary>
    public static PlanUnit? Current(IReadOnlyList<PlanUnit> plan, IReadOnlyCollection<StewardOutcome> states)
    {
        var agreed = plan.Where(u => u.Agreed).ToList();
        return agreed.FirstOrDefault(u => !u.Outcomes.All(o =>
                   states.Any(s => s.Unit == u.Number && s.Outcome == o && s.State == StewardOutcomeState.Done)))
               ?? agreed.LastOrDefault();
    }
}

/// <summary>
/// Marking that a project's steward has something to look at.
/// <para>
/// A static over the context rather than a method on the steward's service, because the thing
/// that most often has news for the steward is <see cref="TicketService"/> - a ticket arriving for
/// review, a ticket blocking - and the steward's service is built on top of that one.
/// </para>
/// </summary>
public static class StewardDue
{
    public static async Task<StewardState> StateAsync(DashboardDbContext db, string projectKey)
    {
        var s = await db.StewardStates.FindAsync(projectKey);
        if (s is null)
        {
            s = new StewardState { ProjectKey = projectKey };
            db.StewardStates.Add(s);
        }
        return s;
    }

    /// <summary>Record that a run is owed and why. The caller saves.</summary>
    public static async Task MarkAsync(DashboardDbContext db, string projectKey, string reason)
    {
        var s = await StateAsync(db, projectKey);
        var reasons = (s.DueReason ?? "").Split("; ", StringSplitOptions.RemoveEmptyEntries).ToList();
        if (!reasons.Contains(reason)) reasons.Add(reason);
        // Kept short: it is a hint for the run's opening line, and the board says the rest.
        s.DueReason = string.Join("; ", reasons.TakeLast(6));
        s.DueSince ??= DateTime.UtcNow;
    }

    private static List<string> Reasons(string? text) =>
        (text ?? "").Split("; ", StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>
    /// A run has ended properly: take out the reasons it was handed at its start, and keep any that
    /// arrived while it ran. The caller saves.
    /// </summary>
    public static void Settle(StewardState s)
    {
        var handed = Reasons(s.RunDueReason);
        var left = Reasons(s.DueReason).Where(r => !handed.Contains(r)).ToList();
        s.DueReason = left.Count > 0 ? string.Join("; ", left) : null;
        if (s.DueReason is null) s.DueSince = null;
        s.RunDueReason = null;
    }
}
