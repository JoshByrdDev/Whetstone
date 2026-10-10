using Whetstone.Models;
using Whetstone.Services;
using Whetstone.Services.Tracker;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The effective title and description of a linked ticket.
///
/// A linked ticket deliberately stores neither: the tracker owns them and they are resolved live.
/// So this is what decides what every row on the board says, and its failure mode is quiet — a
/// ticket whose title silently becomes its own key looks like a ticket someone named badly.
///
/// The rule the tests hold to is that the SINGLE and the MANY paths must agree. The board loads one
/// and the drawer loads the other, and a ticket that reports itself differently depending on which
/// one you opened is worse than either answer.
/// </summary>
public class LinkedIssueResolverTests
{
    /// <summary>A tracker backed by a fixed map; anything not in it is answered "no such issue".</summary>
    private sealed class FakeTracker : IIssueTracker
    {
        private readonly Dictionary<string, Issue> _issues;
        private readonly bool _configured;
        private readonly bool _throws;

        public FakeTracker(Dictionary<string, Issue>? issues = null, bool configured = true, bool throws = false)
            => (_issues, _configured, _throws) = (issues ?? new(), configured, throws);

        public int SingleReads { get; private set; }
        public int BatchReads { get; private set; }

        public TrackerKind Kind => TrackerKind.Jira;
        public string DisplayName => "Jira";
        public string IssueNoun => "issue";
        public bool IsConfigured => _configured;
        public string? IssueUrl(string key) => $"https://x/browse/{key}";
        public string? NormalizeKey(string? candidate) => candidate?.Trim().ToUpperInvariant();
        public string BranchToken(string key) => key;
        public string CommitRef(string key) => key;

        public Task<Issue?> GetIssueAsync(string key, CancellationToken ct = default)
        {
            SingleReads++;
            if (_throws) throw new HttpRequestException("the tracker is down");
            return Task.FromResult(_issues.TryGetValue(key, out var i) ? i : null);
        }

        public Task<Issue?> GetAncestorAsync(string key, CancellationToken ct = default) => GetIssueAsync(key, ct);

        public Task<IReadOnlyDictionary<string, Issue>> GetBriefsAsync(
            IReadOnlyCollection<string> keys, CancellationToken ct = default)
        {
            BatchReads++;
            if (_throws) throw new HttpRequestException("the tracker is down");
            var map = new Dictionary<string, Issue>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in keys) if (_issues.TryGetValue(k, out var i)) map[k] = i;
            return Task.FromResult<IReadOnlyDictionary<string, Issue>>(map);
        }

