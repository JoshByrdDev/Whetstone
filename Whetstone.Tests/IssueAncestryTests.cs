using Whetstone.Services;
using Whetstone.Services.Tracker;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The parent chain a phase is handed for a sub-task.
///
/// This exists because of ALPHA-8749. It is a sub-task whose description is two sentences and whose
/// links are empty; its parent story ALPHA-8618 carries the full acceptance criteria - the fee
/// subtypes by name, the workflow status model, the upload path. Speculation read the links, found
/// nothing, reported "no reachable parent story", and blocked a ticket that was already specified.
/// Neither tracker puts the requirements where the work is: Jira does not report the parent as a
/// link at all, and Azure DevOps reports it only as one, so reading either in isolation loses it.
///
/// The rule these tests hold to is the one AdfText already states: never drop something silently. A
/// parent we cannot fetch has to arrive listed-but-unreadable, because a reader who sees no
/// description must be able to tell "the parent said nothing" from "we could not ask".
///
/// The walk itself is tracker-agnostic - it is handed normalised <see cref="Issue"/> records, so
/// these tests exercise the behaviour for both providers at once.
/// </summary>
public class IssueAncestryTests
{
    private static Issue Issue(string key, string? summary = null, string? description = null,
        IssueRef? parent = null, params IssueRef[] children)
        => Whetstone.Services.Tracker.Issue.Empty(key) with
        {
            Summary = summary,
            Description = description,
            Status = "To Do",
            Type = "Story",
            Parent = parent,
            Children = children,
        };

    /// <summary>The parent as a CHILD knows it: a key, a summary, a type - and no description.
    /// That is the trap the walk exists to avoid, so it is what the tests start from.</summary>
    private static IssueRef InlineParent(string key, string summary = "Parent story")
        => new(key, summary, "To Do", "Story");

    private static IssueRef Subtask(string key, string summary)
        => new(key, summary, "To Do", "Sub-task");

    /// <summary>A fetcher backed by a fixed map; anything not in it reads as unfetchable.</summary>
    private static IssueContextBuilder.FetchIssue Fetcher(Dictionary<string, Issue> issues)
        => key => Task.FromResult<Issue?>(issues.TryGetValue(key, out var v) ? v : null);

    private static IssueContextBuilder.FetchIssue NothingReadable
        => _ => Task.FromResult<Issue?>(null);

    // ---- The regression ----------------------------------------------------

    [Fact]
    public async Task A_subtask_with_no_links_still_reaches_its_parents_description()
    {
        var child = Issue("ALPHA-8749", "BE | Implement Fee Workflow Creation",
            parent: InlineParent("ALPHA-8618"));

        var parent = Issue("ALPHA-8618", "Build Workflow and Transaction Process for Imported Fees",
            "Workflow should default to Pending status.");

        var a = await IssueContextBuilder.WalkAncestryAsync("ALPHA-8749", child,
            Fetcher(new() { ["ALPHA-8618"] = parent }));

        var only = Assert.Single(a.ParentChain);
        Assert.Equal("ALPHA-8618", only.Key);
        Assert.Equal(1, only.Level);
        Assert.Equal("ALPHA-8749", only.ParentOf);
        Assert.True(only.Readable);
        Assert.Equal("Workflow should default to Pending status.", only.Description);
    }

    /// <summary>
    /// The reference the child carried has a summary and no description, which is exactly the trap:
    /// a chain built from it alone would look populated while holding none of the requirements. So
    /// the fetch is not an optimisation to skip.
    /// </summary>
    [Fact]
    public async Task The_parents_description_comes_from_the_fetch_not_the_inline_reference()
    {
        var child = Issue("ALPHA-8749", parent: InlineParent("ALPHA-8618", "Parent story"));
        var parent = Issue("ALPHA-8618", "Parent story", "The real criteria.");

        var a = await IssueContextBuilder.WalkAncestryAsync("ALPHA-8749", child,
            Fetcher(new() { ["ALPHA-8618"] = parent }));

        Assert.Equal("The real criteria.", a.ParentChain[0].Description);
    }

