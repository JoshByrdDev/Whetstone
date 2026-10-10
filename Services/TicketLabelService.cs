using Whetstone.Models;
using Whetstone.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Whetstone.Services;

/// <summary>One label, plus how many tickets are wearing it. What the picker offers.</summary>
public record LabelUsage(int Id, string Name, int Count);

/// <summary>A label as it rides on a ticket — the chip's whole content.</summary>
public record LabelRef(int Id, string Name);

/// <summary>
/// The owner's filing tags: assigning one, taking one off, and keeping the offered list honest.
/// <para>
/// Scoped like <see cref="TicketService"/> and deliberately separate from it. Every method on that
/// class is reachable by a worker through some MCP tool; nothing here is reachable by one at all,
/// and keeping the two apart is what makes that checkable by reading the file list rather than by
/// auditing an argument list (see <see cref="Label"/>).
/// </para>
/// </summary>
public class TicketLabelService
{
    private readonly DashboardDbContext _db;
    public TicketLabelService(DashboardDbContext db) => _db = db;

    /// <summary>
    /// Tidy what the owner typed, or return null if there is nothing left worth storing.
    /// <para>
    /// Runs of whitespace collapse to one space rather than being rejected the way Jira rejects
    /// them: this is one person's filing system, and "needs design" reads better than "needs-design"
    /// to the only person who will ever read it. Collapsing is what stops "tech  debt" and
    /// "tech debt" becoming two labels that look identical in a list — the same failure the NOCASE
    /// collation exists to prevent for case, and the reason both happen here rather than being left
    /// to whoever is typing.
    /// </para>
    /// </summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var collapsed = string.Join(" ", raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (collapsed.Length == 0) return null;
        return collapsed.Length > Label.MaxLength ? collapsed[..Label.MaxLength].TrimEnd() : collapsed;
    }

    /// <summary>Every label in use, most-worn first — the order the picker wants to offer them in.</summary>
    public async Task<List<LabelUsage>> ListAsync() =>
        // Ordered before the projection, not after: SQLite can sort on the correlated count, but it
        // cannot sort on a field of a record EF has not built yet.
        await _db.Labels
            .OrderByDescending(l => l.Assignments.Count).ThenBy(l => l.Name)
            .Select(l => new LabelUsage(l.Id, l.Name, l.Assignments.Count))
            .ToListAsync();

    /// <summary>
    /// Who is wearing what, for every ticket that wears anything. One query rather than one per
    /// ticket: the board draws chips on a few hundred rows at a time and asks again every few
    /// seconds, and rows with no labels — the majority — cost nothing here because they are absent.
    /// </summary>
    public async Task<Dictionary<int, List<LabelRef>>> AssignmentsAsync()
    {
        var rows = await _db.TicketLabels
            .OrderBy(x => x.Label!.Name)
            .Select(x => new { x.TicketId, x.LabelId, x.Label!.Name })
            .ToListAsync();

        return rows.GroupBy(r => r.TicketId)
            .ToDictionary(g => g.Key, g => g.Select(r => new LabelRef(r.LabelId, r.Name)).ToList());
    }

    /// <summary>This ticket's labels, alphabetical — chips have no meaningful order of their own.</summary>
    public async Task<List<LabelRef>> ForTicketAsync(int ticketId) =>
        await _db.TicketLabels.Where(x => x.TicketId == ticketId)
            .OrderBy(x => x.Label!.Name)
            .Select(x => new LabelRef(x.LabelId, x.Label!.Name))
            .ToListAsync();

    /// <summary>
    /// Put a label on a ticket, creating it if this is the first time the owner has used the word —
    /// the one operation behind both halves of the picker. Typing a new name and clicking an
    /// existing one arrive here identically, because "does this already exist" is a question the
    /// database can answer better than the UI can.
    /// <para>
    /// Idempotent: assigning a label the ticket already wears is a no-op, not an error. The picker
    /// hides what is already on, but a double-click and a stale list both land here anyway.
    /// </para>
    /// </summary>
    /// <returns>The ticket's labels after the change, or null if there is no such ticket.</returns>
    public async Task<List<LabelRef>?> AssignAsync(int ticketId, string name)
    {
        var clean = Normalize(name);
        if (clean is null) return null;
        if (!await _db.Tickets.AnyAsync(t => t.Id == ticketId)) return null;

        // EF translates this to a NOCASE comparison because the column carries that collation, so
        // typing "UI" when "ui" exists finds it rather than colliding with the unique index.
        var label = await _db.Labels.FirstOrDefaultAsync(l => l.Name == clean);
        if (label is null)
        {
            label = new Label { Name = clean };
            _db.Labels.Add(label);
            await _db.SaveChangesAsync();
        }

        if (!await _db.TicketLabels.AnyAsync(x => x.TicketId == ticketId && x.LabelId == label.Id))
        {
            _db.TicketLabels.Add(new TicketLabel { TicketId = ticketId, LabelId = label.Id });
            await _db.SaveChangesAsync();
        }

        return await ForTicketAsync(ticketId);
    }

    /// <summary>
    /// Take a label off a ticket, and delete the label itself if that was the last ticket wearing
    /// it. The garbage collection is the feature rather than tidiness: the offered list IS the set
    /// in use, so a name typed wrong stops being suggested the moment it stops being worn, and there
    /// is never a screen full of labels nobody uses to go and prune.
    /// </summary>
    /// <returns>The ticket's labels after the change, or null if it was not wearing that one.</returns>
    public async Task<List<LabelRef>?> UnassignAsync(int ticketId, int labelId)
    {
        var link = await _db.TicketLabels.FirstOrDefaultAsync(x => x.TicketId == ticketId && x.LabelId == labelId);
        if (link is null) return null;

        _db.TicketLabels.Remove(link);
        await _db.SaveChangesAsync();

        if (!await _db.TicketLabels.AnyAsync(x => x.LabelId == labelId))
        {
            var orphan = await _db.Labels.FindAsync(labelId);
            if (orphan is not null) { _db.Labels.Remove(orphan); await _db.SaveChangesAsync(); }
        }

        return await ForTicketAsync(ticketId);
    }

    /// <summary>
    /// Rename a label everywhere it is worn — the fix for a typo already filed on nine tickets.
    /// <para>
    /// Renaming onto a name that already exists MERGES rather than failing: every ticket wearing the
    /// old one is moved to the new one (skipping any that already wear both) and the old label is
    /// deleted. That is what someone renaming "gui" to "ui" means, and refusing it would leave them
    /// unpicking the duplicate by hand across every ticket.
    /// </para>
    /// </summary>
    /// <returns>The surviving label, or null if the id is unknown or the new name is empty.</returns>
    public async Task<LabelUsage?> RenameAsync(int labelId, string name)
    {
        var clean = Normalize(name);
        if (clean is null) return null;

        var label = await _db.Labels.FindAsync(labelId);
        if (label is null) return null;

        var existing = await _db.Labels.FirstOrDefaultAsync(l => l.Name == clean && l.Id != labelId);
        if (existing is null)
        {
            label.Name = clean;
            await _db.SaveChangesAsync();
            return await UsageAsync(label.Id);
        }

        // Merge into the label that already owns the name. A ticket wearing both loses the duplicate
        // link rather than tripping the composite key.
        var moving = await _db.TicketLabels.Where(x => x.LabelId == labelId).ToListAsync();
        var already = await _db.TicketLabels.Where(x => x.LabelId == existing.Id)
            .Select(x => x.TicketId).ToListAsync();

        foreach (var link in moving)
        {
            _db.TicketLabels.Remove(link);
            if (!already.Contains(link.TicketId))
                _db.TicketLabels.Add(new TicketLabel { TicketId = link.TicketId, LabelId = existing.Id, CreatedAt = link.CreatedAt });
        }
        _db.Labels.Remove(label);
        await _db.SaveChangesAsync();
        return await UsageAsync(existing.Id);
    }

    /// <summary>Retire a label from every ticket at once. The assignments cascade with it.</summary>
    public async Task<bool> DeleteAsync(int labelId)
    {
        var label = await _db.Labels.FindAsync(labelId);
        if (label is null) return false;
        _db.Labels.Remove(label);
        await _db.SaveChangesAsync();
        return true;
    }

    private Task<LabelUsage?> UsageAsync(int labelId) =>
        _db.Labels.Where(l => l.Id == labelId)
            .Select(l => new LabelUsage(l.Id, l.Name, l.Assignments.Count))
            .FirstOrDefaultAsync();
}
