namespace Whetstone.Models;

public class HeartbeatEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Type { get; set; } = string.Empty;
    /// <summary>Which monitored project this event belongs to. Defaults to the active project when the sender omits it.</summary>
    public string? Project { get; set; }
    /// <summary>
    /// Which agent slot produced this (1..N), or null for the orchestrator's own events - sleeping,
    /// going idle, hibernating - which belong to the fleet rather than to any one agent. With
    /// several agents working at once the feed is interleaved, and this is the only thing that says
    /// whose line is whose.
    /// </summary>
    public int? Agent { get; set; }
    public string? TicketId { get; set; }
    public string? Phase { get; set; }
    public string? ToolName { get; set; }
    public string? Text { get; set; }
    public int? DurationSeconds { get; set; }
    public string? Error { get; set; }
    public Dictionary<string, object>? Meta { get; set; }
}

/// <summary>
/// Every kind of event the heartbeat actually sends. Kept to exactly that: an entry here that no
/// sender emits is a promise the readers believe, and the history page spent its whole life
/// assembling itself from ticket_start / skill_start / ticket_skip — three names that lived in this
/// list and nowhere else — so every run it drew came back reporting no work done.
/// </summary>
public static class EventTypes
{
    public const string CycleStart = "cycle_start";
    public const string CycleEnd = "cycle_end";
    public const string ToolCall = "tool_call";
    public const string TextOutput = "text_output";
    public const string Thinking = "thinking";
    public const string Sleep = "sleep";
    public const string Error = "error";
    public const string Summary = "summary";
    public const string StateChange = "state_change";
}
