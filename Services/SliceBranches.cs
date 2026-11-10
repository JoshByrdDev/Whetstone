using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Whetstone.Models;
using Whetstone.Persistence;

namespace Whetstone.Services;

/// <summary>
/// A slice's branch, and which branch a ticket's work is based on (docs/slice-branches.md,
/// section 2).
/// <para>
/// On a project with <see cref="ProjectConfig.SliceBranchesOn"/>, a ticket of an open slice is
/// forked from, rebased onto and measured against that slice's branch. A ticket of no slice, and
/// one of a released slice, works against the project's base as before. Everything here answers
/// null for "the project's base", so a caller that gets null does exactly what it always did -
/// which is the whole of what a project without the switch ever sees.
/// </para>
/// <para>
/// The branch is a local one, so the local-or-origin rule (<c>Resolve-BaseRef</c> and
/// <c>BaseRefAsync</c>) resolves it to the local ref without being told anything new. The worker
/// is handed it on the claim (<see cref="TicketService.ClaimResult.BaseBranch"/>) rather than
/// working it out, so a worker host is told the same answer.
/// </para>
/// </summary>
public static class SliceBranches
{
    /// <summary>The prefix every slice branch carries. Nothing else on a project may start with it.</summary>
    public const string Prefix = "slice/";

    /// <summary>Whether <paramref name="branch"/> is a slice's branch, which is local only and never on origin.</summary>
    public static bool IsSliceBranch(string? branch) =>
        branch is not null && branch.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// A slice's branch name from its number and spec file: <c>slice/13-cards</c> for
    /// <c>docs/slices/13-cards.md</c>. The number is written with at least two digits so the
    /// branches sort in order, and the rest of the file's name is kept as a slug.
    /// </summary>
    public static string NameFor(int number, string file)
    {
        var stem = Path.GetFileNameWithoutExtension(file.Replace('\\', '/').Split('/').Last()) ?? "";
        // The number leads the name in the usual layout; it is taken out so it is not written twice,
        // and only there - "03-u3" keeps its "u3".
        var rest = Regex.Replace(stem, $@"^0*{number}(?!\d)", "", RegexOptions.None, TimeSpan.FromSeconds(1));
        var slug = Regex.Replace(rest.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (slug.Length > 40) slug = slug[..40].TrimEnd('-');
        return $"{Prefix}{number:00}{(slug.Length > 0 ? "-" + slug : "")}";
    }

    /// <summary>
    /// The branch a ticket of <paramref name="unit"/> is based on, by the table in section 2: the
    /// slice's own branch while the slice is open, and null - the project's base - for a ticket of
    /// no slice, of a released slice, of a slice with no branch, or on a project without the switch.
    /// </summary>
    public static string? BaseFor(ProjectConfig cfg, int? unit, StewardUnit? row)
    {
        if (!cfg.SliceBranchesOn || unit is null || row is null) return null;
        if (row.Unit != unit) return null;
        if (row.ReleasedAt is not null) return null;
        return string.IsNullOrWhiteSpace(row.Branch) ? null : row.Branch.Trim();
    }

    /// <summary>The slice row a ticket belongs to, or null when it belongs to none.</summary>
    public static async Task<StewardUnit?> RowOfAsync(DashboardDbContext db, ProjectConfig cfg, Ticket ticket,
        CancellationToken ct = default)
    {
        if (ticket.PlanUnit is not { } unit) return null;
        // Until the plan goes (piece 7), a ticket carries the plan key it was filed under; one that
        // carries none is of the plan being worked.
        var plan = string.IsNullOrWhiteSpace(ticket.PlanKey) ? cfg.Steward.Plan.Key : ticket.PlanKey;
        return await db.StewardUnits.AsNoTracking()
            .FirstOrDefaultAsync(u => u.ProjectKey == ticket.ProjectKey && u.Plan == plan && u.Unit == unit, ct);
    }

    /// <summary>The branch <paramref name="ticket"/> is based on, or null for the project's base.</summary>
    public static async Task<string?> ForTicketAsync(DashboardDbContext db, ProjectConfig cfg, Ticket ticket,
        CancellationToken ct = default)
    {
        if (!cfg.SliceBranchesOn) return null;
        return BaseFor(cfg, ticket.PlanUnit, await RowOfAsync(db, cfg, ticket, ct));
    }

    /// <summary>
    /// The branch the ticket whose branch is <paramref name="branch"/> is based on, or null for the
    /// project's base - including when no ticket of the project has that branch.
    /// </summary>
    public static async Task<string?> ForBranchAsync(DashboardDbContext db, ProjectConfig cfg, string branch,
        CancellationToken ct = default)
    {
        if (!cfg.SliceBranchesOn || string.IsNullOrWhiteSpace(branch)) return null;
        var ticket = await db.Tickets.AsNoTracking()
            .Where(t => t.ProjectKey == cfg.Name && t.Branch == branch)
            .OrderByDescending(t => t.Id)
            .FirstOrDefaultAsync(ct);
        return ticket is null ? null : await ForTicketAsync(db, cfg, ticket, ct);
    }
}

/// <summary>
/// How the git service, which outlives every request, asks which branch a ticket's work is based
/// on. Null answers the project's base.
/// </summary>
public interface ITicketBases
{
    Task<string?> BaseOfBranchAsync(ProjectConfig cfg, string branch, CancellationToken ct = default);
}

/// <summary>The board's own <see cref="ITicketBases"/>: a short-lived database scope per question.</summary>
public sealed class TicketBases(IServiceScopeFactory scopes) : ITicketBases
{
    public async Task<string?> BaseOfBranchAsync(ProjectConfig cfg, string branch, CancellationToken ct = default)
    {
        if (!cfg.SliceBranchesOn) return null;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();
        return await SliceBranches.ForBranchAsync(db, cfg, branch, ct);
    }
}