    /// <summary>
    /// On Azure DevOps the acceptance criteria are a field of their own, and they are the single
    /// thing most worth climbing for. A chain that carried the description alone would leave the
    /// child reading a summary of work whose actual conditions of success sit one hop up.
    /// </summary>
    [Fact]
    public async Task An_ancestors_acceptance_criteria_travel_with_its_description()
    {
        var child = Issue("1002", parent: InlineParent("1001"));
        var parent = Issue("1001", "Imported fees", "Build the workflow.") with
        {
            AcceptanceCriteria = "Every fee lands in Pending.",
        };

        var a = await IssueContextBuilder.WalkAncestryAsync("1002", child,
            Fetcher(new() { ["1001"] = parent }));

        Assert.Contains("Build the workflow.", a.ParentChain[0].Description);
        Assert.Contains("Every fee lands in Pending.", a.ParentChain[0].Description);
    }

    // ---- Walking up --------------------------------------------------------

    [Fact]
    public async Task The_chain_walks_from_subtask_through_story_to_epic()
    {
        var child = Issue("ALPHA-8749", parent: InlineParent("ALPHA-8618"));
        var story = Issue("ALPHA-8618", "Imported fees", "Story detail.", InlineParent("ALPHA-7008", "Fees & Billing"));
        var epic = Issue("ALPHA-7008", "Fees & Billing Processes", "Epic detail.") with { Type = "Epic" };

        var a = await IssueContextBuilder.WalkAncestryAsync("ALPHA-8749", child,
            Fetcher(new() { ["ALPHA-8618"] = story, ["ALPHA-7008"] = epic }));

        Assert.Equal(new[] { "ALPHA-8618", "ALPHA-7008" }, a.ParentChain.Select(p => p.Key));
        Assert.Equal(new[] { 1, 2 }, a.ParentChain.Select(p => p.Level));
        // Level 2's ParentOf names level 1, so the hierarchy reads without the reader counting.
        Assert.Equal("ALPHA-8618", a.ParentChain[1].ParentOf);
        Assert.Equal("Epic", a.ParentChain[1].Type);
        Assert.Equal("Epic detail.", a.ParentChain[1].Description);
    }

    [Fact]
    public async Task An_issue_with_no_parent_reports_an_empty_chain()
    {
        var a = await IssueContextBuilder.WalkAncestryAsync("ALPHA-1", Issue("ALPHA-1", "Standalone"), NothingReadable);

        Assert.Empty(a.ParentChain);
        Assert.Empty(a.Siblings);
    }

    // ---- Degrading honestly ------------------------------------------------

    [Fact]
    public async Task A_parent_the_tracker_will_not_serve_is_listed_as_unreadable_not_omitted()
    {
        var child = Issue("ALPHA-8749", parent: InlineParent("ALPHA-8618", "Imported fees"));

        var a = await IssueContextBuilder.WalkAncestryAsync("ALPHA-8749", child, NothingReadable);

        var only = Assert.Single(a.ParentChain);
        Assert.Equal("ALPHA-8618", only.Key);
        Assert.False(only.Readable);
        Assert.Null(only.Description);
        // What we did know from the child's own copy survives, so the reader can still go look.
        Assert.Equal("Imported fees", only.Summary);
        Assert.Equal("Story", only.Type);
    }

    /// <summary>
    /// Azure DevOps reports a parent as a bare link, so there is no summary to fall back on. The
    /// ancestor must still be LISTED - "there is a parent and I could not read it" is a far more
    /// useful thing to hand a phase than silence.
    /// </summary>
    [Fact]
    public async Task An_unreadable_parent_with_no_inline_detail_is_still_listed()
    {
        var child = Issue("1002", parent: new IssueRef("1001", null, null, null));

        var a = await IssueContextBuilder.WalkAncestryAsync("1002", child, NothingReadable);

        var only = Assert.Single(a.ParentChain);
        Assert.Equal("1001", only.Key);
        Assert.False(only.Readable);
        Assert.Null(only.Summary);
    }

