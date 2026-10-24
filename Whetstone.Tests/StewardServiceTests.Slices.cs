using Microsoft.EntityFrameworkCore;
using Whetstone.Models;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The rest of docs/plans-as-slices.md: overrides declared when they are asked about, what is not
/// a decision, the charter, ideas, and a slice's story checked, kept true and driven.
/// </summary>
public partial class StewardServiceTests
{
    // ---- piece 2: overrides declared --------------------------------------------------------

    [Fact]
    public async Task A_question_says_what_it_touches_and_an_answer_replaces_only_what_the_owner_ticked()
    {
        await In(s => s.OwnerRecordsDecisionAsync("alpha", new StewardNewDecision("Texts come from one shared number.")));
        await In(s => s.OwnerRecordsDecisionAsync("alpha", new StewardNewDecision("A text names the business.")));
        await Begin();

        // Touches is required; an empty list is a claim too.
        Assert.Equal("bad-request", (await In(s => s.AskAsync("alpha", Question() with { Touches = null }))).Answer);
        Assert.Equal("bad-request", (await In(s => s.AskAsync("alpha", Question() with { Touches = new[] { 9 } }))).Answer);
        // An option can only change what the question touches.
        Assert.Equal("bad-request", (await In(s => s.AskAsync("alpha", Question() with
        {
            Touches = new[] { 1 },
            Changes = new Dictionary<string, IReadOnlyList<int>> { ["A new one"] = new[] { 2 } },
        }))).Answer);

        Assert.Equal("asked", (await In(s => s.AskAsync("alpha", Question() with
        {
            Touches = new[] { 1, 2 },
            Changes = new Dictionary<string, IReadOnlyList<int>> { ["A new one"] = new[] { 1 } },
        }))).Answer);

        // An answer cannot replace what the question did not touch.
        Assert.Equal("bad-request", (await In(s => s.OwnerAnswersAsync("alpha", "sms-provider", "A new one.", new[] { 7 }))).Answer);
        // Left to the option: picking "A new one" replaces #1 and leaves #2.
        Assert.Equal("recorded", (await In(s => s.OwnerAnswersAsync("alpha", "sms-provider", "A new one (Costs money).", null))).Answer);

        var answer = Register().Single(d => d.QuestionKey == "sms-provider");
        Assert.Equal(new[] { 1 }, answer.Replaces.Select(r => r.Number));
        var inForce = Json(await In(s => s.DecisionsAsync("alpha", null)));
        Assert.DoesNotContain("one shared number", inForce);
        Assert.Contains("names the business", inForce);
        // And the next run is told #1 stopped.
        await In(s => s.EndAsync("alpha", "asked", false));
        Assert.Contains($"\"changedDecisions\":[{{\"number\":{answer.Number}", Json(await Begin()));
    }

    [Fact]
    public async Task An_owner_can_answer_and_leave_every_touched_decision_standing()
    {
        await In(s => s.OwnerRecordsDecisionAsync("alpha", new StewardNewDecision("Texts come from one shared number.")));
        await Begin();
        await In(s => s.AskAsync("alpha", Question() with
        {
            Touches = new[] { 1 },
            Changes = new Dictionary<string, IReadOnlyList<int>> { ["A new one"] = new[] { 1 } },
        }));
        await In(s => s.OwnerAnswersAsync("alpha", "sms-provider", "A new one.", Array.Empty<int>()));
        Assert.Empty(Register().Single(d => d.QuestionKey == "sms-provider").Replaces);
        Assert.Contains("one shared number", Json(await In(s => s.DecisionsAsync("alpha", null))));
    }

    // ---- pieces 4 and 5: the charter, and ideas ------------------------------------------------

    private const string Idea3 = "# Unit 3: the day\n\n> **Status: idea.** Written 2026-02-01.\n\nA technician sees their own visits for the day, built on invites. Not their pay.\n";

