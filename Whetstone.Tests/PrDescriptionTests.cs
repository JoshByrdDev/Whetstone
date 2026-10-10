using Whetstone.Models;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The PR description the dialog opens with. Two things are worth pinning: which source wins, and
/// what is never allowed across the boundary onto someone else's repo — the QA record and this
/// dashboard's own ticket id.
/// </summary>
public class PrDescriptionTests
{
    private const string CommitBody =
        "Checkout now refuses to run over uncommitted work.\n\n" +
        "- Refuse when the target repo is dirty\n" +
        "- Name the files, so the refusal is actionable";

    private static Ticket Ticket(Action<Ticket>? setup = null)
    {
        var t = new Ticket { Id = 28, ProjectKey = "alpha", Title = "Add a thing" };
        setup?.Invoke(t);
        return t;
    }

    /// <summary>
    /// The commit message is the version somebody edited for a reader. Everything else on the
    /// ticket was written for the pipeline.
    /// </summary>
    [Fact]
    public void The_commit_message_wins()
    {
        var body = PrDescription.Compose(
            Ticket(t => t.ImplementationSummary = "The summary, which is not as good."),
            CommitBody,
            "The original ask.");

        Assert.StartsWith(CommitBody, body);
        Assert.DoesNotContain("not as good", body);
        Assert.DoesNotContain("original ask", body);
    }

    /// <summary>A branch raised before refinement squashed it has no message worth quoting.</summary>
    [Fact]
    public void Falls_back_to_the_summary_when_the_branch_is_not_yet_one_commit()
    {
        var body = PrDescription.Compose(
            Ticket(t => t.ImplementationSummary = "The summary."), null, "The original ask.");

        Assert.StartsWith("The summary.", body);
        Assert.DoesNotContain("original ask", body);
    }

    /// <summary>
    /// The description is passed in already resolved, because a Jira-linked ticket stores none of
    /// its own — reading the local field would leave exactly those tickets with a blank draft.
    /// </summary>
    [Fact]
    public void Falls_back_to_the_resolved_description_when_nothing_has_been_implemented_yet()
    {
        var jiraLinked = Ticket(t => { t.IssueKey = "ALPHA-8870"; t.Title = null; t.Description = null; });
        Assert.StartsWith("The ask, from Jira.", PrDescription.Compose(jiraLinked, null, "The ask, from Jira."));
    }

    /// <summary>
    /// The work items were being laid out one per bullet, verbatim and in tick order. It read as a
    /// transcript rather than a description, which is what the commit message replaced.
    /// </summary>
    [Fact]
    public void The_work_item_list_is_not_restated()
    {
        var body = PrDescription.Compose(Ticket(t =>
        {
            t.ImplementationSummary = "The summary.";
            t.WorkItems.Add(new WorkItem { Text = "Some work item nobody outside needs", Done = true });
        }), CommitBody);

        Assert.DoesNotContain("nobody outside needs", body);
    }

    /// <summary>
    /// The QA record is this pipeline's account of its own testing, and the reviewer is not its
    /// audience — real assurance happens after the merge. It must not leak into the PR whatever
    /// state it is in.
    /// </summary>
    [Fact]
    public void The_qa_record_never_reaches_the_pr()
    {
        var body = PrDescription.Compose(Ticket(t =>
        {
            t.QaItems.Add(new QaItem { Text = "Dirty tree blocks checkout", Status = QaStatus.Passed, Order = 0 });
            t.QaItems.Add(new QaItem { Text = "Clean tree checks out", Status = QaStatus.Failed, Note = "500 on save", Order = 1 });
        }), CommitBody);

        Assert.DoesNotContain("Tested", body);
        Assert.DoesNotContain("Dirty tree blocks checkout", body);
        Assert.DoesNotContain("500 on save", body);

        // Nor the other way round: a ticket excused from QA testing has nothing to explain here.
        Assert.DoesNotContain("QA", PrDescription.Compose(Ticket(t => t.SkipQaTesting = true), CommitBody));
    }

    [Fact]
    public void Footer_is_the_jira_link_and_only_the_jira_link()
    {
        var linked = PrDescription.Compose(Ticket(t =>
        {
            t.IssueKey = "ALPHA-8870";
            t.IssueUrl = "https://example.atlassian.net/browse/ALPHA-8870";
        }), CommitBody);
        Assert.EndsWith("---\n\n[ALPHA-8870](https://example.atlassian.net/browse/ALPHA-8870)\n", linked);

        // A key with no URL still identifies the work; our row id would not.
        Assert.EndsWith("---\n\nALPHA-8870\n", PrDescription.Compose(Ticket(t => t.IssueKey = "ALPHA-8870"), CommitBody));
    }

    /// <summary>
    /// Ticket #28 is a row number in a local database. It means nothing to whoever reads the PR,
    /// and putting it on someone else's repo is not ours to do.
    /// </summary>
    [Fact]
    public void Our_own_ticket_id_never_appears()
    {
        var body = PrDescription.Compose(Ticket(), CommitBody);

        Assert.DoesNotContain("Ticket #", body);
        Assert.DoesNotContain("28", body);

        // With no Jira key there is nothing to attribute it to, so there is no footer at all.
        Assert.Equal(CommitBody + "\n\n", body);
    }

    /// <summary>Windows line endings and stray blank runs must not reach the host's markdown.</summary>
    [Fact]
    public void Normalizes_line_endings_and_blank_runs()
    {
        var body = PrDescription.Compose(Ticket(), "Lead.\r\n\r\n\r\n- One\r\n- Two\r\n");

        Assert.Equal("Lead.\n\n- One\n- Two\n\n", body);
        Assert.DoesNotContain("\r", body);
    }
}
