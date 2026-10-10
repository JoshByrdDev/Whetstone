using Whetstone.Services.Tracker;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// Reading a ticket's cached attachments back off disk.
///
/// This is the list every phase now sees on get_ticket, and it is the only thing standing between a
/// worker and reasoning about a design it never opened. So the filename it reports has to be the one
/// the human uploaded: an agent told to look for "the design PDF" and handed "46968-ALPHA_State..."
/// has been given a path, not a reason to open it.
/// </summary>
public class IssueAttachmentListingTests : IDisposable
{
    private readonly string _root;
    private readonly IssueAttachmentStore _store;
    private readonly DashboardPaths _paths;

    public IssueAttachmentListingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ccd-att-" + Guid.NewGuid().ToString("N")[..8]);
        _paths = new DashboardPaths(_root);
        _store = new IssueAttachmentStore(_paths);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Write a file the way <c>CacheAsync</c> would have: id-prefixed, in the ticket's folder.</summary>
    private void Place(int ticketId, string storedName, string content = "x")
    {
        var dir = _paths.IssueAttachmentTicketDir(ticketId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, storedName), content);
    }

    [Fact]
    public void A_ticket_that_was_never_fetched_lists_nothing()
    {
        Assert.Empty(_store.ListCached(42));
    }

    [Fact]
    public void The_id_prefix_is_stripped_back_to_the_uploaded_filename()
    {
        Place(27, "46968-ALPHA_Statement_04_10_2025.pdf", "hello");

        var one = Assert.Single(_store.ListCached(27));
        Assert.Equal("ALPHA_Statement_04_10_2025.pdf", one.FileName);
        Assert.Equal(5, one.Bytes);
        Assert.True(File.Exists(one.LocalPath));
    }

    /// <summary>
    /// Real filenames carry dashes of their own. Only the leading run of digits is ours, so the split
    /// is on the FIRST dash after a numeric id — never on whichever dash happens to come next.
    /// </summary>
    [Fact]
    public void Dashes_inside_the_uploaded_name_survive()
    {
        Place(26, "46967-IRA_Statement -2 - PrecisionIRA Final.pdf");

        var one = Assert.Single(_store.ListCached(26));
        Assert.Equal("IRA_Statement -2 - PrecisionIRA Final.pdf", one.FileName);
    }

    /// <summary>
    /// Azure DevOps identifies an attachment by GUID, and a GUID is full of dashes. Splitting on the
    /// first one would hand a reader "b2c3-...-Design.pdf" — a name that is not the uploaded name and
    /// not obviously wrong either, which is the worst of both.
    /// </summary>
    [Fact]
    public void A_guid_prefix_is_stripped_whole_rather_than_at_its_first_dash()
    {
        Place(33, "3f2504e0-4f89-41d3-9a0c-0305e82c3301-Design Spec.pdf");

        var one = Assert.Single(_store.ListCached(33));
        Assert.Equal("Design Spec.pdf", one.FileName);
    }

    /// <summary>Anything not written by us is reported as it is found, rather than silently trimmed.</summary>
    [Fact]
    public void A_file_with_no_numeric_prefix_is_left_alone()
    {
        Place(9, "hand-dropped-notes.txt");

        var one = Assert.Single(_store.ListCached(9));
        Assert.Equal("hand-dropped-notes.txt", one.FileName);
    }

    /// <summary>
    /// A long hand-dropped name whose 37th character happens to be a dash must not be mistaken for a
    /// GUID prefix — the check is that the first 36 characters actually parse as one.
    /// </summary>
    [Fact]
    public void A_long_name_that_merely_has_a_dash_where_a_guid_ends_is_left_alone()
    {
        Place(34, "Some-Very-Long-Filename-That-Exceeds-36-Chars.pdf");

        var one = Assert.Single(_store.ListCached(34));
        Assert.Equal("Some-Very-Long-Filename-That-Exceeds-36-Chars.pdf", one.FileName);
    }

    [Fact]
    public void Every_cached_file_is_listed()
    {
        Place(28, "71840-image-20260807-132308.png");
        Place(28, "71841-image-20260807-134945.png");
        Place(28, "71842-image-20260807-134610.png");

        Assert.Equal(3, _store.ListCached(28).Count);
    }

    /// <summary>Tickets do not see each other's attachments — the folder is the whole isolation.</summary>
    [Fact]
    public void Attachments_do_not_leak_between_tickets()
    {
        Place(1, "100-a.pdf");
        Place(2, "200-b.pdf");

        Assert.Equal("a.pdf", Assert.Single(_store.ListCached(1)).FileName);
        Assert.Equal("b.pdf", Assert.Single(_store.ListCached(2)).FileName);
    }

    // ---- Inherited from an ancestor ----------------------------------------

    /// <summary>Write a file the way <c>CacheAsync</c> would have for an ancestor's attachment.</summary>
    private void PlaceInherited(int ticketId, string issueKey, string storedName, string content = "x")
    {
        var dir = _paths.IssueAttachmentAncestorDir(ticketId, issueKey);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, storedName), content);
    }

    /// <summary>
    /// The parent story's mockup is often the only picture of what was asked for — a sub-task that
    /// carves out one bullet rarely re-attaches it. So it has to reach the phase.
    /// </summary>
    [Fact]
    public void An_ancestors_attachment_is_listed_too()
    {
        PlaceInherited(30, "ALPHA-8618", "77-fee-subtypes.png");

        var one = Assert.Single(_store.ListCached(30));
        Assert.Equal("fee-subtypes.png", one.FileName);
        Assert.Equal("ALPHA-8618", one.SourceIssue);
        Assert.True(File.Exists(one.LocalPath));
    }

    /// <summary>
    /// Reaching the phase is not the same as being claimed as the ticket's own. "The epic shows the
    /// layout" and "your ticket shows the layout" are different claims, and only one of them is true.
    /// </summary>
    [Fact]
    public void The_tickets_own_attachments_are_distinguishable_from_inherited_ones()
    {
        Place(30, "10-my-own.png");
        PlaceInherited(30, "ALPHA-8618", "77-parents.png");
        PlaceInherited(30, "ALPHA-7008", "88-epics.png");

        var all = _store.ListCached(30);

        Assert.Null(all.Single(a => a.FileName == "my-own.png").SourceIssue);
        Assert.Equal("ALPHA-8618", all.Single(a => a.FileName == "parents.png").SourceIssue);
        Assert.Equal("ALPHA-7008", all.Single(a => a.FileName == "epics.png").SourceIssue);
    }

    /// <summary>The ticket's own come first: they are the ones that describe the ticket.</summary>
    [Fact]
    public void Own_attachments_are_listed_before_inherited_ones()
    {
        PlaceInherited(31, "ALPHA-8618", "77-parents.png");
        Place(31, "99-my-own.png");

        Assert.Equal(new[] { "my-own.png", "parents.png" }, _store.ListCached(31).Select(a => a.FileName));
    }

    /// <summary>
    /// Two issues can each hold a file called mockup.png. They must not collide on disk, and the
    /// reader must be able to tell which issue each one came from.
    /// </summary>
    [Fact]
    public void Same_named_files_from_different_issues_both_survive()
    {
        Place(32, "1-mockup.png", "own");
        PlaceInherited(32, "ALPHA-8618", "2-mockup.png", "parents");

        var all = _store.ListCached(32);
        Assert.Equal(2, all.Count);
        Assert.All(all, a => Assert.Equal("mockup.png", a.FileName));
        Assert.Equal(new string?[] { null, "ALPHA-8618" }, all.Select(a => a.SourceIssue));
    }

    // ---- The download budget -----------------------------------------------

    /// <summary>
    /// An attachment list as any tracker would normalise one. Sizes match what Place writes, so an
    /// already-cached file counts as current and no download is attempted — these tests never touch
    /// the network.
    /// </summary>
    private static IReadOnlyList<IssueAttachment> Attachments(params (string Id, string Name, int Size)[] attachments)
        => attachments.Select(a => new IssueAttachment(a.Id, a.Name, $"http://x/{a.Id}", a.Size, null, null, null)).ToList();

    /// <summary>Unconfigured: any download it is asked for fails, which is what we want to observe.</summary>
    private static IIssueTracker NoTracker => Whetstone.Services.Tracker.NoTracker.Instance;

    [Fact]
    public async Task An_ancestors_attachments_land_in_that_ancestors_folder()
    {
        PlaceInherited(40, "ALPHA-8618", "77-fee-subtypes.png", "12345");

        var r = await _store.CacheAsync(40, Attachments(("77", "fee-subtypes.png", 5)), NoTracker, sourceIssueKey: "ALPHA-8618");

        var one = Assert.Single(r.Attachments);
        Assert.Equal("fee-subtypes.png", one.FileName);
        Assert.StartsWith(_paths.IssueAttachmentAncestorDir(40, "ALPHA-8618"), one.LocalPath);
        // And it does not turn up as one of the ticket's own.
        Assert.Equal("ALPHA-8618", Assert.Single(_store.ListCached(40)).SourceIssue);
    }

    /// <summary>
    /// The cap is what stops a hierarchy of well-illustrated epics turning one context call into
    /// forty files. What it drops still has to be named — a silent cap reads as full coverage.
    /// </summary>
    [Fact]
    public async Task Attachments_over_the_budget_are_named_and_reasoned_not_dropped()
    {
        PlaceInherited(41, "ALPHA-8618", "1-first.png", "12345");

        var r = await _store.CacheAsync(41, Attachments(("1", "first.png", 5), ("2", "second.png", 5)),
            NoTracker, sourceIssueKey: "ALPHA-8618", limit: 1);

        Assert.Equal("first.png", Assert.Single(r.Attachments).FileName);
        var skipped = Assert.Single(r.NotCached);
        Assert.Equal("second.png", skipped.FileName);
        Assert.Contains("cap", skipped.Reason);
    }

    /// <summary>
    /// A budget already spent by nearer ancestors is not a reason to say the epic had no attachments.
    /// </summary>
    [Fact]
    public async Task A_spent_budget_reports_every_attachment_it_could_not_take()
    {
        var r = await _store.CacheAsync(42, Attachments(("1", "a.png", 5), ("2", "b.png", 5)),
            NoTracker, sourceIssueKey: "ALPHA-7008", limit: 0);

        Assert.Empty(r.Attachments);
        Assert.Equal(new[] { "a.png", "b.png" }, r.NotCached.Select(s => s.FileName));
        Assert.All(r.NotCached, s => Assert.Contains("budget", s.Reason));
    }
}