    [Fact]
    public async Task The_plan_as_read_is_the_charter_the_projects_decisions_and_every_slice_in_order()
    {
        Configure("full", """ "plan": { "units": "docs/units", "charter": "docs/charter.md", "readable": "docs/PLAN-AS-READ.md" } """);
        _git.InRepo["docs/charter.md"] = "# The plan\n\n## Purpose\n\nTo get people booked.\n";
        _git.First = "# Unit 1: booking\n\n> **Status: agreed 2026-01-01.**\n\n## The walk-through this unit makes possible\n\nA customer books (A1) and a business sees it (A2).\n\n- **A1.** One.\n- **A2.** Two.\n";
        _git.More[3] = ("docs/units/03-the-day.md", Idea3);
        await In(s => s.OwnerRecordsDecisionAsync("alpha", new StewardNewDecision("Prices never differ.")));

        var text = (await In0(s => s.PlanAsReadAsync("alpha")))!;
        Assert.Contains("### Purpose", text);                       // the charter, its headings set under the page's
        Assert.Contains("- **#1** Prices never differ.", text);
        Assert.Contains("### 1. booking - building", text);
        Assert.Contains("#### The walk-through", text);              // the slice being worked, in full
        Assert.Contains("### 3. the day - idea", text);
        Assert.Contains("A technician sees their own visits for the day", text);
        Assert.True(text.IndexOf("### 1.") < text.IndexOf("### 2.") && text.IndexOf("### 2.") < text.IndexOf("### 3."));

        // The plan view names an idea's stage, and an idea is never agreed from the board.
        var view = Json(await In(s => s.PlanViewAsync("alpha")));
        Assert.Contains("\"stage\":\"idea\"", view);
        Assert.Contains("An idea is drafted before it is agreed", view);
    }

    [Fact]
    public async Task A_plan_of_ideas_is_drafted_only_by_expanding_its_next_idea_in_place()
    {
        _git.Second = "# Unit 2: invites\n\n> **Status: idea.**\n\nA business invites its customers.\n";
        _git.More[3] = ("docs/units/03-the-day.md", Idea3);
        await Begin();
        const string draft = "# Unit 2: invites\n\n> **Status: draft, awaiting the owner.**\n\nA business invites its customers.\n\n## The walk-through this unit makes possible\n\nA business sends an invite (A1).\n\n- **A1.** One.\n";
        Assert.Equal("exists", (await In(s => s.DraftUnitAsync("alpha", "02-other-name.md", draft))).Answer);
        Assert.Equal("drafted", (await In(s => s.DraftUnitAsync("alpha", "02-second.md", draft))).Answer);
        Assert.Equal("docs/units/02-second.md", _git.Committed[^1].Path);

        // With no idea at the next number, there is nothing to draft: ideas are the owner's.
        _git.Second = "# Unit 2\n\n> **Status: agreed 2026-02-02.**\n\n- **A1.** One.\n";
        _git.More.Clear();
        _git.More[4] = ("docs/units/04-later.md", Idea3);
        Assert.Equal("no-idea", (await In(s => s.DraftUnitAsync("alpha", "03-the-day.md", draft.Replace("Unit 2", "Unit 3")))).Answer);
    }

    [Fact]
    public async Task The_steward_proposes_an_idea_and_the_board_commits_it_only_on_the_owners_yes()
    {
        await Begin();
        StewardAsk ProposeIdea(string key, string file, string text) => Question(key) with { Options = null, Idea = new StewardIdeaProposal(file, text) };
        // Never over an agreed slice's number, and only a file that reads as an idea.
        Assert.Equal("bad-request", (await In(s => s.AskAsync("alpha", ProposeIdea("idea-one", "01-x.md", Idea3)))).Answer);
        Assert.Equal("bad-request", (await In(s => s.AskAsync("alpha", ProposeIdea("idea-one", "03-the-day.md", "# No status\n")))).Answer);
        // Not over a draft either: that is the steward's to revise.
        Assert.Equal("bad-request", (await In(s => s.AskAsync("alpha", ProposeIdea("idea-one", "02-second.md", Idea3)))).Answer);

        Assert.Equal("asked", (await In(s => s.AskAsync("alpha", ProposeIdea("idea-day", "03-the-day.md", Idea3)))).Answer);
        Assert.Equal("asked", (await In(s => s.AskAsync("alpha", ProposeIdea("idea-pay", "04-pay.md", Idea3)))).Answer);
        var before = _git.Committed.Count;
        Assert.Equal("recorded", (await In(s => s.OwnerAnswersAsync("alpha", "idea-pay", StewardService.LeaveIdeas))).Answer);
        Assert.Equal(before, _git.Committed.Count);
        Assert.Equal("recorded", (await In(s => s.OwnerAnswersAsync("alpha", "idea-day", StewardService.CommitIdea + "."))).Answer);
        Assert.Equal(("docs/units/03-the-day.md", Idea3), _git.Committed[^1]);
        // An idea is the owner's text, not a decision: nothing goes in the register.
        Assert.DoesNotContain(Register(), d => d.QuestionKey is "idea-day" or "idea-pay");
    }

