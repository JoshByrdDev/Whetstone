namespace Whetstone.Services;

public partial class StewardService
{
    /// <summary>The reason a run asked for from the Steward page is handed, as its opening line says it.</summary>
    public const string OwnerAskedReason = "its owner asked for a run";

    /// <summary>
    /// The owner asks for a run now, from the Steward page. This only says whether one may start and
    /// records why it is owed; starting it is <see cref="StewardRunner"/>'s, because what starts it
    /// depends on whether the heartbeat is going.
    /// <para>
    /// Refused while any project's steward has a run open, not only this one's: there is one berth
    /// for the steward (slot 99), and two runs in it at once would be two agents sending events as
    /// the same slot.
    /// </para>
    /// </summary>
    public async Task<StewardReply> OwnerAsksForRunAsync(string projectKey, CancellationToken ct = default)
    {
        var cfg = Config(projectKey);
        if (Off(cfg, projectKey) is { } off) return off;

        var open = (await _leases.ActiveAsync())
            .FirstOrDefault(l => l.ResourceKey.StartsWith("steward:", StringComparison.Ordinal));
        if (open is not null)
        {
            var whose = open.ResourceKey["steward:".Length..];
            return Refuse("running", whose == projectKey
                ? "A run is already going for this project."
                : $"The steward is working on {whose} just now, and it runs one project at a time.");
        }

        await StewardDue.MarkAsync(_db, projectKey, OwnerAskedReason);
        await _db.SaveChangesAsync(ct);
        return new StewardReply("recorded");
    }
}
