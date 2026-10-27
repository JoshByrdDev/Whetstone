using Whetstone.Services;
using Whetstone.Services.Walkthrough;
using Xunit;
using static Whetstone.Services.Walkthrough.WalkthroughDeck;

namespace Whetstone.Tests;

/// <summary>
/// The walkthrough's guarantees: code on a slide is cut from the real patch with git's own markers,
/// the voice is handed text it can say, and every pointer in a model-written script is checked
/// before anything is shown. Each of these was a real defect in the hand-built prototype.
/// </summary>
public class WalkthroughTests
{
    // ---- DiffSnippet ----------------------------------------------------------------------------

    // A frontend hunk, as git prints one: four added lines inside an existing if.
    private const string FrontPatch = """
        diff --git a/distribution.tsx b/distribution.tsx
        index 1..2 100644
        --- a/distribution.tsx
        +++ b/distribution.tsx
        @@ -182,6 +182,10 @@ export function getDistributionSummarySections(
                   data?.paymentDetails?.checkDetails?.receivingAccountOrPlanNumber ||
                   "-",
               });
        +      paymentItems.push({
        +        label: "Receiving Account Type",
        +        value: data?.paymentDetails?.checkDetails?.receivingAccountType || "-",
        +      });
             }
           } else if (isACH) {
             paymentTitle = "ACH Payment Details";
        """;

    private static string FrontFile()
    {
        var lines = Enumerable.Range(1, 177).Select(i => $"// line {i}").ToList();
        lines.AddRange(new[]
        {
            "    if (isRollover) {",                                                           // 178
            "      paymentItems.push({",                                                       // 179
            "        label: \"Receiving Account/Plan Number\",",                                // 180
            "        value:",                                                                  // 181
            "          data?.paymentDetails?.checkDetails?.receivingAccountOrPlanNumber ||",    // 182
            "          \"-\",",                                                                 // 183
            "      });",                                                                       // 184
            "      paymentItems.push({",                                                       // 185
            "        label: \"Receiving Account Type\",",                                       // 186
            "        value: data?.paymentDetails?.checkDetails?.receivingAccountType || \"-\",", // 187
            "      });",                                                                       // 188
            "    }",                                                                           // 189
            "  } else if (isACH) {",                                                           // 190
        });
        return string.Join("\n", lines) + "\n";
    }

    [Fact]
    public void Marks_added_lines_from_the_patch_and_keeps_context_the_patch_does_not_carry()
    {
        var (snippet, error) = DiffSnippet.Build(FrontFile(), FrontPatch, 178, 189);

        Assert.Null(error);
        Assert.Equal(4, snippet!.Added);
        Assert.Equal(0, snippet.Removed);
        Assert.Equal(new[] { 185, 186, 187, 188 }, snippet.Lines.Where(l => l.M == "+").Select(l => l.N!.Value));
        // 178 is outside the patch's three lines of context and still comes out, unmarked.
        Assert.Equal(" ", snippet.Lines[0].M);
        Assert.Equal("if (isRollover) {", snippet.Lines[0].T);   // shared indent stripped
    }

    [Fact]
    public void Removed_lines_hang_before_the_line_that_follows_them()
    {
        const string patch = """
            @@ -10,3 +10,3 @@
             keep
            -old value
            +new value
             tail
            """;
        var file = string.Join("\n", Enumerable.Range(1, 9).Select(i => $"l{i}").Concat(new[] { "keep", "new value", "tail" }));

        var (snippet, _) = DiffSnippet.Build(file, patch, 10, 12);

        Assert.Equal(new[] { " ", "-", "+", " " }, snippet!.Lines.Select(l => l.M));
        Assert.Equal("old value", snippet.Lines[1].T);
        Assert.Null(snippet.Lines[1].N);
        Assert.Equal(1, snippet.Removed);
    }

    [Fact]
    public void Refuses_when_the_file_and_the_patch_disagree()
    {
        var file = FrontFile().Replace("receivingAccountType || \"-\",", "somethingElse,");
        var (snippet, error) = DiffSnippet.Build(file, FrontPatch, 178, 189);
        Assert.Null(snippet);
        Assert.Contains("line 187", error);
    }

    [Fact]
    public void Refuses_a_range_past_the_end_of_the_file()
    {
        var (_, error) = DiffSnippet.Build("a\nb\n", "", 1, 5);
        Assert.Contains("past the end", error);
    }

    // ---- Pronunciation --------------------------------------------------------------------------