    [Fact]
    public async Task An_agreed_slice_whose_file_or_number_moved_is_flagged()
    {
        Assert.DoesNotContain("movedFrom\":\"Slice", Json(await In(s => s.PlanViewAsync("alpha"))));
        _git.FirstFile = "docs/units/01-renamed.md";
        var view = Json(await In(s => s.PlanViewAsync("alpha")));
        Assert.Contains("Slice 1 was agreed as docs/units/01-first.md, and is docs/units/01-renamed.md now", view);
    }

    // ---- piece 6: the story checked ---------------------------------------------------------------

    private static string DraftWith(string story) =>
        "# Unit 2: invites\n\n> **Status: draft, awaiting the owner.**\n\n## The walk-through this unit makes possible\n\n" + story
        + "\n\n## Outcomes\n\n- **A1.** One.\n- **B1.** Two.\n- **B2.** Three.\n- **Z1.** The walk-through, driven.\n";

    [Fact]
    public void A_story_cites_its_outcomes_in_every_sentence_and_stays_short()
    {
        var outcomes = new[] { "A1", "B1", "B2", "Z1" };
        var ok = SliceStory.Check(DraftWith("A business sends an invite (A1). The customer signs up from it and adds a dog (B1, B2)."), outcomes);
        Assert.Empty(ok.Errors);
        Assert.Empty(ok.Uncited);   // Z is the walk-through itself, never cited by it

        var bad = SliceStory.Check(DraftWith("A business sends an invite (A1). The customer is happy. They pay (C9)."), outcomes);
        Assert.Contains(bad.Errors, e => e.Contains("C9"));
        Assert.Contains(bad.Errors, e => e.Contains("The customer is happy."));
        Assert.Equal(new[] { "B1", "B2" }, bad.Uncited);
        // A parenthesis that is not a citation is not one.
        Assert.Empty(SliceStory.Cited("They pick a day (any open one)."));

        var words = string.Join(" ", Enumerable.Repeat("A person does a thing (A1).", 50));
        Assert.Contains(SliceStory.Check(DraftWith(words), outcomes).Errors, e => e.Contains("most is 250"));
        var longish = string.Join(" ", Enumerable.Repeat("A person does a thing (A1).", 40));
        var warned = SliceStory.Check(DraftWith(longish), outcomes);
        Assert.Empty(warned.Errors);
        Assert.Contains(warned.Notes, n => n.Contains("may be two"));
        Assert.NotEmpty(SliceStory.Check("# x\n\n- **A1.** One.\n", outcomes).Errors);   // no story at all
    }

    [Fact]
    public async Task A_draft_whose_story_does_not_check_is_refused_and_a_good_one_says_what_it_does_not_cite()
    {
        await Begin();
        var refused = await In(s => s.DraftUnitAsync("alpha", "02-second.md", DraftWith("A business sends an invite. They are glad (A1).")));
        Assert.Equal("story", refused.Answer);
        Assert.Contains("cites no outcome", refused.Why);
        var drafted = await In(s => s.DraftUnitAsync("alpha", "02-second.md", DraftWith("A business sends an invite (A1).")));
        Assert.Equal("revised", drafted.Answer);
        Assert.Contains("No step cites B1, B2", drafted.Why);
    }

    // ---- piece 7: the story kept true -----------------------------------------------------------

    private const string Building = "# Unit 2: invites\n\n> **Status: agreed 2026-02-01.**\n\n## The walk-through this unit makes possible\n\n"
        + "A business sends an invite by email (A1). The customer signs up from it (B1).\n\n## Outcomes\n\n- **A1.** One.\n- **B1.** Two.\n\n## Decided\n\nNothing yet.\n\n## Done means\n\nIt works.\n";

