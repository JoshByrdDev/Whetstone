using Whetstone.Services;
using Whetstone.Services.Tracker;
using Xunit;

using Snapshot = Whetstone.Services.IssueSnapshotService.Snapshot;

namespace Whetstone.Tests;

/// <summary>
/// The children a phase is handed for a parent issue.
///
/// A ticket linked to a feature or an epic is linked to a heading: the feature says what the area
/// is, and its stories say what to build, each with its own acceptance criteria. The issue's own
/// copy of its children is a key and a title, so a worker handed that list alone has the names of
/// the requirements and none of their text - and no tool to go and read them. These tests hold the
/// read to the rule the parent chain keeps: every child is listed, and one whose text was not read
/// says so rather than passing for a child that says nothing.
/// </summary>
public class IssueChildrenTests
{
    private static IssueRef Ref(string key, string summary = "A story") => new(key, summary, "New", "User Story");

    private static Issue Brief(string key, string? description, string? criteria = null, string summary = "A story")
        => Issue.Empty(key) with
        {
            Summary = summary,
            Description = description,
            AcceptanceCriteria = criteria,
            Status = "Active",
            Type = "User Story",
        };

    private static IssueContextBuilder.FetchBriefs Briefs(params Issue[] issues)
        => _ => Task.FromResult<IReadOnlyDictionary<string, Issue>>(
            issues.ToDictionary(i => i.Key, StringComparer.OrdinalIgnoreCase));

    // ---- get_issue_context -------------------------------------------------

    [Fact]
    public async Task Each_child_carries_its_description_and_acceptance_criteria()
    {
        var c = await IssueContextBuilder.ReadChildrenAsync(
            new[] { Ref("PROJ-2", "Export as CSV"), Ref("PROJ-3", "Export as PDF") },
            Briefs(Brief("PROJ-2", "Users can export the report.", "Given a report, a CSV downloads."),
                   Brief("PROJ-3", "Same, as a PDF.", "The PDF has the report's header.")));

        Assert.Equal(new[] { "PROJ-2", "PROJ-3" }, c.Read.Select(r => r.Key));
        Assert.All(c.Read, r => Assert.True(r.Readable));
        Assert.Equal("Users can export the report.", c.Read[0].Description);
        Assert.Equal("Given a report, a CSV downloads.", c.Read[0].AcceptanceCriteria);
        Assert.Equal("The PDF has the report's header.", c.Read[1].AcceptanceCriteria);
        Assert.Empty(c.NotRead);
    }

