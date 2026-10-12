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
/// </summary>
public class StewardMerges
{
    /// <summary>How long one call waits. Short enough for the least patient client to still be listening.</summary>
    public TimeSpan Wait { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>
    /// How long an answer nobody collected is still the answer. Past this the ticket may no longer
    /// be what was checked, and the next call starts again.
    /// </summary>
    public TimeSpan KeepFor { get; set; } = TimeSpan.FromMinutes(20);

    private sealed record InFlight(Task<StewardReply> Task, DateTime StartedAt)
    {
        public DateTime? FinishedAt;
    }

    private readonly ConcurrentDictionary<(string Project, int Ticket), InFlight> _merges = new();
    private readonly Func<string, int, Task<StewardReply>> _merge;

    public StewardMerges(IServiceScopeFactory scopes) : this(async (project, ticketId) =>
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<StewardService>().MergeAsync(project, ticketId, CancellationToken.None);
    })
    { }

    public StewardMerges(Func<string, int, Task<StewardReply>> merge) => _merge = merge;

    /// <summary>
    /// Start the ticket's merge, or join the one already going, and wait on it for <see cref="Wait"/>.
    /// <paramref name="ct"/> ends the waiting and never the merge.
    /// </summary>
    public async Task<StewardReply> MergeAsync(string projectKey, int ticketId, CancellationToken ct = default)
    {
        var key = (projectKey, ticketId);
        if (_merges.TryGetValue(key, out var old) && old.FinishedAt is { } at && DateTime.UtcNow - at > KeepFor)
            _merges.TryRemove(new KeyValuePair<(string, int), InFlight>(key, old));

        var merge = _merges.GetOrAdd(key, _ =>
        {
            var started = new InFlight(Task.Run(() => _merge(projectKey, ticketId)), DateTime.UtcNow);
            started.Task.ContinueWith(_ => started.FinishedAt = DateTime.UtcNow, TaskScheduler.Default);
            return started;
        });

        if (await Task.WhenAny(merge.Task, Task.Delay(Wait, ct)) != merge.Task)
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
