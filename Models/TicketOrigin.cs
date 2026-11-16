namespace Whetstone.Models;

/// <summary>
/// Where a ticket came from, as attribution counts it (docs/intake-plan.md, section 6): what made
/// the board file it, not who pressed the button. Stored by name, like every enum on a ticket.
/// <para>
/// Written once, at filing, by the code that files - <see cref="TicketOrigin.Of"/> inside
/// <c>TicketService.CreateAsync</c> - and never changed after. No MCP tool and no client route
/// writes it.
/// </para>
/// </summary>
public enum TicketOriginKind
{
    /// <summary>A person filed it. What every ticket from before this was recorded reads as.</summary>
    Owner,
    /// <summary>A steward, or the board on a slice's behalf, filed it from a slice.</summary>
    Steward,
    /// <summary>A worker's run noticed it in passing (<see cref="Ticket.FoundOn"/> is set).</summary>
    FoundInRun,
    /// <summary>Filed because a Done ticket's problem came back (<see cref="Ticket.RegressionOf"/> is set).</summary>
    Regression,
    /// <summary>Filed by intake, from a project's own error reports and alarms.</summary>
    Intake,
}

/// <summary>
/// The facts about a filing that decide its origin. Handed to <c>TicketService.CreateAsync</c> by
/// every caller that is not a person filing by hand, so each of those facts is on the row before
/// the origin is worked out from them.
/// </summary>
/// <param name="CreatedBy">Who filed it when it was not a person (<see cref="Ticket.CreatedBy"/>).</param>
/// <param name="FoundOn">The ticket (or discovery) whose run noticed this.</param>
/// <param name="RegressionOf">The Done ticket whose problem came back.</param>
/// <param name="IntakeChannel">Set only by intake: <see cref="IntakeChannels"/>.</param>
/// <param name="IntakeIssueId">Set only by intake: the <c>IntakeIssue</c> row it was filed for.</param>
public sealed record TicketFiling(
    TransitionActor? CreatedBy = null,
    int? FoundOn = null,
    int? RegressionOf = null,
    string? IntakeChannel = null,
    int? IntakeIssueId = null)
{
    public static readonly TicketFiling ByOwner = new();
}

/// <summary>The channels attribution counts intake tickets by. Lower case, as the plan names them.</summary>
public static class IntakeChannels
{
    public const string Sentry = "sentry";
    public const string CloudWatchAlarm = "cloudwatch-alarm";
    public const string Storm = "storm";
    public const string Unrecognised = "unrecognised";
}

public static class TicketOrigin
{
    /// <summary>
    /// The one place a ticket's origin is derived. Most specific first: an intake ticket that is a
    /// regression is <see cref="TicketOriginKind.Intake"/> - what it came from is production - and
    /// a defect a run found on a steward's ticket is <see cref="TicketOriginKind.FoundInRun"/>.
    /// </summary>
    public static TicketOriginKind Of(Ticket t) =>
        !string.IsNullOrWhiteSpace(t.IntakeChannel) ? TicketOriginKind.Intake
        : t.RegressionOf is not null ? TicketOriginKind.Regression
        : t.FoundOn is not null ? TicketOriginKind.FoundInRun
        : t.CreatedBy is TransitionActor.Steward or TransitionActor.System ? TicketOriginKind.Steward
        : TicketOriginKind.Owner;

    /// <summary>The plan's spelling, for the API and the marts: <c>found-in-run</c>, not <c>FoundInRun</c>.</summary>
    public static string Name(TicketOriginKind k) => k switch
    {
        TicketOriginKind.FoundInRun => "found-in-run",
        _ => k.ToString().ToLowerInvariant(),
    };
}