    /// <summary>One batched call for the lot - the same cheap read a link is resolved with.</summary>
    [Fact]
    public async Task Children_are_read_in_one_call()
    {
        var calls = 0;
        IssueContextBuilder.FetchBriefs fetch = keys =>
        {
            calls++;
            return Task.FromResult<IReadOnlyDictionary<string, Issue>>(
                keys.ToDictionary(k => k, k => Brief(k, "text"), StringComparer.OrdinalIgnoreCase));
        };

        await IssueContextBuilder.ReadChildrenAsync(new[] { Ref("PROJ-2"), Ref("PROJ-3"), Ref("PROJ-4") }, fetch);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_child_the_read_did_not_return_is_listed_unreadable_not_empty()
    {
        var c = await IssueContextBuilder.ReadChildrenAsync(
            new[] { Ref("PROJ-2"), Ref("PROJ-3", "Hidden story") },
            Briefs(Brief("PROJ-2", "Visible.")));

        var hidden = c.Read.Single(r => r.Key == "PROJ-3");
        Assert.False(hidden.Readable);
        Assert.Null(hidden.Description);
        // What the parent's own copy knew is kept, so there is a name to go and look up.
        Assert.Equal("Hidden story", hidden.Summary);
    }

    [Fact]
    public async Task A_failed_read_lists_every_child_unreadable_rather_than_failing_the_call()
    {
        var c = await IssueContextBuilder.ReadChildrenAsync(
            new[] { Ref("PROJ-2"), Ref("PROJ-3") },
            _ => throw new HttpRequestException("boom"));

        Assert.Equal(2, c.Read.Length);
        Assert.All(c.Read, r => Assert.False(r.Readable));
    }

    [Fact]
    public async Task Children_over_the_cap_are_named_with_the_reason_not_dropped()
    {
        var total = IssueContextBuilder.MaxChildrenRead + 3;
        var refs = Enumerable.Range(1, total).Select(i => Ref($"PROJ-{i}", $"Story {i}")).ToArray();
        IReadOnlyCollection<string>? asked = null;
        IssueContextBuilder.FetchBriefs fetch = keys =>
        {
            asked = keys;
            return Task.FromResult<IReadOnlyDictionary<string, Issue>>(
                keys.ToDictionary(k => k, k => Brief(k, "text"), StringComparer.OrdinalIgnoreCase));
        };

        var c = await IssueContextBuilder.ReadChildrenAsync(refs, fetch);

        Assert.Equal(IssueContextBuilder.MaxChildrenRead, c.Read.Length);
        Assert.Equal(IssueContextBuilder.MaxChildrenRead, asked!.Count);
        Assert.Equal(3, c.NotRead.Length);
        Assert.Equal($"PROJ-{total}", c.NotRead[^1].Key);
        Assert.Equal($"Story {total}", c.NotRead[^1].Summary);
        Assert.All(c.NotRead, n => Assert.Contains("cap", n.Reason));
    }

    [Fact]
    public async Task A_long_child_is_cut_and_says_so()
    {
        var longText = new string('a', IssueContextBuilder.MaxChildFieldChars + 500);
        var c = await IssueContextBuilder.ReadChildrenAsync(new[] { Ref("PROJ-2") },
            Briefs(Brief("PROJ-2", "short", longText)));

        var only = Assert.Single(c.Read);
        Assert.True(only.Truncated);
        Assert.Equal("short", only.Description);
        Assert.Contains("[truncated: 500 more characters", only.AcceptanceCriteria);
    }

    [Fact]
    public async Task No_children_means_no_read()
    {
        var c = await IssueContextBuilder.ReadChildrenAsync(Array.Empty<IssueRef>(),
            _ => throw new InvalidOperationException("should not be called"));

        Assert.Empty(c.Read);
        Assert.Empty(c.NotRead);
    }

    // ---- since the plan was made -------------------------------------------

    private static Issue Parent(params IssueRef[] children)
        => Issue.Empty("PROJ-1") with { Summary = "The feature", Description = "An area.", Children = children };

    private static Dictionary<string, Issue> Read(params Issue[] issues)
        => issues.ToDictionary(i => i.Key, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void An_edited_childs_criteria_count_as_a_change()
    {
        var parent = Parent(Ref("PROJ-2", "Export as CSV"), Ref("PROJ-3"));
        var before = IssueSnapshotService.Capture(parent,
            Read(Brief("PROJ-2", "Export.", "A CSV downloads."), Brief("PROJ-3", "Other.")));

        var diff = IssueSnapshotService.Compare(before, parent,
            Read(Brief("PROJ-2", "Export.", "A CSV downloads, with a header row."), Brief("PROJ-3", "Other.")));

        Assert.True(diff.Changed);
        Assert.Equal("PROJ-2", Assert.Single(diff.ChangedChildren).Key);
        Assert.Contains("CHILD ISSUE(S) EDITED", IssueSnapshotService.Render(diff, "Azure Boards"));
        Assert.Contains("PROJ-2 (Export as CSV)", IssueSnapshotService.Render(diff, "Azure Boards"));
    }

    [Fact]
    public void Unchanged_children_stay_silent()
    {
        var parent = Parent(Ref("PROJ-2"));
        var before = IssueSnapshotService.Capture(parent, Read(Brief("PROJ-2", "Export.", "A CSV.")));

        // Line endings and edge whitespace are not edits.
        var diff = IssueSnapshotService.Compare(before, parent, Read(Brief("PROJ-2", "Export.\r\n", "A CSV.")));

        Assert.False(diff.Changed);
    }

    [Fact]
    public void A_new_child_is_reported()
    {
        var before = IssueSnapshotService.Capture(Parent(Ref("PROJ-2")), Read(Brief("PROJ-2", "x")));
        var now = Parent(Ref("PROJ-2"), Ref("PROJ-5", "Late story"));

        var diff = IssueSnapshotService.Compare(before, now, Read(Brief("PROJ-2", "x"), Brief("PROJ-5", "y")));

        Assert.True(diff.Changed);
        Assert.Equal("PROJ-5", Assert.Single(diff.NewChildren).Key);
    }

    /// <summary>
    /// A baseline stored before children were kept has no opinion on them. Reporting every child
    /// as new would make every mid-flight ticket on a feature cry wolf on its next cycle.
    /// </summary>
    [Fact]
    public void A_baseline_without_children_does_not_report_them()
    {
        var parent = Parent(Ref("PROJ-2"));
        var old = new Snapshot("PROJ-1", null, "The feature", "An area.",
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

        var diff = IssueSnapshotService.Compare(old, parent, Read(Brief("PROJ-2", "x")));

        Assert.False(diff.Changed);
    }

    [Fact]
    public void A_failed_read_now_is_not_evidence_of_a_change()
    {
        var parent = Parent(Ref("PROJ-2"));
        var before = IssueSnapshotService.Capture(parent, Read(Brief("PROJ-2", "x")));

        Assert.False(IssueSnapshotService.Compare(before, parent, children: null).Changed);
        // And a child that simply was not returned this time is not an edit either.
        Assert.False(IssueSnapshotService.Compare(before, parent, Read()).Changed);
    }

    [Fact]
    public void A_baseline_with_children_survives_the_round_trip_through_the_database()
    {
        var parent = Parent(Ref("PROJ-2"));
        var json = System.Text.Json.JsonSerializer.Serialize(
            IssueSnapshotService.Capture(parent, Read(Brief("PROJ-2", "x"))));
        var back = System.Text.Json.JsonSerializer.Deserialize<Snapshot>(json)!;

        Assert.False(IssueSnapshotService.Compare(back, parent, Read(Brief("PROJ-2", "x"))).Changed);
        Assert.True(IssueSnapshotService.Compare(back, parent, Read(Brief("PROJ-2", "y"))).Changed);
    }
}