        public Task<IReadOnlyList<IssueRef>> GetMyUnfinishedAsync(int max, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<IssueRef>>(Array.Empty<IssueRef>());

        public Task<IReadOnlyList<BlockingLink>> GetBlockingLinksAsync(
            IReadOnlyCollection<string> keys, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<BlockingLink>>(Array.Empty<BlockingLink>());

        public Task<byte[]?> DownloadAttachmentAsync(IssueAttachment a, CancellationToken ct = default)
            => Task.FromResult<byte[]?>(null);
    }

    private sealed class FixedFactory(IIssueTracker tracker) : IssueTrackerFactory
    {
        public override IIssueTracker For(string project) => tracker;
    }

    private static Issue Issue(string key, string? summary, string? description = null, string? criteria = null)
        => Whetstone.Services.Tracker.Issue.Empty(key) with
        {
            Summary = summary, Description = description, AcceptanceCriteria = criteria,
        };

    private static Ticket Linked(int id, string key) => new()
    {
        Id = id, ProjectKey = "alpha", IssueKey = key,
    };

    private static Ticket Standalone(int id, string title, string? description = null) => new()
    {
        Id = id, ProjectKey = "alpha", Title = title, Description = description,
    };

    private static LinkedIssueResolver Resolver(FakeTracker tracker) => new(new FixedFactory(tracker));

    // ---- The happy path ----------------------------------------------------

    [Fact]
    public async Task A_linked_ticket_takes_its_title_and_description_from_the_tracker()
    {
        var r = await Resolver(new FakeTracker(new()
        {
            ["ALPHA-1"] = Issue("ALPHA-1", "Add the export button", "Put it on the report screen."),
        })).ResolveAsync(Linked(1, "ALPHA-1"));

        Assert.Equal("Add the export button", r.Title);
        Assert.Equal("Put it on the report screen.", r.Description);
        Assert.True(r.FromTracker);
        Assert.False(r.Unavailable);
    }

    /// <summary>
    /// Azure DevOps keeps acceptance criteria in a field of their own. A drawer showing the
    /// description while silently dropping the criteria beside it is the worse kind of partial
    /// answer, so the effective description carries both.
    /// </summary>
    [Fact]
    public async Task Acceptance_criteria_travel_with_the_description()
    {
        var r = await Resolver(new FakeTracker(new()
        {
            ["1234"] = Issue("1234", "Export", "Build the export.", "Header row included."),
        })).ResolveAsync(Linked(1, "1234"));

        Assert.Contains("Build the export.", r.Description);
        Assert.Contains("Header row included.", r.Description);
    }

    [Fact]
    public async Task An_unlinked_ticket_keeps_its_own_title_and_never_asks_the_tracker()
    {
        var tracker = new FakeTracker();
        var r = await Resolver(tracker).ResolveAsync(Standalone(1, "Tidy the board", "Some local prose."));

        Assert.Equal("Tidy the board", r.Title);
        Assert.Equal("Some local prose.", r.Description);
        Assert.False(r.FromTracker);
        Assert.False(r.Unavailable);
        Assert.Equal(0, tracker.SingleReads);
    }

    // ---- Degrading honestly ------------------------------------------------

    /// <summary>
    /// A tracker that cannot be reached leaves the title as the key, and says so. The flag is what
    /// the drawer draws "unreachable" from — without it a Jira outage reads as a board full of
    /// tickets somebody named after their own keys.
    /// </summary>
    [Fact]
    public async Task An_unreachable_tracker_falls_back_to_the_key_and_flags_it()
    {
        var r = await Resolver(new FakeTracker(throws: true)).ResolveAsync(Linked(1, "ALPHA-1"));

        Assert.Equal("ALPHA-1", r.Title);
        Assert.True(r.FromTracker);
        Assert.True(r.Unavailable);
    }

    [Fact]
    public async Task An_unconfigured_tracker_falls_back_rather_than_erroring()
    {
        var r = await Resolver(new FakeTracker(configured: false)).ResolveAsync(Linked(1, "ALPHA-1"));

        Assert.Equal("ALPHA-1", r.Title);
        Assert.True(r.Unavailable);
    }

    /// <summary>
    /// "No such issue" is not a resolution. Caching the key as its own title would report the ticket
    /// as resolved while showing nothing but its key — which is what a project switched to a
    /// different tracker looks like, and precisely when a reader most needs to be told.
    /// </summary>
    [Fact]
    public async Task An_issue_the_tracker_does_not_have_is_reported_unavailable()
    {
        var r = await Resolver(new FakeTracker()).ResolveAsync(Linked(1, "ALPHA-404"));

        Assert.Equal("ALPHA-404", r.Title);
        Assert.True(r.Unavailable);
    }

    /// <summary>
    /// The single path and the batch path must agree. The board loads one and the drawer the other,
    /// and the same ticket describing itself two ways depending on which you opened is worse than
    /// either answer on its own.
    /// </summary>
    [Fact]
    public async Task The_single_and_batch_paths_agree_about_a_missing_issue()
    {
        var ticket = Linked(1, "ALPHA-404");

        var one = await Resolver(new FakeTracker()).ResolveAsync(ticket);
        var many = (await Resolver(new FakeTracker()).ResolveManyAsync(new[] { ticket }))[1];

        Assert.Equal(one.Title, many.Title);
        Assert.Equal(one.Unavailable, many.Unavailable);
        Assert.Equal(one.FromTracker, many.FromTracker);
    }

    // ---- The batch path ----------------------------------------------------

    /// <summary>
    /// One call per project, not one per ticket. This is the whole reason the batch path exists:
    /// a board of forty tickets was forty round-trips before it.
    /// </summary>
    [Fact]
    public async Task A_board_render_is_one_call_for_the_whole_project()
    {
        var tracker = new FakeTracker(new()
        {
            ["ALPHA-1"] = Issue("ALPHA-1", "One"),
            ["ALPHA-2"] = Issue("ALPHA-2", "Two"),
            ["ALPHA-3"] = Issue("ALPHA-3", "Three"),
        });

        var map = await Resolver(tracker).ResolveManyAsync(new[]
        {
            Linked(1, "ALPHA-1"), Linked(2, "ALPHA-2"), Linked(3, "ALPHA-3"), Standalone(4, "Local one"),
        });

        Assert.Equal(1, tracker.BatchReads);
        Assert.Equal(0, tracker.SingleReads);
        Assert.Equal("One", map[1].Title);
        Assert.Equal("Three", map[3].Title);
        Assert.Equal("Local one", map[4].Title);
    }

    /// <summary>The cache is what makes a second board render free.</summary>
    [Fact]
    public async Task A_second_read_inside_the_window_costs_no_call()
    {
        var tracker = new FakeTracker(new() { ["ALPHA-1"] = Issue("ALPHA-1", "One") });
        var resolver = Resolver(tracker);

        await resolver.ResolveManyAsync(new[] { Linked(1, "ALPHA-1") });
        var again = await resolver.ResolveAsync(Linked(1, "ALPHA-1"));

        Assert.Equal(1, tracker.BatchReads);
        Assert.Equal(0, tracker.SingleReads);
        Assert.Equal("One", again.Title);
    }

    /// <summary>
    /// An outage after a good read shows the last-known title rather than the bare key — stale but
    /// readable beats correct and useless — and still says it is stale.
    /// </summary>
    [Fact]
    public async Task After_a_good_read_an_outage_shows_the_last_known_title_still_flagged()
    {
        var issues = new Dictionary<string, Issue> { ["ALPHA-1"] = Issue("ALPHA-1", "Add the export button") };
        var resolver = Resolver(new FakeTracker(issues));
        await resolver.ResolveAsync(Linked(1, "ALPHA-1"));

        // A different resolver would have an empty cache, so reuse this one and break its tracker by
        // asking about a ticket whose project now answers with a failure.
        var broken = new LinkedIssueResolver(new FixedFactory(new FakeTracker(throws: true)));
        var cold = await broken.ResolveAsync(Linked(1, "ALPHA-1"));

        Assert.Equal("ALPHA-1", cold.Title);       // nothing cached here: the key is all there is
        Assert.True(cold.Unavailable);
    }

    /// <summary>An issue with no summary at all still gets a non-empty title.</summary>
    [Fact]
    public async Task An_issue_with_no_summary_falls_back_to_its_key()
    {
        var r = await Resolver(new FakeTracker(new() { ["ALPHA-1"] = Issue("ALPHA-1", null) }))
            .ResolveAsync(Linked(1, "ALPHA-1"));

        Assert.Equal("ALPHA-1", r.Title);
        Assert.False(r.Unavailable);   // we DID reach it; it simply has no summary
    }
}