    [Fact]
    public async Task An_answer_about_a_slice_being_built_rewords_its_story_and_writes_the_decision_in_one_commit()
    {
        _git.Second = Building;
        await Begin();
        var ask = Question("invite-channel") with
        {
            Options = new[] { new BlockOption("By text", "Cheaper to read", true), new BlockOption("By email") },
            Unit = 2,
            Story = new StewardStoryChange("A business sends an invite by email (A1).",
                new Dictionary<string, string> { ["By text"] = "A business sends an invite by text (A1)." }),
        };
        // The sentence must be in the story exactly; a built slice's story is never rewritten.
        Assert.Equal("bad-request", (await In(s => s.AskAsync("alpha", ask with { Story = ask.Story! with { Sentence = "Not in it." } }))).Answer);
        Assert.Equal("bad-request", (await In(s => s.AskAsync("alpha", ask with { Unit = 1 }))).Answer);
        Assert.Equal("asked", (await In(s => s.AskAsync("alpha", ask))).Answer);

        Assert.Equal("recorded", (await In(s => s.OwnerAnswersAsync("alpha", "invite-channel", "By text (Cheaper to read)."))).Answer);
        var (path, content) = _git.Committed[^1];
        Assert.Equal("docs/units/02-second.md", path);
        Assert.Contains("A business sends an invite by text (A1). The customer signs up from it (B1).", content);
        var made = Register().Single(d => d.QuestionKey == "invite-channel");
        Assert.Contains($"- **(#{made.Number}) Which provider sends our texts.** Decided by the owner", content);
        Assert.True(content.IndexOf("## Decided") < content.IndexOf($"(#{made.Number})") && content.IndexOf($"(#{made.Number})") < content.IndexOf("## Done means"));
        Assert.Equal(2, made.PlanUnit);
    }

    [Fact]
    public async Task A_story_question_answered_another_way_changes_nothing_in_the_story()
    {
        _git.Second = Building;
        await Begin();
        await In(s => s.AskAsync("alpha", Question("invite-channel") with
        {
            Options = new[] { new BlockOption("By text", null, true), new BlockOption("By email") },
            Unit = 2,
            Story = new StewardStoryChange("A business sends an invite by email (A1).",
                new Dictionary<string, string> { ["By text"] = "A business sends an invite by text (A1)." }),
        }));
        var before = _git.Committed.Count;
        Assert.Equal("recorded", (await In(s => s.OwnerAnswersAsync("alpha", "invite-channel", "By email."))).Answer);
        Assert.Equal(before, _git.Committed.Count);

        // Once the slice is built, its story is history.
        await using (var ctx = NewCtx())
        {
            ctx.StewardOutcomes.AddRange(
                new StewardOutcome { ProjectKey = "alpha", Plan = "", Unit = 2, Outcome = "A1", State = StewardOutcomeState.Done },
                new StewardOutcome { ProjectKey = "alpha", Plan = "", Unit = 2, Outcome = "B1", State = StewardOutcomeState.Done });
            await ctx.SaveChangesAsync();
        }
        var late = await In(s => s.AskAsync("alpha", Question("invite-again") with
        {
            Options = new[] { new BlockOption("By text", null, true), new BlockOption("By email") },
            Unit = 2,
            Story = new StewardStoryChange("A business sends an invite by email (A1).", new Dictionary<string, string> { ["By text"] = "x (A1)." }),
        }));
        Assert.Equal("bad-request", late.Answer);
        Assert.Contains("built", late.Why);
    }

    // ---- piece 3: not decisions -----------------------------------------------------------------

    [Fact]
    public async Task An_instruction_has_an_end_is_never_a_decision_and_is_closed_by_saying_how_it_ended()
    {
        Assert.Equal("bad-request", (await In(s => s.OwnerInstructsAsync("alpha", "Hold merges.", null))).Answer);
        Assert.Equal("recorded", (await In(s => s.OwnerInstructsAsync("alpha", "Hold merges.", "the flaky test is fixed"))).Answer);
        Assert.Empty(Register());

        var first = Json(await Begin());
        Assert.Contains("Hold merges. - until the flaky test is fixed", first);
        Assert.Contains("\"instructions\":[{\"Id\":1,\"Text\":\"Hold merges.\"", first);
        Assert.Equal("bad-request", (await In(s => s.CloseInstructionAsync("alpha", 1, ""))).Answer);
        Assert.Equal("closed", (await In(s => s.CloseInstructionAsync("alpha", 1, "Ticket 12 fixed it, merged at abc."))).Answer);
        await In(s => s.EndAsync("alpha", "closed it", true));

        var second = Json(await Begin());
        Assert.DoesNotContain("Hold merges", second);
        Assert.Equal("bad-request", (await In(s => s.OwnerClosesInstructionAsync("alpha", 1, null))).Answer);
    }

