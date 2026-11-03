using System.Collections.Concurrent;

namespace Whetstone.Services;

/// <summary>
/// A steward's merges in flight, kept apart from the calls that asked for them.
/// <para>
/// A merge is as slow as the project's own checks, and a project's checks can run for minutes. A
/// tool call cannot: the agent's client drops one that has said nothing for five minutes, and some
/// drop it far sooner. While the merge ran inside the call, the dropped call cancelled the checks
/// with it, the steward was told nothing, called again, and was dropped again - a ticket whose
/// checks ran long could not be merged at all, and every run reported the board as not answering.
/// </para>
/// <para>
/// So the merge runs here, in a scope of its own, and a call only waits on it. One that is still
/// checking when the wait is up answers "checking"; the next call for the same ticket joins the
/// same merge rather than starting another, and whichever call is there when it finishes is given
/// the answer. A call that drops takes nothing down with it.
/// </para>
/// <para>
/// An answer nobody collected is kept, so a merge that finished between calls is not lost - but only
/// for the ticket as it was checked. Each merge is stamped with the ticket's phase, branch tip and
/// distance from the base when it starts, and a kept answer is handed out only while that stamp
/// still holds. Without that, a dropped call's "behind" outlived the rebase that answered it: the
/// steward rebased, asked again, was told "behind" again, and the rebase then had nothing to do.
/// </para>
/// </summary>
public class StewardMerges
{
    /// <summary>How long one call waits when nothing says which client is calling: short enough for the least patient one.</summary>
    public TimeSpan Wait { get; set; } = ShortWait;

    /// <summary>
    /// Codex ends a tool call at 60 seconds by default, so a call it makes waits 45 and answers
    /// "checking". Claude's client drops a call only after five minutes of silence, so a call from
    /// it waits four: every "checking" is another turn of the model reading the whole run so far,
    /// and a 20-minute database suite was costing a steward twenty-odd of them per merge. Gemini's
    /// limit has not been measured here, so it keeps the short wait until it is.
    /// </summary>
    public static TimeSpan WaitFor(string provider) =>
        provider == "claude" ? TimeSpan.FromMinutes(4) : ShortWait;

    private static readonly TimeSpan ShortWait = TimeSpan.FromSeconds(45);

    /// <summary>The wait for the client calling, read each call; null uses <see cref="Wait"/>.</summary>
    private readonly Func<TimeSpan>? _waitFor;

    /// <summary>
    /// How long an answer nobody collected is still the answer. Past this the ticket may no longer
    /// be what was checked, and the next call starts again.
    /// </summary>
    public TimeSpan KeepFor { get; set; } = TimeSpan.FromMinutes(20);

    private sealed record InFlight(Task<StewardReply> Task, DateTime StartedAt, string? Stamp)
    {
        public DateTime? FinishedAt;
    }

    private readonly ConcurrentDictionary<(string Project, int Ticket), InFlight> _merges = new();
    private readonly Func<string, int, Task<StewardReply>> _merge;
    private readonly Func<string, int, Task<string?>>? _stamp;

    public StewardMerges(IServiceScopeFactory scopes) : this(async (project, ticketId) =>
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<StewardService>().MergeAsync(project, ticketId, CancellationToken.None);
    }, async (project, ticketId) =>
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<StewardService>().MergeStampAsync(project, ticketId, CancellationToken.None);
    })
    {
        // The steward's provider as the settings name it now. Read each call rather than once, so
        // a provider changed in Settings is the one the next run's calls are waited on for.
        _waitFor = () =>
        {
            using var scope = scopes.CreateScope();
            var registry = scope.ServiceProvider.GetRequiredService<ProjectRegistry>();
            return WaitFor(AgentRoles.Resolve(registry.GetGlobalConfig(), AgentRoles.Steward).Provider);
        };
    }

    /// <param name="merge">The merge itself.</param>
    /// <param name="stamp">What the ticket is right now, as far as a merge's answer depends on it; null keeps any answer for <see cref="KeepFor"/>.</param>
    public StewardMerges(Func<string, int, Task<StewardReply>> merge, Func<string, int, Task<string?>>? stamp = null)
    {
        _merge = merge;
        _stamp = stamp;
    }

    /// <summary>
    /// Start the ticket's merge, or join the one already going, and wait on it for <see cref="Wait"/>.
    /// <paramref name="ct"/> ends the waiting and never the merge.
    /// </summary>
    public async Task<StewardReply> MergeAsync(string projectKey, int ticketId, CancellationToken ct = default)
    {
        var key = (projectKey, ticketId);
        var stamp = _stamp is null ? null : await _stamp(projectKey, ticketId);
        // A finished answer nobody collected is the answer only for the ticket it was given about -
        // except "merged", which changed the ticket itself and must still reach the steward. A merge
        // still running is joined whatever: it is past every check that reads the ticket.
        if (_merges.TryGetValue(key, out var old) && old.FinishedAt is { } at
            && (DateTime.UtcNow - at > KeepFor
                || (_stamp is not null && stamp != old.Stamp && old.Task.Result.Answer != "merged")))
            _merges.TryRemove(new KeyValuePair<(string, int), InFlight>(key, old));

        var merge = _merges.GetOrAdd(key, _ =>
        {
            var started = new InFlight(Task.Run(() => _merge(projectKey, ticketId)), DateTime.UtcNow, stamp);
            started.Task.ContinueWith(_ => started.FinishedAt = DateTime.UtcNow, TaskScheduler.Default);
            return started;
        });

        if (await Task.WhenAny(merge.Task, Task.Delay(_waitFor?.Invoke() ?? Wait, ct)) != merge.Task)
        {
            ct.ThrowIfCancellationRequested();
            var minutes = (DateTime.UtcNow - merge.StartedAt).TotalMinutes;
            return new StewardReply("checking",
                $"The merge's checks are still running ({minutes:0.#} minutes so far). Nothing is wrong and nothing is merged yet. " +
                "Call merge for this ticket again for the answer: it joins this same merge and does not start another. " +
                "Do other work in between if there is any, and do not end the run before it has answered.");
        }

        _merges.TryRemove(new KeyValuePair<(string, int), InFlight>(key, merge));
        return await merge.Task;
    }
}
