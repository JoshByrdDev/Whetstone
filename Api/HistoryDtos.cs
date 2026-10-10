namespace Whetstone.Api;

/// <summary>
/// One worker run, as the history timeline shows it.
/// <para>
/// <paramref name="Partial"/> marks a row rebuilt from the event log instead of read from the run's
/// own report. A <c>cycle_start</c> event only ever carried the ticket, project and phase, so every
/// field past those is null on such a row and the UI says "no run record" rather than drawing a
/// zero that reads like a measurement.
/// </para>
/// </summary>
public record HistoryCycle(
    DateTime StartTime,
    /// <summary>Null when the run never ended - a worker that was killed, or one still going.</summary>
    DateTime? EndTime,
    int? DurationSeconds,
    int? TicketId,
    string? Project,
    string? Phase,
    /// <summary>Where the ticket landed when the run handed it off. Null on a partial row.</summary>
    string? PhaseAfter,
    string? Skill,
    bool Resumed,
    string? Model,
    string? Outcome,
    int? Mutations,
    int? NumTurns,
    /// <summary>Input + output + cache writes. Cache reads are counted apart, as everywhere else.</summary>
    int? Tokens,
    int? CacheReadTokens,
    bool Partial);

public record HistoryPhaseCount(string Phase, int Count);

public record HistoryStats(
    int TotalCycles,
    int CyclesToday,
    int TicketsToday,
    int AvgDurationSeconds,
    long TokensToday,
    int Completed,
    int Timeouts,
    int Errors,
    /// <summary>How many rows in the window came from the event log rather than a run report.</summary>
    int PartialCycles,
    string? TopPhase,
    IReadOnlyList<HistoryPhaseCount> PhaseBreakdown);

public record HistoryResponse(int Days, IReadOnlyList<HistoryCycle> Cycles, HistoryStats Stats);