    /// <summary>
    /// Both trackers permit re-parenting, so a hierarchy can be made to loop. Walking one would
    /// fetch until something else gave out.
    /// </summary>
    [Fact]
    public async Task A_parent_cycle_terminates()
    {
        var child = Issue("SELF", parent: InlineParent("A"));
        var a = Issue("A", "A", parent: InlineParent("B"));
        var b = Issue("B", "B", parent: InlineParent("A"));

        var result = await IssueContextBuilder.WalkAncestryAsync("SELF", child,
            Fetcher(new() { ["A"] = a, ["B"] = b }));

        Assert.Equal(new[] { "A", "B" }, result.ParentChain.Select(p => p.Key));
    }

    [Fact]
    public async Task The_chain_stops_climbing_after_three_ancestors()
    {
        var child = Issue("SELF", parent: InlineParent("L1"));
        var issues = new Dictionary<string, Issue>();
        for (var i = 1; i <= 6; i++)
            issues[$"L{i}"] = Issue($"L{i}", $"L{i}", parent: InlineParent($"L{i + 1}"));

        var a = await IssueContextBuilder.WalkAncestryAsync("SELF", child, Fetcher(issues));

        Assert.Equal(new[] { "L1", "L2", "L3" }, a.ParentChain.Select(p => p.Key));
    }

    // ---- Siblings ----------------------------------------------------------

    /// <summary>
    /// Siblings are what tell a phase where its own scope stops - ALPHA-8749's export-to-CSV work
    /// belongs to ALPHA-8753, and the parent story describes both.
    /// </summary>
    [Fact]
    public async Task Siblings_are_the_parents_other_children_and_never_this_one()
    {
        var child = Issue("ALPHA-8749", parent: InlineParent("ALPHA-8618"));
        var parent = Issue("ALPHA-8618", "Imported fees", null, null,
            Subtask("ALPHA-8749", "BE | Fee workflow creation"),
            Subtask("ALPHA-8753", "FE | Export to CSV"));

        var a = await IssueContextBuilder.WalkAncestryAsync("ALPHA-8749", child,
            Fetcher(new() { ["ALPHA-8618"] = parent }));

        var sibling = Assert.Single(a.Siblings);
        Assert.Equal("ALPHA-8753", sibling.Key);
        Assert.Equal("FE | Export to CSV", sibling.Summary);
        Assert.Equal("Sub-task", sibling.Type);
    }

    /// <summary>Two levels up, the other children are aunts and uncles - noise, not scope.</summary>
    [Fact]
    public async Task Siblings_do_not_come_from_further_up_the_chain()
    {
        var child = Issue("SELF", parent: InlineParent("STORY"));
        var story = Issue("STORY", "Story", parent: InlineParent("EPIC"));
        var epic = Issue("EPIC", "Epic", null, null, Subtask("OTHER-1", "Someone else's work"));

        var a = await IssueContextBuilder.WalkAncestryAsync("SELF", child,
            Fetcher(new() { ["STORY"] = story, ["EPIC"] = epic }));

        Assert.Empty(a.Siblings);
    }

    [Fact]
    public async Task An_unreadable_parent_yields_no_siblings_rather_than_a_wrong_empty_answer()
    {
        var child = Issue("ALPHA-8749", parent: InlineParent("ALPHA-8618"));

        var a = await IssueContextBuilder.WalkAncestryAsync("ALPHA-8749", child, NothingReadable);

        Assert.Empty(a.Siblings);
        Assert.False(a.ParentChain[0].Readable);
    }

    // ---- Inherited attachments ---------------------------------------------