    [Theory]
    [InlineData("It reads Traditional IRA.", "It reads Traditional I-R-A.")]
    [InlineData("The PDF and ACH paths.", "The P-D-F and A-C-H paths.")]
    [InlineData("Before ABC-8939 shipped.", "Before A-B-C eighty-nine thirty-nine shipped.")]
    [InlineData("ABC-8905 and ABC-8900.", "A-B-C eighty-nine oh five and A-B-C eighty-nine hundred.")]
    [InlineData("It stays NULL in SQL.", "It stays null in sequel.")]
    [InlineData("Pulled from AH U/I, i.e. the wizard.", "Pulled from A-H U-I, that is, the wizard.")]
    public void Speaks_initialisms_and_ticket_keys_the_way_the_voice_can_say_them(string written, string spoken)
    {
        Assert.Equal(spoken, new Pronunciation().Speak(written));
    }

    [Fact]
    public void Project_overrides_win_over_the_general_rules()
    {
        var p = new Pronunciation(new Dictionary<string, string> { ["PrecisionIRA"] = "Precision I-R-A" });
        Assert.Equal("Historical Precision I-R-A documents.", p.Speak("Historical PrecisionIRA documents."));
    }

    [Fact]
    public void A_project_can_say_its_key_prefix_is_a_word()
    {
        var p = new Pronunciation(new Dictionary<string, string> { ["ALPHA"] = "Alpha" });
        Assert.Equal("Before Alpha eighty-nine thirty-nine shipped.", p.Speak("Before ALPHA-8939 shipped."));
        Assert.Equal("Before A-L-P-H-A eighty-nine thirty-nine shipped.", new Pronunciation().Speak("Before ALPHA-8939 shipped."));
    }

    [Fact]
    public void Sentences_do_not_break_inside_abbreviations()
    {
        var got = Pronunciation.Sentences("The P.D.F. renders. Then i.e. this stays. And U.S. rules apply? Yes. Done.");
        Assert.Equal(new[] { "The P.D.F. renders.", "Then i.e. this stays.", "And U.S. rules apply?", "Yes.", "Done." }, got);
    }

    // ---- WAV length -----------------------------------------------------------------------------

