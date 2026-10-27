using Whetstone.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Whetstone.Persistence;

public class DashboardDbContext : DbContext
{
    public DashboardDbContext(DbContextOptions<DashboardDbContext> options) : base(options) { }

    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<WorkItem> WorkItems => Set<WorkItem>();
    public DbSet<QaItem> QaItems => Set<QaItem>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<QaShot> QaShots => Set<QaShot>();
    public DbSet<NoteAttachment> NoteAttachments => Set<NoteAttachment>();
    public DbSet<PhaseTransition> PhaseTransitions => Set<PhaseTransition>();
    public DbSet<CycleRun> CycleRuns => Set<CycleRun>();
    public DbSet<TicketDependency> TicketDependencies => Set<TicketDependency>();
    public DbSet<ResourceLease> ResourceLeases => Set<ResourceLease>();
    public DbSet<TicketAnalysis> TicketAnalyses => Set<TicketAnalysis>();
    public DbSet<AnalysisWalkthrough> AnalysisWalkthroughs => Set<AnalysisWalkthrough>();
    public DbSet<UnitReport> UnitReports => Set<UnitReport>();
    public DbSet<Label> Labels => Set<Label>();
    public DbSet<TicketLabel> TicketLabels => Set<TicketLabel>();
    public DbSet<ReviewTarget> ReviewTargets => Set<ReviewTarget>();
    public DbSet<ReviewComment> ReviewComments => Set<ReviewComment>();
    public DbSet<GateFinding> GateFindings => Set<GateFinding>();
    public DbSet<StewardQuestion> StewardQuestions => Set<StewardQuestion>();
    public DbSet<StewardEntry> StewardEntries => Set<StewardEntry>();
    public DbSet<StewardUnit> StewardUnits => Set<StewardUnit>();
    public DbSet<StewardState> StewardStates => Set<StewardState>();
    public DbSet<StewardOutcome> StewardOutcomes => Set<StewardOutcome>();
    public DbSet<StewardClearance> StewardClearances => Set<StewardClearance>();
    public DbSet<StewardDecision> StewardDecisions => Set<StewardDecision>();
    public DbSet<StewardDecisionReplace> StewardDecisionReplaces => Set<StewardDecisionReplace>();
    public DbSet<StewardDecisionConflict> StewardDecisionConflicts => Set<StewardDecisionConflict>();
    public DbSet<StewardInstruction> StewardInstructions => Set<StewardInstruction>();
    public DbSet<StewardFollowUp> StewardFollowUps => Set<StewardFollowUp>();