    /// <summary>
    /// A cacher that hands back one file per issue and records which issues it was asked about, so a
    /// test can check both what came back and what we bothered to ask for.
    /// </summary>
    private static IssueContextBuilder.CacheAttachments Cacher(List<string> askedAbout)
        => (key, _) =>
        {
            askedAbout.Add(key);
            return Task.FromResult((
                new[] { new IssueContextBuilder.AttachmentDto($"{key}-mockup.png", $"C:/cache/{key}.png", 12, "image/png", "Someone", null) },
                Array.Empty<IssueContextBuilder.NotCachedDto>()));
        };

    /// <summary>
    /// The picture of what was asked for is usually attached to the story, not to the sub-task that
    /// carves one bullet out of it - so it has to be downloaded from up the chain, not just named.
    /// </summary>
    [Fact]
    public async Task Each_ancestor_contributes_its_attachments()
    {
        var child = Issue("ALPHA-8749", parent: InlineParent("ALPHA-8618"));
        var story = Issue("ALPHA-8618", "Imported fees", parent: InlineParent("ALPHA-7008"));
        var epic = Issue("ALPHA-7008", "Fees & Billing");

        var asked = new List<string>();
        var a = await IssueContextBuilder.WalkAncestryAsync("ALPHA-8749", child,
            Fetcher(new() { ["ALPHA-8618"] = story, ["ALPHA-7008"] = epic }), Cacher(asked));

        // Nearest first, so the parent gets first call on whatever budget the caller is holding.
        Assert.Equal(new[] { "ALPHA-8618", "ALPHA-7008" }, asked);
        Assert.Equal("ALPHA-8618-mockup.png", Assert.Single(a.ParentChain[0].Attachments).FileName);
        Assert.Equal("C:/cache/ALPHA-7008.png", Assert.Single(a.ParentChain[1].Attachments).LocalPath);
    }

    /// <summary>
    /// An ancestor we could not fetch has no attachment list behind it. Asking anyway would produce
    /// an empty list that reads as "the parent had none" - which is the exact confusion Readable
    /// exists to prevent.
    /// </summary>
    [Fact]
    public async Task An_unreadable_ancestor_is_not_asked_for_attachments()
    {
        var child = Issue("ALPHA-8749", parent: InlineParent("ALPHA-8618"));

        var asked = new List<string>();
        var a = await IssueContextBuilder.WalkAncestryAsync("ALPHA-8749", child, NothingReadable, Cacher(asked));

        Assert.Empty(asked);
        Assert.Empty(a.ParentChain[0].Attachments);
        Assert.False(a.ParentChain[0].Readable);
    }

    /// <summary>An attachment we chose not to download is still named, with the reason.</summary>
    [Fact]
    public async Task An_ancestor_attachment_we_skipped_is_reported_not_dropped()
    {
        var child = Issue("ALPHA-8749", parent: InlineParent("ALPHA-8618"));
        var story = Issue("ALPHA-8618", "Imported fees");

        var a = await IssueContextBuilder.WalkAncestryAsync("ALPHA-8749", child,
            Fetcher(new() { ["ALPHA-8618"] = story }),
            (_, _) => Task.FromResult((
                Array.Empty<IssueContextBuilder.AttachmentDto>(),
                new[] { new IssueContextBuilder.NotCachedDto("spec.docx", "'.docx' is not a readable type") })));

        var only = Assert.Single(a.ParentChain);
        Assert.Empty(only.Attachments);
        Assert.Equal("spec.docx", Assert.Single(only.AttachmentsNotCached).File);
    }

    /// <summary>Nobody caching is still a valid call - the chain and its descriptions do not depend on it.</summary>
    [Fact]
    public async Task Without_a_cacher_the_chain_still_reads()
    {
        var child = Issue("ALPHA-8749", parent: InlineParent("ALPHA-8618"));
        var story = Issue("ALPHA-8618", "Imported fees", "The criteria.");

        var a = await IssueContextBuilder.WalkAncestryAsync("ALPHA-8749", child,
            Fetcher(new() { ["ALPHA-8618"] = story }));

        Assert.Equal("The criteria.", a.ParentChain[0].Description);
        Assert.Empty(a.ParentChain[0].Attachments);
    }
}