    [Fact]
    public async Task A_follow_up_waits_until_a_question_takes_it_up()
    {
        await In(s => s.OwnerRecordsDecisionAsync("alpha", new StewardNewDecision("A technician has a role of their own.")));
        await Begin();
        Assert.Equal("bad-request", (await In(s => s.FollowUpAsync("alpha", 9, "x"))).Answer);
        Assert.Equal("noted", (await In(s => s.FollowUpAsync("alpha", 1, "Let's talk about a dynamic role."))).Answer);
        Assert.Contains("dynamic role", Json(await In(s => s.StatusAsync("alpha"))) + Json(await In(s => s.RegisterViewAsync("alpha"))));

        Assert.Equal("asked", (await In(s => s.AskAsync("alpha", Question("roles") with { FollowUps = new[] { 1 } }))).Answer);
        await using var ctx = NewCtx();
        Assert.Equal("question:roles", (await ctx.StewardFollowUps.SingleAsync()).CitedBy);
        Assert.Equal("bad-request", (await In(s => s.AskAsync("alpha", Question("roles-again") with { FollowUps = new[] { 1 } }))).Answer);
    }

    [Fact]
    public async Task A_ruling_that_reads_a_decision_which_no_longer_holds_is_flagged()
    {
        await In(s => s.OwnerRecordsDecisionAsync("alpha", new StewardNewDecision("Everyone in the team sees the same schedule.")));
        await Begin();
        Assert.Equal("bad-request", (await In(s => s.RuleAsync("alpha", "x", "y", null, new[] { 5 }))).Answer);
        Assert.Equal("ruled", (await In(s => s.RuleAsync("alpha", "A screen ticket shows the whole team's day.", "#1", null, new[] { 1 }))).Answer);
        Assert.DoesNotContain("was replaced by", Json(await In(s => s.StatusAsync("alpha"))));

        await In(s => s.OwnerRecordsDecisionAsync("alpha", new StewardNewDecision("Technicians see only their own visits.",
            Replaces: new[] { new StewardReplacing(1, "Roles.") })));
        Assert.Contains("\"flagged\":\"#1 was replaced by #2\"", Json(await In(s => s.StatusAsync("alpha"))));
        await In(s => s.EndAsync("alpha", "x", false));
        Assert.Contains("\"flaggedRulings\":[{\"Id\":", Json(await Begin()));

        // Kept by the owner: it now reads what replaced the dead decision, and the flag is gone.
        var id = Entries().Single(e => e.Kind == StewardEntryKind.Ruling).Id;
        Assert.Equal("kept", (await In(s => s.OwnerKeepsRulingAsync("alpha", id))).Answer);
        Assert.Equal("2", Entries().Single(e => e.Id == id).Reads);
        Assert.DoesNotContain("was replaced by", Json(await In(s => s.StatusAsync("alpha"))));
    }

    [Fact]
    public async Task A_provisional_decision_comes_due_when_its_unit_is_built_and_the_owner_confirms_it()
    {
        await In(s => s.OwnerRecordsDecisionAsync("alpha", new StewardNewDecision("Texts are free for now.", Provisional: true, ProvisionalUnit: 1)));
        Assert.Contains("\"due\":[]", Json(await In(s => s.RegisterViewAsync("alpha"))));
        await using (var ctx = NewCtx())
        {
            ctx.StewardOutcomes.AddRange(
                new StewardOutcome { ProjectKey = "alpha", Plan = "", Unit = 1, Outcome = "A1", State = StewardOutcomeState.Done },
                new StewardOutcome { ProjectKey = "alpha", Plan = "", Unit = 1, Outcome = "A2", State = StewardOutcomeState.Done });
            await ctx.SaveChangesAsync();
        }
        Assert.Contains("\"due\":[1]", Json(await In(s => s.RegisterViewAsync("alpha"))));
        Assert.Contains("\"provisionalDue\":[{\"number\":1", Json(await Begin()));
        Assert.Equal("confirmed", (await In(s => s.OwnerConfirmsDecisionAsync("alpha", 1))).Answer);
        Assert.Contains("\"status\":\"inForce\"", Json(await In(s => s.DecisionsAsync("alpha", null))));
        Assert.Equal("bad-request", (await In(s => s.OwnerConfirmsDecisionAsync("alpha", 1))).Answer);
    }
}
