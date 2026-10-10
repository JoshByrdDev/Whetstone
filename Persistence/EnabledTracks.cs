using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Whetstone.Models;

namespace Whetstone.Persistence;

/// <summary>
/// The per-project "enabled tracks" setting, and the one rule that guards changing it.
///
/// <para>
/// The setting itself is presentation: it says which lanes a project's board draws and which tracks
/// its New Ticket form offers, and NOTHING in the pipeline reads it — not the claim, not
/// <see cref="PhaseMachine"/>, not a skill. A ticket's track is fixed when it is created, and a
/// setting that could contradict it afterwards would only be a second, quieter answer to a question
/// already settled.
/// </para>
/// <para>
/// Which is exactly why turning one off is not the harmless act it looks like. The tickets on that
/// track keep their phases, the claim keeps handing them to agents and the heartbeat keeps walking
/// them forward; the only thing that changes is that no lane is drawn for the stage they are
/// standing in. Hidden would quietly come to mean lost. So the rule lives at the EDIT rather than
/// at the read: finish them or delete them first.
/// </para>
/// </summary>
public static class EnabledTracks
{
    /// <summary>
    /// What counts as still riding on a track: everything that is not Done,
    /// <see cref="TicketPhase.Blocked"/> included — a blocked ticket is one somebody still has to
    /// come back to, and it is the case most likely to be forgotten, so the one most worth
    /// refusing over.
    ///
    /// <para>
    /// An expression rather than a predicate method so both queries below can hand the SAME clause
    /// to EF. Two hand-written copies of "not Done" is precisely how a refusal and the count that
    /// explains it drift apart, and the symptom is a checkbox the form says is locked and the server
    /// then lets through.
    /// </para>
    /// </summary>
    public static readonly Expression<Func<Ticket, bool>> Unfinished = t => t.Phase != TicketPhase.Done;

    /// <summary>The tracks every project starts on, and the floor no project may go below.</summary>
    public static readonly TicketTrack[] Default = { TicketTrack.Delivery };

    /// <summary>
    /// Track names as a caller gave them, normalised: unrecognised names dropped, duplicates
    /// dropped, enum order restored so the settings form always offers them the same way round.
    /// Null when there were no names at all, which is how a request that says nothing about tracks
    /// leaves the stored list alone — distinct from an empty list, which is a request to have none.
    /// </summary>
    public static TicketTrack[]? Parse(IEnumerable<string?>? names) =>
        names?
            .Select(n => Enum.TryParse<TicketTrack>(n?.Trim(), ignoreCase: true, out var t) ? t : (TicketTrack?)null)
            .Where(t => t is not null)
            .Select(t => t!.Value)
            .Distinct()
            .OrderBy(t => t)
            .ToArray();

    /// <summary>What a track is called in a sentence addressed to a person.</summary>
    public static string Label(TicketTrack track) =>
        track == TicketTrack.Review ? "peer review" : "delivery";

    /// <summary>
    /// Why <paramref name="project"/> may not move from <paramref name="current"/> to
    /// <paramref name="wanted"/>, or null when it may. Null <paramref name="wanted"/> is "the
    /// request did not mention tracks", which is always allowed.
    /// </summary>
    public static async Task<string?> RefusalAsync(
        string project, IReadOnlyCollection<TicketTrack> current, TicketTrack[]? wanted, DashboardDbContext db)
    {
        if (wanted is null) return null;

        // The other way into the same dead end: a project with nothing enabled draws a board with no
        // lanes and has no track to offer the New Ticket form, so it can never be given work again.
        if (wanted.Length == 0) return "A project needs at least one track enabled.";

        foreach (var track in current.Where(t => !wanted.Contains(t)))
        {
            var open = await db.Tickets.Where(Unfinished)
                .CountAsync(t => t.ProjectKey == project && t.Track == track);
            if (open > 0)
                return $"{open} {Label(track)} ticket{(open == 1 ? " is" : "s are")} still open on this "
                     + "project. Finish or delete them before turning that track off.";
        }
        return null;
    }

    /// <summary>
    /// Unfinished ticket counts for every project and track, keyed <c>"&lt;project&gt;|&lt;track&gt;"</c>.
    /// What lets the settings form grey out a box that will not untick yet and say why, instead of
    /// letting the save fail — the refusal above is still the enforcement.
    ///
    /// <para>
    /// One request-wide read rather than two queries per project: this is a few hundred rows, and
    /// per-project counting is how a settings page becomes a query storm. Grouped in memory on
    /// purpose — the projection is two small columns, and expressing the grouping in SQL over a
    /// string-converted enum buys nothing but a translation this would have to be right about.
    /// </para>
    /// </summary>
    public static async Task<Dictionary<string, int>> OpenByTrackAsync(DashboardDbContext db)
    {
        var rows = await db.Tickets.Where(Unfinished)
            .Select(t => new { t.ProjectKey, t.Track })
            .ToListAsync();
        return rows.GroupBy(r => Key(r.ProjectKey, r.Track)).ToDictionary(g => g.Key, g => g.Count());
    }

    /// <summary>The key <see cref="OpenByTrackAsync"/> files a count under, and reads one back by.</summary>
    public static string Key(string project, TicketTrack track) => $"{project}|{track}";
}