    /// <summary>
    /// Every <see cref="DateTime"/> in this database is UTC. This is what makes the type system agree.
    /// <para>
    /// SQLite has no date type: values go in as text and come back as <c>DateTimeKind.Unspecified</c>,
    /// which loses the one fact that matters. Unspecified serializes to JSON with no <c>Z</c>, and a
    /// browser reads a zoneless ISO string as LOCAL time — so on a UTC-5 machine every timestamp the
    /// API returned was five hours in the future. Long-ago timestamps survived that (five hours does
    /// not change "2d ago"), which is why it stayed hidden until a page started showing things that
    /// had happened minutes earlier and rendered every one of them as "just now".
    /// </para>
    /// <para>
    /// Reading stamps the Kind back on. Writing converts a Local value and leaves Unspecified alone:
    /// every writer here passes <c>DateTime.UtcNow</c>, so an unlabelled value is already UTC and
    /// "helpfully" shifting it by the server's offset would corrupt it.
    /// </para>
    /// </summary>
    public class UtcConverter : ValueConverter<DateTime, DateTime>
    {
        public UtcConverter() : base(
            v => v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : v,
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc)) { }
    }

    public class NullableUtcConverter : ValueConverter<DateTime?, DateTime?>
    {
        public NullableUtcConverter() : base(
            v => v.HasValue && v.Value.Kind == DateTimeKind.Local ? v.Value.ToUniversalTime() : v,
            v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v) { }
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        b.Properties<DateTime>().HaveConversion<UtcConverter>();
        b.Properties<DateTime?>().HaveConversion<NullableUtcConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Ticket>(e =>
        {
            e.Property(t => t.Phase).HasConversion<string>().HasMaxLength(32);
            e.Property(t => t.PhaseBeforeBlock).HasConversion<string>().HasMaxLength(32);
            e.Property(t => t.ProjectKey).HasMaxLength(128);
            // Stored as the name, like every other enum here, so reordering the enum can never
            // reinterpret rows already on disk. The claim's ordering happens in memory on the
            // materialized list, so nothing depends on this sorting in SQL.
            e.Property(t => t.Track).HasConversion<string>().HasMaxLength(16);
            e.Property(t => t.CreatedBy).HasConversion<string>().HasMaxLength(16);
            e.Property(t => t.PlanOutcomes).HasMaxLength(256);
            e.Property(t => t.PlanKey).HasMaxLength(64);
            e.HasIndex(t => t.Phase);
            e.HasIndex(t => t.ProjectKey);

            e.HasMany(t => t.WorkItems).WithOne(w => w.Ticket!).HasForeignKey(w => w.TicketId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(t => t.QaItems).WithOne(q => q.Ticket!).HasForeignKey(q => q.TicketId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(t => t.Comments).WithOne(c => c.Ticket!).HasForeignKey(c => c.TicketId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(t => t.QaShots).WithOne(s => s.Ticket!).HasForeignKey(s => s.TicketId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(t => t.GateFindings).WithOne(f => f.Ticket!).HasForeignKey(f => f.TicketId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<GateFinding>(e =>
        {
            e.Property(f => f.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(f => f.Ruling).HasConversion<string>().HasMaxLength(16);
            // Every read is "this analysis's findings on this ticket, in order".
            e.HasIndex(f => new { f.TicketId, f.AnalysisId, f.Order });
        });

        b.Entity<QaShot>().Property(s => s.ImageFile).HasMaxLength(64);

        b.Entity<QaItem>().Property(q => q.Status).HasConversion<string>().HasMaxLength(16);
        b.Entity<WorkItem>().Property(w => w.Scope).HasConversion<string>().HasMaxLength(16);
        b.Entity<Comment>(e =>
        {
            e.Property(c => c.Author).HasConversion<string>().HasMaxLength(16);
            e.Property(c => c.Phase).HasConversion<string>().HasMaxLength(32);
            e.HasMany(c => c.Attachments).WithOne(a => a.Comment!).HasForeignKey(a => a.CommentId)
                .OnDelete(DeleteBehavior.Cascade);
            // Every read of this is "what has the owner said that nobody has answered yet", on one
            // ticket, on the hot path of both the claim and the hand-off check.
            e.HasIndex(c => new { c.TicketId, c.AcknowledgedAt });
        });

        b.Entity<NoteAttachment>(e =>
        {
            e.Property(a => a.FileName).HasMaxLength(260);
            e.Property(a => a.StoredFile).HasMaxLength(64);
            e.Property(a => a.ContentType).HasMaxLength(128);
        });

        // Metrics tables. Both cascade with the ticket: a deleted ticket's history is not worth
        // keeping as orphaned rows, and the aggregates these feed are about the pipeline's behaviour
        // over time rather than a permanent audit trail.
        b.Entity<PhaseTransition>(e =>
        {
            e.Property(p => p.From).HasConversion<string>().HasMaxLength(32);
            e.Property(p => p.To).HasConversion<string>().HasMaxLength(32);
            e.Property(p => p.Actor).HasConversion<string>().HasMaxLength(16);
            e.Property(p => p.ProjectKey).HasMaxLength(128);
            e.Property(p => p.Track).HasConversion<string>().HasMaxLength(16);
            // Every metric over this table is "some window, some project, grouped by phase".
            e.HasIndex(p => p.At);
            e.HasIndex(p => new { p.ProjectKey, p.At });
            e.HasOne(p => p.Ticket).WithMany().HasForeignKey(p => p.TicketId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ResourceLease>(e =>
        {
            e.Property(l => l.ResourceKey).HasMaxLength(128);
            e.Property(l => l.Skill).HasMaxLength(64);
            e.Property(l => l.HolderLabel).HasMaxLength(64);
            e.Property(l => l.ReleaseReason).HasMaxLength(256);
            e.HasIndex(l => l.TicketId);
            // The invariant, enforced by the database rather than by every caller counting correctly:
            // one holder per (resource, slot) at a time. Filtered to live leases so the history of
            // released ones can pile up in the same table without colliding.
            e.HasIndex(l => new { l.ResourceKey, l.Slot })
                .IsUnique()
                .HasFilter("\"ReleasedAt\" IS NULL");
            // No foreign key to Ticket on purpose: a lease outliving its ticket must still be
            // visible and still expire, rather than vanishing and leaving the resource looking free
            // while something is potentially still using it.
        });

        b.Entity<TicketDependency>(e =>
        {
            e.Property(d => d.Origin).HasConversion<string>().HasMaxLength(16);
            e.Property(d => d.BlockerIssueKey).HasMaxLength(64);
            e.Property(d => d.BlockerStatus).HasMaxLength(64);
            e.Property(d => d.BlockerStatusCategory).HasMaxLength(32);
            e.HasIndex(d => d.TicketId);
            e.HasOne(d => d.Ticket).WithMany().HasForeignKey(d => d.TicketId).OnDelete(DeleteBehavior.Cascade);
            // Deliberately NO foreign key on BlockerTicketId: deleting a ticket that something else
            // is waiting on must not silently delete the record of the wait. The blocked ticket keeps
            // a dangling row, which reads as "waiting on a ticket that is gone" - a thing worth
            // seeing, and worth a person deciding about, rather than a row that quietly disappears.
        });

        // Classroom analyses. Cascades with the ticket for the same reason the metrics tables do —
        // teaching material about work that no longer exists is not worth keeping as orphaned rows.
        // Enums are stored as their names like everywhere else here, so reordering either enum can
        // never silently reinterpret rows that are already on disk.
        b.Entity<TicketAnalysis>(e =>
        {
            e.Property(a => a.PhaseAtGeneration).HasConversion<string>().HasMaxLength(32);
            e.Property(a => a.Lens).HasConversion<string>().HasMaxLength(16);
            e.Property(a => a.ProjectKey).HasMaxLength(128);
            e.Property(a => a.HeadSha).HasMaxLength(64);
            e.Property(a => a.Model).HasMaxLength(64);
            // Every read is "the analyses of this ticket, newest first".
            e.HasIndex(a => new { a.TicketId, a.GeneratedAt });
            e.HasOne(a => a.Ticket).WithMany().HasForeignKey(a => a.TicketId).OnDelete(DeleteBehavior.Cascade);
        });

        // One walkthrough per analysis, gone with it. Status by name, like the analysis enums above.
        b.Entity<AnalysisWalkthrough>(e =>
        {
            e.Property(w => w.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(w => w.Voice).HasMaxLength(32);
            e.Property(w => w.Model).HasMaxLength(64);
            e.HasIndex(w => w.AnalysisId).IsUnique();
            e.HasOne(w => w.Analysis).WithMany().HasForeignKey(w => w.AnalysisId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(w => w.UnitReportId).IsUnique();
            e.HasOne(w => w.UnitReport).WithMany().HasForeignKey(w => w.UnitReportId).OnDelete(DeleteBehavior.Cascade);
        });

        // Unit reports hang off no row: a unit is a plan's file, not an entity, and a report must
        // outlive the tickets it was written from being deleted.
        b.Entity<UnitReport>(e =>
        {
            e.Property(r => r.ProjectKey).HasMaxLength(128);
            e.Property(r => r.Plan).HasMaxLength(128);
            e.Property(r => r.BaseRef).HasMaxLength(256);
            e.Property(r => r.FromSha).HasMaxLength(64);
            e.Property(r => r.ToSha).HasMaxLength(64);
            e.Property(r => r.Model).HasMaxLength(64);
            // Every read is "the reports of this unit, newest first".
            e.HasIndex(r => new { r.ProjectKey, r.Plan, r.Unit, r.GeneratedAt });
        });

        // The owner's filing tags. Nothing here hangs off the Ticket entity on purpose — see
        // Models/Labels.cs: the ticket aggregate is what every worker receives over MCP, and these
        // are the one thing on the board the pipeline must never be able to read.
        b.Entity<Label>(e =>
        {
            // NOCASE is what makes "UI" and "ui" one label rather than two that look identical in a
            // list. It has to be on the COLUMN, not just on the unique index: every lookup here is
            // by name, and a case-sensitive comparison would happily create the duplicate that the
            // index then refuses to save.
            e.Property(l => l.Name).HasMaxLength(Label.MaxLength).UseCollation("NOCASE");
            e.HasIndex(l => l.Name).IsUnique();
        });

        b.Entity<TicketLabel>(e =>
        {
            e.HasKey(x => new { x.TicketId, x.LabelId });
            e.HasOne(x => x.Ticket).WithMany().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Label).WithMany(l => l.Assignments).HasForeignKey(x => x.LabelId).OnDelete(DeleteBehavior.Cascade);
            // The composite key already indexes (TicketId, LabelId); this is the other direction,
            // which is what the usage counts and the orphan check read.
            e.HasIndex(x => x.LabelId);
        });

        // The Review track's two tables. Both cascade with the ticket: staged feedback about a
        // branch whose ticket is gone has nobody to send it and nothing to anchor it to.
        b.Entity<ReviewTarget>(e =>
        {
            // The ticket's own id IS the key. A 1:1 modelled as a collection of at most one is a
            // shape every reader then has to check the count of.
            e.HasKey(r => r.TicketId);
            e.Property(r => r.PrId).HasMaxLength(64);
            e.Property(r => r.PrUrl).HasMaxLength(1024);
            e.Property(r => r.SourceBranch).HasMaxLength(256);
            e.Property(r => r.TargetBranch).HasMaxLength(256);
            e.Property(r => r.PrAuthor).HasMaxLength(128);
            e.Property(r => r.ReviewedAtSha).HasMaxLength(64);
            e.Property(r => r.HeadSha).HasMaxLength(64);
            e.Property(r => r.Verdict).HasConversion<string>().HasMaxLength(32);
            e.HasOne(r => r.Ticket).WithOne(t => t.ReviewTarget!)
                .HasForeignKey<ReviewTarget>(r => r.TicketId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ReviewComment>(e =>
        {
            e.Property(c => c.FilePath).HasMaxLength(1024);
            e.Property(c => c.Side).HasConversion<string>().HasMaxLength(8);
            e.Property(c => c.Severity).HasConversion<string>().HasMaxLength(16);
            e.Property(c => c.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(c => c.Author).HasConversion<string>().HasMaxLength(16);
            e.Property(c => c.AnchorSha).HasMaxLength(64);
            e.Property(c => c.ExternalThreadId).HasMaxLength(128);
            // Every read is "this ticket's staged comments, in display order".
            e.HasIndex(c => new { c.TicketId, c.Order });
            e.HasOne(c => c.Ticket).WithMany(t => t.ReviewComments)
                .HasForeignKey(c => c.TicketId).OnDelete(DeleteBehavior.Cascade);
        });

        // A steward's own tables. None of them hangs off the Ticket entity: a question or a call
        // may name a ticket, but it belongs to the project and outlives the ticket it was about.
        b.Entity<StewardQuestion>(e =>
        {
            e.Property(q => q.ProjectKey).HasMaxLength(128);
            e.Property(q => q.Key).HasMaxLength(64);
            e.Property(q => q.RemindedOn).HasMaxLength(10);
            e.Property(q => q.PlanKey).HasMaxLength(40);
            e.Property(q => q.Decisions).HasMaxLength(400);
            e.Property(q => q.Touches).HasMaxLength(400);
            // Asked once, for good: this is what refuses a run that has forgotten it already asked.
            e.HasIndex(q => new { q.ProjectKey, q.Key }).IsUnique();
        });

        b.Entity<StewardEntry>(e =>
        {
            e.Property(x => x.ProjectKey).HasMaxLength(128);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Release).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => new { x.ProjectKey, x.Kind, x.At });
        });

        b.Entity<StewardOutcome>(e =>
        {
            e.Property(o => o.ProjectKey).HasMaxLength(128);
            e.Property(o => o.Outcome).HasMaxLength(16);
            e.Property(o => o.State).HasConversion<string>().HasMaxLength(16);
            e.Property(o => o.Plan).HasMaxLength(64);
            e.HasIndex(o => new { o.ProjectKey, o.Plan, o.Unit, o.Outcome }).IsUnique();
        });

        b.Entity<StewardUnit>(e =>
        {
            e.Property(u => u.ProjectKey).HasMaxLength(128);
            e.Property(u => u.Plan).HasMaxLength(64);
            e.Property(u => u.ReleasedSha).HasMaxLength(64);
            e.HasIndex(u => new { u.ProjectKey, u.Plan, u.Unit }).IsUnique();
        });

        b.Entity<StewardClearance>(e =>
        {
            e.Property(c => c.ProjectKey).HasMaxLength(128);
            e.Property(c => c.Flag).HasMaxLength(128);
            e.Property(c => c.ChangeId).HasMaxLength(64);
            e.Property(c => c.Sha).HasMaxLength(64);
            e.HasIndex(c => new { c.ProjectKey, c.TicketId, c.Flag, c.ChangeId }).IsUnique();
        });

        // The register of decisions. A number is the project's for good, and what one replaces
        // hangs off the newer decision only.
        b.Entity<StewardDecision>(e =>
        {
            e.Property(d => d.ProjectKey).HasMaxLength(128);
            e.Property(d => d.Title).HasMaxLength(300);
            e.Property(d => d.PlanKey).HasMaxLength(64);
            e.Property(d => d.Outcomes).HasMaxLength(200);
            e.Property(d => d.Source).HasConversion<string>().HasMaxLength(16);
            e.Property(d => d.QuestionKey).HasMaxLength(64);
            e.Property(d => d.SourceRef).HasMaxLength(400);
            e.Property(d => d.ImportKey).HasMaxLength(200);
            e.HasIndex(d => new { d.ProjectKey, d.Number }).IsUnique();
            e.HasIndex(d => new { d.ProjectKey, d.ImportKey });
            e.HasMany(d => d.Replaces).WithOne().HasForeignKey(r => r.DecisionId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<StewardDecisionReplace>(e =>
        {
            e.Property(r => r.Why).HasMaxLength(600);
            e.HasIndex(r => new { r.DecisionId, r.Number }).IsUnique();
        });

        b.Entity<StewardInstruction>(e =>
        {
            e.Property(i => i.ProjectKey).HasMaxLength(128);
            e.Property(i => i.Until).HasMaxLength(600);
            e.Property(i => i.ClosedBy).HasMaxLength(16);
            e.HasIndex(i => new { i.ProjectKey, i.ClosedAt });
        });

        b.Entity<StewardFollowUp>(e =>
        {
            e.Property(f => f.ProjectKey).HasMaxLength(128);
            e.Property(f => f.CitedBy).HasMaxLength(300);
            e.HasIndex(f => new { f.ProjectKey, f.Decision });
        });

        b.Entity<StewardDecisionConflict>(e =>
        {
            e.Property(c => c.ProjectKey).HasMaxLength(128);
            e.Property(c => c.Why).HasMaxLength(600);
            e.Property(c => c.Settled).HasMaxLength(16);
            e.HasIndex(c => new { c.ProjectKey, c.Older, c.Newer }).IsUnique();
        });

        b.Entity<StewardState>(e =>
        {
            e.HasKey(s => s.ProjectKey);
            e.Property(s => s.ProjectKey).HasMaxLength(128);
            e.Property(s => s.RestingAtSha).HasMaxLength(64);
            e.Property(s => s.CreatedDay).HasMaxLength(10);
        });

        b.Entity<CycleRun>(e =>
        {
            e.Property(c => c.Phase).HasConversion<string>().HasMaxLength(32);
            e.Property(c => c.PhaseAfter).HasConversion<string>().HasMaxLength(32);
            e.Property(c => c.Outcome).HasConversion<string>().HasMaxLength(16);
            e.Property(c => c.ProjectKey).HasMaxLength(128);
            e.Property(c => c.Track).HasConversion<string>().HasMaxLength(16);
            e.Property(c => c.Skill).HasMaxLength(64);
            e.Property(c => c.Model).HasMaxLength(64);
            e.HasIndex(c => c.StartedAt);
            e.HasIndex(c => new { c.ProjectKey, c.StartedAt });
            e.HasOne(c => c.Ticket).WithMany().HasForeignKey(c => c.TicketId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