    [Fact]
    public void Reads_a_wav_length_from_its_header()
    {
        var path = Path.GetTempFileName();
        try
        {
            using (var w = new BinaryWriter(File.Create(path)))
            {
                const int rate = 24000, bytesPerSample = 2, seconds = 3;
                var data = rate * bytesPerSample * seconds;
                w.Write("RIFF"u8.ToArray()); w.Write(36 + data); w.Write("WAVE"u8.ToArray());
                w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1);
                w.Write(rate); w.Write(rate * bytesPerSample); w.Write((short)bytesPerSample); w.Write((short)16);
                w.Write("data"u8.ToArray()); w.Write(data); w.Write(new byte[data]);
            }
            Assert.Equal(3.0, Narrator.WavSeconds(path)!.Value, 3);
        }
        finally { File.Delete(path); }
    }

    // ---- the validator --------------------------------------------------------------------------

    private static AnalysisDocument.Doc Doc() => new("h", new[]
    {
        new AnalysisDocument.Section("The path", new[]
        {
            new AnalysisDocument.Block("userFlow")
            {
                Nodes = new[]
                {
                    new AnalysisDocument.FlowNode("gate", "decision", "Rollover paid by check?", null, null, "unchanged", null, null),
                    new AnalysisDocument.FlowNode("legacy", "screen", "Shows the original selection", null, null, "new", null, null),
                },
                Edges = new[] { new AnalysisDocument.FlowEdge("gate", "legacy", "no", "new") },
            },
        }),
        new AnalysisDocument.Section("Risks", new[]
        {
            new AnalysisDocument.Block("callout") { Title = "Untested join", Text = "No test walks it.", Tone = "risk" },
            new AnalysisDocument.Block("question") { Text = "Freeze one field at a time?" },
        }),
    });

    private static WalkthroughValidator.Context Ctx() => new(
        Doc(),
        "Where the highlighted blank section is, it would read “Receiving Account Type” and to the right the type.\n\nTesting - ensure Enrollments are not impacted.",
        new[] { new WalkthroughValidator.Shot(0, "The new row.", "/shot/0", 1280, 720, new ImageRect(668, 574, 530, 30)) },
        new[] { new WalkthroughValidator.Attachment("mockup.png", "/att/mockup.png", 1462, 812) },
        (path, from, to) => Task.FromResult(path == "distribution.tsx"
            ? DiffSnippet.Build(FrontFile(), FrontPatch, from, to)
            : ((DiffSnippet.Result?)null, (string?)"no such file in the diff")));

    private static RawScript Script(params RawScene[] scenes) => new(scenes);
    private static RawBeat Say(string s) => new(s);

    [Fact]
    public async Task A_good_script_passes_every_check_untouched()
    {
        var result = await WalkthroughValidator.ValidateAsync(Script(
            new RawScene("Overview", "title") { Beats = new[] { Say("ALPHA-8940.") } },
            new RawScene("The ask", "ask")
            {
                Quotes = new[] { "it would read \"Receiving Account Type\"" },   // straight quotes match curly ones
                Attachments = new[] { "mockup.png" },
                Beats = new[] { Say("The ask.") with { Quotes = new[] { 0 }, Image = "mockup.png", Zoom = new[] { 700, 560, 760, 252 } } },
            },
            new RawScene("The result", "shot") { Shot = 0, Beats = new[] { Say("Here it is.") with { Focus = true } } },
            new RawScene("The path", "flow") { Block = "0.0", Beats = new[] { Say("Older ones fall back.") with { Nodes = new[] { "gate", "legacy" } } } },
            new RawScene("The change", "code")
            {
                File = "distribution.tsx", From = 178, To = 189, Reveal = true,
                Beats = new[] { Say("It used to stop here.") with { Lines = new[] { 179, 184 } }, Say("Now one more.") with { Lines = new[] { 185, 188 }, Reveal = true } },
            },
            new RawScene("Risks", "cards") { Blocks = new[] { "1.0" }, Beats = new[] { Say("One risk.") with { Show = 1 } } },
            new RawScene("Worth sitting with", "question") { Block = "1.1", Beats = new[] { Say("One question.") with { Show = 1 } } }), Ctx());

        Assert.Empty(result.Problems);
        Assert.Equal(7, result.Scenes.Count);

        var shot = result.Scenes[2].Beats[0].Cue!;
        Assert.Equal(new ImageRect(668, 574, 530, 30), shot.Box);
        Assert.NotNull(shot.Zoom);   // QA's focus, padded

        var code = result.Scenes[4].Code!;
        Assert.True(code.Reveal);
        Assert.Equal(4, code.Added);
    }

    [Fact]
    public async Task Pointers_that_miss_are_dropped_and_explained()
    {
        var result = await WalkthroughValidator.ValidateAsync(Script(
            new RawScene("Ask", "ask") { Quotes = new[] { "words the ticket never said" }, Beats = new[] { Say("x") } },
            new RawScene("Shot", "shot") { Shot = 4, Beats = new[] { Say("x") } },
            new RawScene("Flow", "flow") { Block = "0.0", Beats = new[] { Say("x") with { Nodes = new[] { "invented" } } } },
            new RawScene("Code", "code") { File = "nowhere.cs", From = 1, To = 5, Beats = new[] { Say("x") } },
            new RawScene("Cards", "cards") { Blocks = new[] { "0.0" }, Beats = new[] { Say("x") } }), Ctx());

        Assert.Single(result.Scenes);   // only the flow survives, with its bad node removed
        Assert.Null(result.Scenes[0].Beats[0].Cue!.Nodes);
        Assert.Contains(result.Problems, p => p.Contains("not in the ticket's description word for word"));
        Assert.Contains(result.Problems, p => p.Contains("shot 4 does not exist"));
        Assert.Contains(result.Problems, p => p.Contains("node \"invented\" is not in the flow"));
        Assert.Contains(result.Problems, p => p.Contains("nowhere.cs") && p.Contains("no such file"));
        Assert.Contains(result.Problems, p => p.Contains("\"0.0\" is not a callout"));
    }

    [Fact]
    public async Task A_box_off_the_image_and_highlight_lines_outside_the_range_are_refused()
    {
        var result = await WalkthroughValidator.ValidateAsync(Script(
            new RawScene("Shot", "shot") { Shot = 0, Beats = new[] { Say("x") with { Box = new[] { 1200, 700, 200, 50 } } } },
            new RawScene("Code", "code") { File = "distribution.tsx", From = 178, To = 189, Beats = new[] { Say("x") with { Lines = new[] { 170, 180 } } } }), Ctx());

        Assert.Null(result.Scenes[0].Beats[0].Cue!.Box);
        Assert.Null(result.Scenes[1].Beats[0].Cue!.Lines);
        Assert.Contains(result.Problems, p => p.Contains("box runs off the image"));
        Assert.Contains(result.Problems, p => p.Contains("fall outside the scene's range"));
    }

    [Fact]
    public async Task A_reveal_no_beat_triggers_is_undone_so_the_new_code_is_not_hidden_all_scene()
    {
        var result = await WalkthroughValidator.ValidateAsync(Script(
            new RawScene("Code", "code") { File = "distribution.tsx", From = 178, To = 189, Reveal = true, Beats = new[] { Say("x") } }), Ctx());

        Assert.False(result.Scenes[0].Code!.Reveal);
        Assert.Contains(result.Problems, p => p.Contains("no beat reveals"));
    }

    [Fact]
    public async Task A_sentence_too_long_to_voice_is_reported()
    {
        var result = await WalkthroughValidator.ValidateAsync(Script(
            new RawScene("Overview", "title") { Beats = new[] { Say(string.Join(" ", Enumerable.Repeat("word", 80)) + ".") } }), Ctx());
        Assert.Contains(result.Problems, p => p.Contains("keep each under"));
    }

    [Fact]
    public async Task A_script_that_runs_long_is_sent_back_to_be_cut_without_losing_anything()
    {
        var beat = Say(string.Join(" ", Enumerable.Repeat("word", 25)) + ".");
        var result = await WalkthroughValidator.ValidateAsync(Script(
            Enumerable.Range(0, 5).Select(i => new RawScene($"S{i}", "title") { Beats = Enumerable.Repeat(beat, 4).ToArray() }).ToArray()), Ctx());

        Assert.Equal(5, result.Scenes.Count);
        Assert.Contains(result.Problems, p => p.Contains("500 words") && p.Contains("under 420"));
    }

    // ---- pictures ------------------------------------------------------------------------------

    private static RawPictureItem Item(string id, string text, string? kind = null, string? change = null, string? @in = null) =>
        new(id, text) { Kind = kind, Change = change, In = @in };

    [Fact]
    public async Task A_picture_keeps_its_ids_across_beats_and_a_beat_without_one_holds_the_last()
    {
        var result = await WalkthroughValidator.ValidateAsync(Script(
            new RawScene("The idea", "picture")
            {
                Beats = new[]
                {
                    Say("Every rollover reached the same screen.") with { Picture = new RawPicture(new[] { Item("gate", "Rollover"), Item("screen", "Distribution screen") })
                        { Arrows = new[] { new RawArrow("gate", "screen") } } },
                    Say("Now older ones take a turn first.") with { Picture = new RawPicture(new[]
                        {
                            Item("gate", "Rollover"), Item("legacy", "Original selection", change: "new"), Item("screen", "Distribution screen"),
                        }) { Arrows = new[] { new RawArrow("gate", "legacy") { Change = "new" }, new RawArrow("legacy", "screen") } } },
                    Say("That turn is the whole change.") with { Lit = new[] { "legacy" } },
                },
            }), Ctx() with { Vocabulary = "src/distribution.tsx" });

        Assert.Empty(result.Problems);
        var beats = result.Scenes.Single().Beats;
        Assert.Equal(2, beats[0].Cue!.Picture!.Items.Count);
        Assert.Equal("new", beats[1].Cue!.Picture!.Items[1].Change);
        Assert.Null(beats[2].Cue!.Picture);                       // holds the second picture
        Assert.Equal(new[] { "legacy" }, beats[2].Cue!.Lit);      // and can still point into it
    }

    [Fact]
    public async Task A_picture_loses_what_does_not_hold_together_and_says_why()
    {
        var result = await WalkthroughValidator.ValidateAsync(Script(
            new RawScene("Bad", "picture")
            {
                Beats = new[]
                {
                    Say("x") with
                    {
                        Picture = new RawPicture(new[]
                        {
                            Item("gate", "Rollover check"),
                            Item("gate", "Rollover again"),
                            Item("kafka", "Kafka broker queue"),
                            Item("legacy", "Original selection", @in: "gate"),
                            Item("sel", "Selection", kind: "triangle"),
                        }) { Arrows = new[] { new RawArrow("gate", "kafka"), new RawArrow("gate", "legacy") } },
                        Lit = new[] { "kafka" },
                    },
                },
            }), Ctx());

        var picture = result.Scenes.Single().Beats[0].Cue!.Picture!;
        Assert.Equal(new[] { "gate", "legacy" }, picture.Items.Select(i => i.Id));
        Assert.Null(picture.Items[1].In);                          // "gate" is a box, not a group
        Assert.Single(picture.Arrows);
        Assert.Contains(result.Problems, p => p.Contains("used twice"));
        Assert.Contains(result.Problems, p => p.Contains("\"kafka\"") && p.Contains("none of its words"));
        Assert.Contains(result.Problems, p => p.Contains("is not a group in this picture"));
        Assert.Contains(result.Problems, p => p.Contains("kind \"triangle\""));
        Assert.Contains(result.Problems, p => p.Contains("arrow gate -> kafka"));
        Assert.Contains(result.Problems, p => p.Contains("lit \"kafka\""));
    }

    [Fact]
    public async Task A_picture_scene_that_opens_on_nothing_is_left_out()
    {
        var result = await WalkthroughValidator.ValidateAsync(Script(
            new RawScene("Empty", "picture") { Beats = new[] { Say("x"), Say("y") with { Picture = new RawPicture(new[] { Item("gate", "Rollover") }) } } }), Ctx());

        Assert.Empty(result.Scenes);
        Assert.Contains(result.Problems, p => p.Contains("first beat must draw a picture"));
    }

    [Fact]
    public void The_brief_teaches_pictures_by_id_not_by_animation()
    {
        var p = WalkthroughPrompt.Build(new Whetstone.Models.Ticket { Id = 4, Title = "t", Branch = "b" },
            new Whetstone.Models.TicketAnalysis(), PromptDoc, "Save an address", "the ask",
            Array.Empty<WalkthroughPrompt.ShotMaterial>(), Array.Empty<WalkthroughPrompt.AttachmentMaterial>(),
            Array.Empty<WalkthroughPrompt.ChangedFile>(), "abc1234");
        Assert.Contains("## Pictures", p);
        Assert.Contains("BY ID", p);
        Assert.Contains("\"picture\": a small animated diagram", p);
    }

    // ---- the prompt, for each subject ----------------------------------------------------------

    private static readonly AnalysisDocument.Doc PromptDoc = new("h", new[]
    {
        new AnalysisDocument.Section("How it works now", new[]
        {
            new AnalysisDocument.Block("userFlow")
            {
                Nodes = new[] { new AnalysisDocument.FlowNode("n1", "screen", "Addresses", null, null, "new", null, 1) },
                Edges = Array.Empty<AnalysisDocument.FlowEdge>(),
            },
        }),
    });

    [Fact]
    public void A_tickets_brief_still_quotes_its_description_and_reads_lines_at_its_commit()
    {
        var p = WalkthroughPrompt.Build(new Whetstone.Models.Ticket { Id = 4, Title = "t", Branch = "b" },
            new Whetstone.Models.TicketAnalysis(), PromptDoc, "Save an address", "the ask as written",
            Array.Empty<WalkthroughPrompt.ShotMaterial>(), Array.Empty<WalkthroughPrompt.AttachmentMaterial>(),
            new[] { new WalkthroughPrompt.ChangedFile("src/a.ts", 3, 1) }, "abc1234").ReplaceLineEndings("\n");

        Assert.Contains("analysed at commit abc1234 on branch b.\n\n## How to write for the ear", p);
        Assert.Contains("`git show abc1234:<path>`", p);
        Assert.Contains("## What you may point at\n\n### Analysis blocks", p);
        Assert.Contains("### The ticket's description (quote from this, verbatim)\n```\nthe ask as written", p);
        Assert.Contains("- 0.0 userFlow", p);
    }

    [Fact]
    public void A_units_brief_quotes_its_spec_and_reads_its_code_at_the_end_of_its_range()
    {
        var p = WalkthroughPrompt.BuildForUnit("Unit 1: Addresses", "First plan", PromptDoc, "A1 can save an address",
            new[] { new WalkthroughPrompt.ShotMaterial(3, "the list", "C:/r/3.png", 800, 600, null) },
            new[] { new WalkthroughPrompt.ChangedFile("src/a.ts", 3, 1) }, "1111111111aa", "2222222222bb").ReplaceLineEndings("\n");

        Assert.Contains("The unit: Unit 1: Addresses, of the plan \"First plan\".", p);
        Assert.Contains("range 1111111111..2222222222", p);
        Assert.Contains("`git show 2222222222bb:<path>`", p);
        Assert.Contains("- shot 3: 800x600 at C:/r/3.png", p);
        Assert.Contains("### The unit's spec (quote from this, verbatim)\n```\nA1 can save an address", p);
        Assert.DoesNotContain("ticket's own words", p);
    }
}
