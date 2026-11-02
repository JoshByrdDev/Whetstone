namespace Whetstone.Models;

/// <summary>Where an experiment stands. The finer stages (enrolling, landing, waiting) are derived from its data.</summary>
public enum ExperimentStatus
{
    Running,
    /// <summary>Stopped by the owner or by a guardrail. Every later run gets control's settings.</summary>
    Stopped,
    /// <summary>The owner has declared a winner.</summary>
    Concluded,
}

/// <summary>Who stopped an experiment.</summary>
public enum ExperimentStopKind
{
    Human,
    /// <summary>The board, because the variant clearly broke a quality guardrail (docs/bugs-and-experiments-plan.md, 2.6).</summary>
    Guardrail,
}

/// <summary>
/// An A/B test of one phase's skill or agent settings on one project
/// (docs/bugs-and-experiments-plan.md, section 2): two arms, tickets assigned to one for good the
/// first time they are claimed into the phase, measured per ticket to Done.
/// <para>
/// Started, stopped and concluded only by the owner, through REST routes with no MCP tool behind
/// them. The one move the board makes by itself is a guardrail stop, which can only make the
/// pipeline more like it was.
/// </para>
/// </summary>
public class Experiment
{
    public int Id { get; set; }

    public string ProjectKey { get; set; } = "";

    /// <summary>The skill under test: speculate, implement, qa-test, refine or review-pr.</summary>
    public string Skill { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>What the owner expects the variant to change, in their words.</summary>
    public string? Hypothesis { get; set; }

    public ExperimentStatus Status { get; set; } = ExperimentStatus.Running;

    /// <summary>Enrolment closes once each arm has this many tickets.</summary>
    public int MinTicketsPerArm { get; set; } = 8;

    /// <summary>How long after the last enrolled ticket reaches Done before a verdict, so escaped bugs have time to surface.</summary>
    public int WaitDays { get; set; } = 21;

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StoppedAt { get; set; }
    public ExperimentStopKind? StopKind { get; set; }
    public string? StopReason { get; set; }

    public DateTime? ConcludedAt { get; set; }
    /// <summary><c>control</c>, <c>variant</c> or <c>none</c>.</summary>
    public string? Winner { get; set; }
    public string? ConclusionNote { get; set; }

    public List<ExperimentArm> Arms { get; set; } = new();
}

/// <summary>One arm: a name and the overrides a run in it gets. A null override leaves the setting as configured.</summary>
public class ExperimentArm
{
    public const string Control = "control";
    public const string Variant = "variant";

    public int Id { get; set; }
    public int ExperimentId { get; set; }
    public Experiment? Experiment { get; set; }

    /// <summary><see cref="Control"/> or <see cref="Variant"/>.</summary>
    public string Key { get; set; } = Control;

    public string Name { get; set; } = "";

    /// <summary>A file name under <c>skills/&lt;skill&gt;/variants/</c> in the project's folder, without <c>.md</c>.</summary>
    public string? SkillVariant { get; set; }

    public string? Model { get; set; }
    public string? Effort { get; set; }
    public string? Provider { get; set; }
}

/// <summary>A ticket's arm in an experiment, for good: every later pass of the phase runs the same one.</summary>
public class ExperimentAssignment
{
    public int Id { get; set; }

    public int ExperimentId { get; set; }
    public Experiment? Experiment { get; set; }

    public int TicketId { get; set; }

    public string ArmKey { get; set; } = ExperimentArm.Control;

    /// <summary>1 for the first ticket enrolled, and so on. Pairs (1,2), (3,4)... hold one of each arm.</summary>
    public int Ordinal { get; set; }

    public DateTime AssignedAt { get; set; } = DateTime.UtcNow;
}
