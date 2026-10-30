using System.Text;
using System.Text.Json;
using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services.Tracker;
using Microsoft.EntityFrameworkCore;

namespace Whetstone.Services;

/// <summary>
/// Generates a Classroom analysis: a headless agent run, pointed at one ticket, that reads the
/// branch and the target repo and comes back with a typed teaching document.
/// <para>
/// Three things make this safe to run whenever the human feels like it, and all three are
/// deliberate. It is <b>read-only</b>, enforced by the CLI rather than asked for in the prompt —
/// how, is the provider's business (see <see cref="AgentProvider.BuildAnalysisRun"/>). It takes
/// <b>no resource lease and creates no worktree</b>, so it can never queue behind, or block, real
/// pipeline work. And its output is <b>never read back by any phase</b>: nothing in the state
/// machine knows this table exists.
/// </para>
/// <para>
/// One run at a time, globally. Two concurrent analyses would be two more agent processes competing
/// with the heartbeat's own workers for the same machine, to produce something a person can only
/// read one of. That single-slot rule is also what lets the run share one scratch directory.
/// </para>
/// <para>
/// That rule is <b>not enforced here</b>. This class executes exactly one (ticket, lens) and knows
/// nothing about whether it should be the one running — <see cref="AnalysisQueue"/> owns the single
/// slot, the waiting line, and every decision about when. Everything below therefore runs at
/// dequeue time, which for a queued item is minutes after it was asked for: the login is renewed,
/// HEAD is re-read, and the tracker is asked for the title here rather than at the click, because all
/// three answers go stale in the wait.
/// </para>
/// </summary>
public class AnalysisRunner : IAnalysisExecutor
{
    /// <summary>
    /// A run that has not produced a document in this long is not going to. Without it, one wedged
    /// process holds the single slot — and now the whole queue behind it — until the dashboard
    /// restarts.
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(15);

    private readonly IServiceScopeFactory _scopes;
    private readonly ProjectRegistry _registry;
    private readonly LinkedIssueResolver _resolver;
    private readonly IssueTrackerFactory _trackers;
    private readonly AgentProviderFactory _providers;
    private readonly ClaudeUsageService _usage;
    private readonly DashboardPaths _paths;
    private readonly Walkthrough.WalkthroughService _walkthroughs;
    private readonly ILogger<AnalysisRunner> _log;

    public AnalysisRunner(IServiceScopeFactory scopes, ProjectRegistry registry,
        LinkedIssueResolver resolver, IssueTrackerFactory trackers, AgentProviderFactory providers,
        ClaudeUsageService usage, DashboardPaths paths, Walkthrough.WalkthroughService walkthroughs,
        ILogger<AnalysisRunner> log)
    {
        _scopes = scopes;
        _registry = registry;
        _resolver = resolver;
        _trackers = trackers;
        _providers = providers;
        _usage = usage;
        _paths = paths;
        _walkthroughs = walkthroughs;
        _log = log;
    }

    /// <summary>
    /// What this run is being held to, and how to tell later whether it still applies.
    /// <para>
    /// Computed once at dequeue and carried the length of the run, because both halves are read at
    /// opposite ends of it: the segments go into the prompt on the way out and back into
    /// <see cref="AnalysisDocument.Parse"/> on the way in, and the two MUST be the same list — a map
    /// joined against a second, freshly-fetched segmentation would silently mis-attribute every
    /// ruling if the tracker's copy changed while the agent was reading.
    /// </para>
    /// </summary>
    /// <param name="DescriptionHash">Of the ticket's own requirement text as it stood at generation.
    /// The staleness axis <see cref="TicketAnalysisService.IsStale"/> cannot see: phase and HEAD both
    /// say what WE did, and neither moves when someone rewrites the ticket underneath us.</param>
    private record Requirements(
        IReadOnlyList<DescriptionSegmenter.Segment> Segments, int Dropped, string DescriptionHash);

    /// <summary>
    /// Read one ticket and write one analysis, start to finish. Returns the stored analysis, or the
    /// sentence explaining why there isn't one — never both, never neither.
    /// <para>
    /// Whether this ticket <i>should</i> be studied at all was settled a moment ago by the queue's
    /// gate, and is not re-asked here. What IS asked here is everything that had to wait until the
    /// item's turn: the login, the commit at HEAD, and the tracker's version of the title.
    /// </para>
    /// </summary>
    public async Task<(int? analysisId, string? error)> RunAsync(
        int ticketId, AnalysisLens lens, IRunProgress progress, CancellationToken ct)
    {
        Ticket ticket;
        ProjectConfig cfg;
        string readDir;
        AnalysisPin pin;
        string prompt;
        Requirements reqs;

        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();
            var found = await db.Tickets
                .Include(t => t.WorkItems).Include(t => t.QaItems).Include(t => t.Comments)
                // The shots are not read as images — the run has no eyes on them. Their CAPTIONS
                // are what it needs, so that a coverage ruling can name the one that shows its
                // requirement working and the reader gets a picture instead of a claim.
                .Include(t => t.QaShots)
                .FirstOrDefaultAsync(t => t.Id == ticketId, ct);

            if (found == null) return (null, "that ticket no longer exists");
            ticket = found;

            cfg = _registry.GetProjectConfig(ticket.ProjectKey);
            if (string.IsNullOrWhiteSpace(cfg.ProjectPath) || !Directory.Exists(cfg.ProjectPath))
                return (null, $"project '{ticket.ProjectKey}' has no readable repo path on this machine");

            // Read now, not when this was queued. The worktree can be pruned and the branch can gain
            // commits while an item waits, so deciding either at the click would point the run at a
            // directory that has since gone — and would stamp the stored analysis with a commit it
            // never actually read.
            readDir = ReadDir(ticket, cfg);
            // Which code it reads: the HEAD and tree of readDir for a project of one, each changed
            // repo's for a project of several, whose workspace has no HEAD of its own (AnalysisPin).
            pin = AnalysisPin.Of(ticket, cfg);

            // A CLI handed a token that aged out overnight does not fail fast: it retries the 401 for
            // three minutes and then dies with an empty stderr. Renewing here turns the usual version
            // of that into no failure at all. Checked at dequeue because a check at the click is
            // already stale by the third item in the queue, which is exactly the case it exists for.
            if (await _usage.EnsureSignedInAsync() is { } authError) return (null, authError);

            // the tracker over the network. Here rather than at the click so that joining the queue is
            // instant and cannot fail for a reason that has nothing to do with queueing.
            // What this run is likely to cost, from the ones before it. Reported before the process
            // starts so the panel has a denominator from the first poll rather than after the first
            // tool call — the wait for the CLI to boot is the part of a run that feels longest.
            await ReportExpectationsAsync(db, lens, progress, ct);

            var resolved = await _resolver.ResolveAsync(ticket);

            // The requirement text, cut into the lines the run will be asked to rule on one by one.
            // Also at dequeue: an ancestor's acceptance criteria can be edited while an item waits,
            // and the map has to describe the words the run actually read.
            var (map, siblings) = await RequirementSourcesAsync(ticket, resolved.Description);
            reqs = new Requirements(map.Segments, map.Dropped,
                DescriptionSegmenter.Fingerprint(resolved.Description));

            prompt = BuildPrompt(ticket, cfg, lens, readDir, resolved.Title, resolved.Description,
                map, siblings);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not prepare the Classroom analysis for ticket {TicketId}", ticketId);
            return (null, ex.Message);
        }

        var (analysisId, error) = await ExecuteAsync(ticket, lens, readDir, pin, prompt, reqs, progress, ct);

        // Every analysis gets its walkthrough, in the same slot, before the slot is given back. The
        // analysis is already saved: GenerateAsync records its own failure on its own row and never
        // turns into this run's error.
        if (analysisId is int id) await _walkthroughs.GenerateAsync(id, progress, ct);
        return (analysisId, error);
    }

    public Task RunWalkthroughAsync(int analysisId, IRunProgress progress, CancellationToken ct) =>
        _walkthroughs.GenerateAsync(analysisId, progress, ct);

    /// <summary>
    /// A unit report takes the same slot; the work is its own runner's. Its walkthrough follows in the
    /// slot, as an analysis's does, and never fails the report.
    /// </summary>
    public async Task<(int? reportId, string? error)> RunUnitAsync(UnitReports.UnitSubject unit, IRunProgress progress, CancellationToken ct)
    {
        var (reportId, error) = await _scopes.CreateScope().ServiceProvider
            .GetRequiredService<UnitReports.UnitReportRunner>().RunAsync(unit, progress, ct);
        if (reportId is int id) await _walkthroughs.GenerateForUnitAsync(id, progress, ct);
        return (reportId, error);
    }

    public Task RunUnitWalkthroughAsync(int reportId, IRunProgress progress, CancellationToken ct) =>
        _walkthroughs.GenerateForUnitAsync(reportId, progress, ct);

    /// <summary>
    /// Every body of text this ticket can legitimately be held to, and the siblings an unmet ask may
    /// legitimately have gone to.
    /// <para>
    /// The asymmetry is the whole reason a hierarchy does not make this section unreadable:
    /// <b>ancestors supply requirements, siblings absorb them.</b> A sub-task that carves one bullet
    /// out of a fully-specified story inherits the story's criteria, so ancestors are a source of
    /// lines to rule on. A sibling never is — it is the neighbouring piece of work, and its criteria
    /// are explicitly not this ticket's job. What a sibling is good for is being the ANSWER to a
    /// line this ticket did not do, so they arrive as names only, for a <c>deferred</c> ruling to
    /// point at.
    /// </para>
    /// </summary>
    private async Task<(DescriptionSegmenter.Result Map, IReadOnlyList<IssueRef> Siblings)>
        RequirementSourcesAsync(Ticket t, string? ownText)
    {
        var sources = new List<DescriptionSegmenter.Source> { new(null, null, ownText) };
        IReadOnlyList<IssueRef> siblings = Array.Empty<IssueRef>();

        if (!string.IsNullOrEmpty(t.IssueKey))
        {
            try
            {
                var tracker = _trackers.For(t.ProjectKey);
                if (tracker.IsConfigured && await tracker.GetIssueAsync(t.IssueKey) is { } issue)
                {
                    // No attachment caching: this run has no Write tool and nowhere to put a file,
                    // and a mockup it cannot open is not a requirement it can rule on.
                    var ancestry = await IssueContextBuilder.WalkAncestryAsync(
                        issue.Key, issue, key => tracker.GetAncestorAsync(key));

                    // An unreadable ancestor contributes nothing rather than an empty description
                    // that would read as "the parent asked for nothing".
                    foreach (var a in ancestry.ParentChain.Where(a => a.Readable))
                        sources.Add(new DescriptionSegmenter.Source(a.Key, AncestorRole(a.Level, a.Type), a.Description));

                    siblings = ancestry.Siblings;
                }
            }
            catch (Exception ex)
            {
                // Inherited criteria are an enrichment. A tracker that will not answer costs the map
                // its ancestors, not the analysis — and the ticket's own text is still the bulk of it.
                _log.LogDebug(ex, "Could not read the parent chain for {IssueKey}", t.IssueKey);
            }
        }

        return (DescriptionSegmenter.Compose(sources), siblings);
    }

    /// <summary>
    /// How an ancestor is described to the reader. The tracker's own word for it wherever there is
    /// one — "epic" says more than "grandparent", and says it in the vocabulary the reader's board
    /// already uses.
    /// </summary>
    private static string AncestorRole(int level, string? type) =>
        !string.IsNullOrWhiteSpace(type) ? type!.Trim().ToLowerInvariant()
        : level == 1 ? "parent"
        : level == 2 ? "grandparent"
        : $"ancestor {level} levels up";

    /// <summary>
    /// How long runs like this one take, and how many tool calls they make, from the last few that
    /// finished.
    /// <para>
    /// Median rather than mean: one run that hit the fifteen-minute timeout would drag an average
    /// far enough to make every later bar crawl. Same lens first, because the lenses genuinely
    /// differ — a Code read walks five to eight files that a Decisions read never opens — and only
    /// then any lens, because a rough denominator beats none. Nothing is reported when nothing
    /// comparable has ever finished; the client says "no estimate" rather than being handed a
    /// constant dressed up as history.
    /// </para>
    /// </summary>
    private static async Task ReportExpectationsAsync(
        DashboardDbContext db, AnalysisLens lens, IRunProgress progress, CancellationToken ct)
    {
        const int Window = 8;

        try
        {
            var recent = await db.TicketAnalyses.AsNoTracking()
                .OrderByDescending(a => a.Id)
                .Select(a => new { a.Lens, a.DurationMs, a.StepCount })
                .Take(Window * 4)
                .ToListAsync(ct);

            var sameLens = recent.Where(a => a.Lens == lens).Take(Window).ToList();
            var pool = sameLens.Count >= 3 ? sameLens : recent.Take(Window).ToList();

            var seconds = Median(pool.Where(a => a.DurationMs > 0).Select(a => (double)a.DurationMs)) / 1000.0;
            // Analyses written before the run counted its own steps store a zero, which is absence
            // rather than a very quick run — they are left out rather than allowed to drag it down.
            var steps = Median(pool.Where(a => a.StepCount > 0).Select(a => (double)a.StepCount));

            if (seconds > 0 || steps > 0) progress.Expect(seconds, (int)Math.Round(steps));
        }
        catch (OperationCanceledException) { throw; }
        catch { /* an estimate is a nicety; a run must not fail for want of one */ }
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0) return 0;
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    private async Task<(int? analysisId, string? error)> ExecuteAsync(
        Ticket ticket, AnalysisLens lens, string readDir, AnalysisPin pin, string prompt,
        Requirements reqs, IRunProgress progress, CancellationToken ct)
    {
        // The Classroom is a role of its own, with its own provider and model - see
        // AgentRoles.Analysis.
        var provider = _providers.For(AgentRoles.Analysis);
        // One analysis runs at a time, so one scratch directory named after the feature is enough.
        var scratch = Path.Combine(_paths.RunDir, "analysis");

        try
        {
            var run = await AgentJsonRunner.RunAsync(provider, readDir, scratch, prompt,
                AnalysisDocument.JsonSchema, progress, Timeout, "analysis", _log, ct);
            if (run.Outcome is not { } outcome) return (null, run.Error);
            return await PersistAsync(ticket, lens, pin, outcome, reqs, run.DurationMs, run.Steps);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Classroom analysis for ticket {TicketId} failed", ticket.Id);
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// Store the document the provider came back with. Whatever envelope, stream or file it actually
    /// arrived in was unwrapped by the provider — see <see cref="AgentProvider.ReadAnalysis"/>. What
    /// reaches here is a validated object and its accounting, or an error explaining neither exists.
    /// </summary>
    private async Task<(int? analysisId, string? error)> PersistAsync(
        Ticket ticket, AnalysisLens lens, AnalysisPin pin, AgentOutcome outcome, Requirements reqs,
        int durationMs, int stepCount)
    {
        if (outcome.Error != null) return (null, outcome.Error);
        if (outcome.Structured is not { } structured)
            return (null, "the run finished without producing an analysis document");

        var (doc, error) = AnalysisDocument.Parse(structured, reqs.Segments);
        if (doc == null) return (null, error ?? "the analysis document was empty");

        var row = new TicketAnalysis
        {
            TicketId = ticket.Id,
            ProjectKey = ticket.ProjectKey,
            PhaseAtGeneration = ticket.Phase,
            Lens = lens,
            HeadSha = pin.HeadSha,
            TreeSha = pin.TreeSha,
            RepoHeadsJson = pin.ReposJson(),
            Branch = ticket.Branch,
            Headline = doc.Headline,
            BlocksJson = AnalysisDocument.Serialize(doc),
            DescriptionHash = reqs.DescriptionHash,
            InputTokens = outcome.InputTokens,
            OutputTokens = outcome.OutputTokens,
            CacheReadTokens = outcome.CacheReadTokens,
            CacheCreationTokens = outcome.CacheCreationTokens,
            Model = outcome.Model,
            DurationMs = durationMs,
            StepCount = stepCount,
        };

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();
        db.TicketAnalyses.Add(row);
        await db.SaveChangesAsync();
        return (row.Id, null);
    }

    // ---- prompt -------------------------------------------------------------

    /// <summary>
    /// What each lens asks for extra of. The spine of sections never changes — a lens shifts the
    /// weighting, so two analyses of the same ticket stay comparable instead of being two different
    /// kinds of document.
    /// </summary>
    private static string LensDirection(AnalysisLens lens) => lens switch
    {
        AnalysisLens.Decisions =>
            "LENS: DECISIONS. Spend most of your budget on \"Why this way\" and \"How it worked before\". "
            + "The comparison table of routes considered is the centrepiece — make it real, with the constraint that "
            + "killed each rejected option. Keep the reading path short.",
        AnalysisLens.Code =>
            "LENS: CODE. Spend most of your budget on \"What changed\" and \"Where to look\". The reading path should "
            + "be long enough to actually navigate the change (5-8 steps), ordered so each step makes the next one "
            + "legible. Keep the design-space table to the one alternative that mattered.",
        AnalysisLens.Testing =>
            "LENS: TESTING. Spend most of your budget on \"How we know it works\" and \"What would catch you out\". "
            + "Table every acceptance criterion against what actually verified it, and be explicit about the ones "
            + "nothing exercised. Name the failure modes still open.",
        _ =>
            "LENS: FULL. Balance the sections. This is the first read of the ticket and should stand on its own.",
    };

    /// <summary>
    /// The whole instruction, assembled. A pure function of its arguments and public for the same
    /// reason <see cref="ReadDir"/> is: it is worth being able to look at without starting a
    /// process, and three of its parts now appear only under conditions
    /// (<see cref="DescriptionSegmenter"/> found lines; the issue has siblings; the description
    /// overflowed) that are otherwise only reachable by running an agent for four minutes.
    /// </summary>
    public static string BuildPrompt(Ticket t, ProjectConfig cfg, AnalysisLens lens, string readDir,
        string? effectiveTitle, string? effectiveDescription,
        DescriptionSegmenter.Result map, IReadOnlyList<IssueRef> siblings)
    {
        string P(string path) => path.Replace('\\', '/');

        // The ticket's own record, verbatim. Everything the pipeline wrote down about this work,
        // handed over in one blob so the run spends its time reading the REPO instead of the DB.
        var record = JsonSerializer.Serialize(new
        {
            id = t.Id,
            title = effectiveTitle ?? t.Title,
            description = effectiveDescription ?? t.Description,
            phase = t.Phase.ToString(),
            issueKey = t.IssueKey,
            branch = t.Branch,
            masterNotes = t.MasterNotes,
            implementationSummary = t.ImplementationSummary,
            existingLearningBrief = t.LearningBrief,
            workItems = t.WorkItems.OrderBy(w => w.Order).Select(w => new { w.Text, w.Done }),
            qaItems = t.QaItems.OrderBy(q => q.Order).Select(q => new { q.Text, status = q.Status.ToString(), q.Note }),
            qaShots = t.QaShots.OrderBy(s => s.Order).Select(s => new { s.Order, s.Caption }),
            comments = t.Comments.OrderBy(c => c.CreatedAt)
                .Select(c => new { author = c.Author.ToString(), phase = c.Phase?.ToString(), c.Body, c.CreatedAt }),
        }, new JsonSerializerOptions { WriteIndented = true });

        // Each conditional note is its own raw string, de-indented to column zero by its own closing
        // delimiter. Written inline in the template's interpolation holes they would arrive carrying
        // the template's indentation - which C# does not strip from an interpolated VALUE, only from
        // the literal's own lines - and eight leading spaces turns a paragraph of instruction into a
        // markdown code block.
        var inheritedNote = !map.Segments.Any(s => s.Origin != null) ? "" : """

            Lines marked `inherited` were written on an ANCESTOR, not on this ticket. They still
            count - a sub-task that carves one bullet out of a story is held to the story's criteria
            - but say so when you rule on one.
            """;

        var truncatedNote = map.Dropped == 0 ? "" : $"""

            This description is long enough that {map.Dropped} further line(s) were cut before you
            saw them, so the map you produce is knowingly incomplete.
            """;

        var siblingNote = siblings.Count == 0 ? "" : """

            The neighbouring work, for `deferredTo` to point at. These are NOT yours to deliver and
            nothing in them is a line you rule on - they are where a line this ticket did not do may
            legitimately have gone:


            """
            + string.Join("\n", siblings.Take(12).Select(s =>
                $"- {s.Key}"
                + (string.IsNullOrWhiteSpace(s.Summary) ? "" : $" - {s.Summary}")
                + (string.IsNullOrWhiteSpace(s.Status) ? "" : $" [{s.Status}]")));

        // The requirement text, pre-cut. Nothing is asked of the run about WHERE the lines are —
        // that was settled in C# before the process started, which is what lets the joined map show
        // a line the run never mentioned.
        var requirements = map.Segments.Count == 0 ? "" : $$"""

        ## The requirement text

        Every line this ticket can be held to, already numbered. `S1` and friends are ids you will
        quote back; the words beside them are the reader's own, and you will never retype them.
        {{inheritedNote}}{{truncatedNote}}

        ```
        {{DescriptionSegmenter.ForPrompt(map.Segments)}}
        ```
        {{siblingNote}}

        """;

        // Both halves of the coverage instruction are conditional on there being requirement text at
        // all. On a locally-raised ticket with a one-line title there is nothing to be held to, and
        // asking for the section anyway invites a map of the title.
        var coverageSection = map.Segments.Count == 0 ? "" : $$"""
        10. **Requirement coverage** - one `coverage` block: a ruling on every numbered line of the
            requirement text above. This is what answers "did we build what was asked for", and the
            reader sees it FIRST, above section 1, however far down the document you write it.

            **Write it last.** Every section above is you working out what this change actually did.
            Ruling on {{map.Segments.Count}} requirements before doing that work is how a coverage map
            becomes {{map.Segments.Count}} guesses.

            Set the block's `text` to **the whole map in one sentence**: what landed and what did
            not, **named in the ticket's own words**. "Everything in the criteria landed except the
            audit row; the bulk path and the statement were never touched." It is read before any row
            is, and it is the answer to the question the reader arrived with - so it names the gaps
            rather than counting them, and it is one sentence, not three. A summary that lists ids
            ("the context is S1, S2, S6, S10") has answered nothing: the reader cannot look those up.

            Emit one ruling per id - `{id, verdict, evidence, refs, qa, shot, deferredTo}` - and **do
            not retype the line**. The reader is shown their own words; a paraphrase of a requirement
            is the thing they would then have to go and check against the real one.

            **The ids are handles for this exchange and nothing else - the reader never sees them.**
            An `S4` written into `evidence`, into the summary, or anywhere else in the document is a
            reference to something they cannot look up, in a section whose whole job is to be
            readable. To point at another requirement, quote three or four of ITS OWN WORDS: "the
            same gate as the account-holder selector", never "the same gate as S5".

            - `met` - this change implements it. **Requires `refs`.** A verdict you cannot point at
              is downgraded to `unknown` before the reader sees it, so pointing at nothing costs you
              the verdict rather than buying you one.
            - `partial` - some of it landed. `evidence` must say which part did NOT.
            - `gap` - nothing in this change addresses it. **This is a useful answer**, and the one
              the reader opened the section for. A map with no gaps on a ticket that has them is
              worth less than no map.
            - `deferred` - deliberately not this ticket's job. **Requires `deferredTo`**: the sibling
              key that owns it, or the comment that descoped it. Without one it is recorded as a gap,
              because "we decided not to" and "we did not" are the same thing to a reader with no
              pointer to follow.
            - `unknown` - you could not tell from the diff. Say in `evidence` what you would have had
              to read. Reaching for this is not a failure; reaching for `met` instead is.
            - `context` - background, a repro step, an environment note, a link. Not something anyone
              asked to be built. Used honestly this is most of a long description; used to make the
              rail look green it is the one way to hide a requirement, and the reader can see every
              line you called context sitting there in their own ticket.

            `evidence` is one sentence in the same voice as the rest of the document: what was done,
            not a restatement of what was asked. `refs` are 1-3 `file:line` under the same rule as a
            reading path - lines you opened, not lines you assumed. `qa` quotes the QA item from the
            record that exercised this line, where one did.

            `shot` is the `order` of the QA screenshot in the record that **shows this requirement
            working**, and it is the strongest thing you can put on a row - the reader sees the
            requirement met instead of reading that it was. Set it only when the caption says that
            shot is of THIS requirement. A picture of the wrong screen beside a requirement is worse
            than no picture, so where the captions do not tell you, leave it off.

            `snippet` quotes the few lines that ARE the answer to one requirement - `{text, path,
            startLine, language}`, under every rule the `code` block follows, and **6 lines at the
            absolute most**. A snippet longer than 8 is dropped before the reader sees it, so cut to
            the lines that carry the point rather than sending the whole method.

            **At most two across the entire map**, and only where the code settles the question
            faster than the sentence does - the condition that implements the rule, the line that
            was wrong. A citation asks the reader to go and look and a sentence asks them to take it
            on trust; a snippet ends the argument in place, which is exactly why one on every row
            would end nothing. Rows where the sentence already says it get no snippet.

            **Rule on every id, including the ones you consider background.** Anything you leave out
            reaches the reader as "the analysis returned no ruling on this line" - which is a worse
            answer than any of the six above, and is not something you can make invisible by
            omitting it.

        """;

        // Conditional for the same reason section 10 is, and easy to forget BECAUSE it is only a
        // reference entry: a block kind documented but never asked for is one the run may decide to
        // produce anyway, and it would then be dropped on the way in - output paid for and thrown away.
        var coverageBlockDoc = map.Segments.Count == 0 ? "" : """
        - `coverage` - the ruling on each line of the requirement text, as `rulings`, plus the
          one-sentence summary in `text`. **Give those two and nothing else** - no ordering, no
          grouping, and never the requirement's own words. The reader's lines are joined back on by
          the dashboard, in the ticket's order, and a line you did not rule on is shown either way.
          A ruling's `snippet` follows every rule `code` does, at a sixth of the length; snippets
          spent there do not count against the document's two-to-four.

        """;

        // Which criterion a stop answers. Conditional for the same reason as the coverage entry: with
        // no requirement text there are no ids to name, and a field the run cannot fill is one it
        // fills with something made up.
        var flowAsksDoc = map.Segments.Count == 0 ? "" : """
            - `asks` - the ids of the requirement lines that **called for this stop**: 1 to 3 from
              the requirement text above. Set it on every `new`, `changed` or `removed` stop a line
              asked for, and leave it off `unchanged` ones. The reader opens a stop to find out why
              it is there, and is shown those lines in their own words beside your `explain` - so
              name the line that asked for THIS stop, not every line in the same area. A new stop no
              line asked for gets none, and that is worth a sentence in its `explain`: something was
              built that nobody wrote down.

            """.Replace("\n", "\n  ");

        // The exemption is worth stating only where there is a map to exempt. Without one it names a
        // block the run has not been told about, and every mention of a kind it cannot use is an
        // invitation to try. What the exemption buys where it DOES apply: a run counting rulings
        // against the word budget shortens the map to protect the prose, which means dropping
        // requirements to save space.
        // Examples are outside the budget for the coverage map's reason, from the other side: a run
        // counting them against the prose would drop the example to save the sentence it explains.
        var budget = map.Segments.Count == 0
            ? """
              **Length: 900-1600 words across all blocks, `example` blocks excepted.** Long enough
              to teach, short enough to finish. Examples sit outside it so that one is never cut to
              protect the prose it explains; their own limit is one per decision.
              """.Replace("\n", "\n  ")
            : """
              **Length: 900-1600 words across all blocks, the `coverage` and `example` blocks
              excepted.** Long enough to teach, short enough to finish. The rulings sit outside the
              budget on purpose: they are a table the reader scans, not prose they read, and
              shortening them to protect a word count would mean dropping requirements to save
              space. Examples sit outside it so that one is never cut to protect the prose it
              explains; their own limit is one per decision.
              """.Replace("\n", "\n  ");

        var label = string.IsNullOrWhiteSpace(cfg.LearningLabel) ? "the domain" : cfg.LearningLabel;
        var display = string.IsNullOrWhiteSpace(cfg.DisplayName) ? t.ProjectKey : cfg.DisplayName;
        var baseBranch = string.IsNullOrWhiteSpace(cfg.BaseBranch) ? "main" : cfg.BaseBranch;

        // A project of several repos is read from its workspace, which is a folder of repos rather
        // than a repo: each has its own base, git runs inside one, and a file is named with its
        // repo's folder first, which is how the reader's diff of it is fetched again
        // (TicketGitService.Repos). For a project of one every piece below is the text it always was.
        var several = cfg.IsMultiRepo;
        var rootNote = several ? " - a folder of several repos, not itself a repo" : "";
        var baseText = several ? "each repo's own, below" : baseBranch;
        var reposList = several
            ? "\n- Repos, each its own git repo:\n" + string.Join("\n", cfg.Repos.Select(m => m.ReadOnly
                  ? $"  - `{m.Dir}` - read-only for the pipeline: here for context, never changed by a ticket"
                  : $"  - `{m.Dir}` - base `{(m.BaseBranch.Length > 0 ? m.BaseBranch : "main")}`"))
              + "\n- Name every file by its path from the workspace, its repo's folder first (`" +
              cfg.Repos[0].Dir + "/src/...`), in prose and in every block that cites one."
            : "";
        var logStep = several
            ? "In each repo the ticket changed, `git -C <repo> log --oneline <its base>..HEAD` and `git -C <repo> diff <its base>...HEAD --stat`"
            : $"`git log --oneline {baseBranch}..HEAD` and `git diff {baseBranch}...HEAD --stat`";
        var conventions = several ? "Each repo's own `CLAUDE.md`, the workspace's at its root if there is one," : "This repo's `CLAUDE.md`";

        // A raw literal takes its line breaks from the source file, and core.autocrlf checks this one
        // out as CRLF - which, mixed with the "\n" joins above, made the prompt depend on the checkout.
        return ($$"""
        You are writing a TEACHING ANALYSIS of one ticket in the "{{display}}" project, for the person who
        owns this repo but did not write this change and did not watch it being written. They know the
        codebase in general and this corner of it barely. Nobody is waiting on you; nothing downstream
        consumes this. Its only job is that they finish reading knowing something about {{label}} they
        did not know before.

        ## Absolute constraints

        - **READ ONLY.** Do not modify, create or delete any file. Do not stage, commit, checkout,
          switch, stash, reset or push anything. Your Bash use is limited to reads - `git log`,
          `git diff`, `git show`, `git status`. You have no Edit or Write tool; do not work around that.
        - Do not run the app, the test suite, or any build.
        - Output nothing but the structured document. It is validated against a schema; there is no
          prose channel and no summary to write afterwards.

        ## Where things are

        - Reading here (already your working directory): {{P(readDir)}}
        - Repo root:        {{P(cfg.ProjectPath)}}{{rootNote}}
        - Branch:           {{t.Branch}}      Base: {{baseText}}{{reposList}}
        - This repo's notes store: {{P(cfg.ResolvedMemoryDir)}}  (one file per durable fact + MEMORY.md index)

        ## The ticket's record

        Everything the pipeline wrote down about this work. Treat it as evidence, not as content to
        repeat back:

        ```json
        {{record}}
        ```
        {{requirements}}
        ## What to read before writing

        1. {{logStep}}, then the
           diff itself for the files that carry the actual decision (not the ones that carry the same
           line pasted into four places).
        2. The source AROUND the change - the callers, the type it belongs to, the rule it sits inside.
           This is what separates an analysis from a diff summary. Budget most of your reading here.
        3. {{conventions}} and its notes store (`MEMORY.md` first, then whichever notes cover
           this area). Where a note already explains the area, cite it as `[[note-name]]` in your prose
           and write only the delta - do not re-explain a subsystem the reader already has a note on.

        ## The document

        {{LensDirection(lens)}}

        Use these sections, in this order, titled like this. Drop a section entirely if the ticket
        genuinely gives you nothing real for it - an absent section is honest, a padded one costs the
        reader time and teaches them to skim the next analysis:

        1. **The area** - what part of the system this lives in, in domain terms; what it is for; how a
           request or a piece of data actually flows through it. Where the ticket uses vocabulary the
           reader may not own, define it in a `glossary` block.
        2. **How it worked before** - the rule or behaviour that was in place. The section most often
           skipped and the one that makes every section after it legible. Be specific about the old
           rule, and **quote it**: one `code` block of the old lines, titled to say so ("Before, on
           main") and carrying no `startLine`, because those lines are not in the file any more.
           Where the old rule is easier to picture than to read, follow it with an `example`, and
           use the same made-up situation again in section 3 - the same input meeting the old rule
           and then the new one is the clearest before-and-after this document can give.
        3. **What changed** - the change stated as the two or three DECISIONS it really is, with the
           rest named as plumbing. Not a file-by-file walk. **One `code` block is required here** -
           the lines the decision actually turns on. If you cannot point at lines that carry it, you
           have not found the decision yet; go back to the diff before writing this section.
           **Follow each decision with an `example`** where one would help a reader picture it - a
           rule with a condition, an order, a fallback, two rules that look alike. A decision that
           is pure plumbing (a registration, a rename) gets none rather than a forced one.
        4. **The user's path** - one `userFlow` block: the route a person takes through the product to
           reach what this ticket changed, with every stop marked `new`, `changed`, `unchanged` or
           `removed`. Start far enough back that the reader recognises where they are - the unchanged
           stops are the scaffolding that makes the new ones legible. A short prose block before it
           saying what the person was trying to do is usually worth the words.

           **Skip this section entirely unless a person using the product would now do something
           differently, see something different, or reach a different outcome.** Retry logic, an
           index, a config default, a queue, a refactor behind an unchanged surface: no user flow -
           omit it, and do not stretch for one. **A flowchart of function calls is a failure of this
           section, not a substitute for it** - if your nodes are methods, you have written the
           reading path twice and taught nothing.
        5. **Why this way** - a `comparison` block over the routes that were available: what each would
           have cost, and the constraint that ruled it out. Use the ticket's comments and the diff as
           evidence. If the record genuinely shows no alternative was weighed, say so plainly in prose
           and give the constraint that forced the shape instead - do not invent a debate.
        6. **How we know it works** - what the verification was actually worth. **Do not table the
           acceptance criteria here**; section 10 already rules on every one of them line by line, and
           a second coverage table is the cross-referencing this document exists to save the reader.
           What belongs here is the part a table cannot hold: how QA went about it, what the checks
           would not have caught, and - where you drew a flow - which of its branches nothing walked.
        7. **What would catch you out** - `callout` blocks. The thing that surprises a competent reader
           is the highest-value sentence in this whole document. Include the edge case, the ordering
           constraint, the invariant that is not obvious from the code.
        8. **Where to look** - a `readingPath` block: 4-8 steps as `file:line` locations, ordered as a
           route ("start here, then here"), each with what it is and why it is the next stop.
        9. **Worth sitting with** - one `question` block. Something this change raises but does not
           answer. Not a quiz question, and not rhetorical: a real open question about the system.
        {{coverageSection}}
        ## Using the blocks

        - `prose` - explanation. Supports `**bold**`, `` `code` `` and `[[note-name]]` links into the
          notes store. Two to four sentences per block; break at every idea rather than writing a wall.
        - `keyValue` - the orientation facts worth having at a glance (entry point, owning service,
          config that governs it, where the data lands).
        - `comparison` - any real trade-off or coverage matrix. `columns` then `rows`; keep cells short
          enough to scan. Use `note` for the caveat under the table.
        - `example` - a made-up situation that shows a rule working, for the reader who understood
          the rule as stated but cannot yet picture it. `text` is the setup in one or two sentences:
          who and what exists before anything happens ("Acme Corp, EIN 12-3456789, already has an
          active 401(k) plan named Acme 401(k) Plan"). `columns` and `rows` are the cases - 2 to 4,
          each an input, what happens, and why - and `note` is the one thing the cases show that the
          rule did not say on its own.

          - **Invent the names, never the behaviour.** The people, companies and numbers are made
            up; every outcome must be what the code you read actually does with that input, and
            follow from lines you have cited or quoted. An example whose outcome the code would not
            produce teaches the reader a rule that does not exist, and looks more certain doing it
            than any sentence could.
          - **Show the edge, not the typical case.** The case worth a row is the one the rule turns
            on: two inputs that differ in one thing and come out differently, the input that reaches
            the fallback, the one that passes one check and fails the other. A row that only
            restates the happy path teaches nothing the rule did not.
          - **Keep one cast.** Introduce your made-up people and data once and reuse them in every
            example in the document, so each new example costs the reader only what is new in it.
          - Cells are short: a value or a few words. If a case needs a paragraph, it is prose.
        - `userFlow` - the user's route, as `nodes` and `edges`. **Give the graph and nothing else** -
          where a box sits, what colour it is and which way the arrows run is decided by the renderer,
          not by you. There is no layout field and adding one is not possible.

          - `kind` - `start` (where the person already is), `screen` (something they land on), `step`
            (something they do), `decision` (a fork - every edge out of one needs a `label`),
            `system` (something the product does without them; name the endpoint or the job), `end`
            (an outcome, and a refusal is an outcome worth drawing).
          - `change` - `new`, `changed`, `unchanged`, `removed`. **Be strict.** Marking every stop
            `new` throws away the only thing this diagram does that a screenshot does not.
          - `actor` - the role taking the step, in the domain's own word for it. Set it only when the
            flow genuinely crosses more than one role; on a single-actor flow, leave it off every node
            rather than repeating one name down the page.
          - `label` - **2 to 5 words. A HEADER, not a sentence.** "Confirm the replacement", not
            "The user confirms they want to replace the existing advisor". Plain text, no markdown.
            The box is deliberately small and everything you want to say about the stop goes in
            `explain`, which the reader opens with a click.
          - `explain` - 1 to 3 sentences: what actually happens here, and the thing about it a reader
            would not guess. The rule that governs the step, the state it leaves behind, what makes
            it fail. This is the payload of the whole diagram - a stop with a label and no `explain`
            is a box with a name on it. Supports `**bold**`, `` `code` `` and `[[note-name]]`.
          - `refs` - **1 to 3** `file:line` citations for what implements the stop. Same rules as a
            `readingPath` location: lines you verified, not lines you assumed. The reader gets each
            file's diff alongside your `explain` when they open the stop, so cite the file where the
            interesting part of this step actually lives - not the barrel file it is exported from.
          - `shot` - the `order` of the QA screenshot in the record that **is a picture of this
            stop**. It is drawn ON the stop, always visible, so the reader recognises the screen
            without opening anything - which makes it the most valuable field on a `screen` or `end`
            node and usually the wrong field on a `step`, `decision` or `system` one. Go through the
            captions and place every shot you can honestly place; a flow whose screens are drawn as
            empty boxes when the ticket has pictures of them has wasted the pictures. Set it only
            when the caption says that shot is of THIS stop, and never twice - one screenshot cannot
            be a picture of two different places, so if two stops both look like the same shot, it
            belongs to the one it actually shows.
          {{flowAsksDoc}}- **6 to 12 nodes.** Every edge's `from` and `to` must be an `id` you declared. A line back
            to an earlier stop is fine and often the truest part of the picture - a failed validation
            returning to the form is something the user really does.
          - **Every path ends where the user can see it ends.** A flow that stops at the write is
            missing its last stop: finish the successful path on an `end` that says what the person
            is now looking at, not on the `system` node that saved it. Drawing only the refusals as
            outcomes leaves the diagram with no picture of success.
          - **Draw the gates, do not describe them.** If reaching a stop depends on a condition, that
            condition is a `decision` with both ways out drawn - a second gate mentioned only inside
            an `explain` is a branch the reader cannot see.
        - `code` - a real snippet, quoted from the file you read. Set `path` (repo-relative),
          `startLine` (the real line the first quoted line sits on) and `language`. **20 lines
          maximum** - cut to the lines that carry the point, and use `...` on its own line for an
          elision. If it cannot be made to fit, it is a `readingPath` step instead, not a longer
          snippet. Put why it matters in `note`.

          `startLine` means **the line that code sits on in the file as it is now**, on this branch.
          Count it; do not estimate. It becomes the number printed beside the first line and the line
          the editor jumps to, so being one out is worse than being absent.

          **Code that no longer exists there gets NO `startLine`.** The "how it worked before"
          snippet is the usual case: those lines were changed or deleted, so there is no line in
          today's file that holds them, and stamping one on points the reader at whatever happens to
          be there now. Omit `startLine`, keep `path` (the file is still the right file), and set
          `title` to say what it is - "Before, on main". The snippet then renders without line
          numbers, which is the honest presentation of a quote from history.

          The reader sees your snippet with the file's own line numbers down the side and opens the
          file next to it, so **one quoted line must be one real line**:

          - DO strip the common leading indentation from the whole block. Deeply nested code is
            unreadable in a narrow column, and removing the same amount from every line keeps the
            relative shape intact.
          - **NEVER re-wrap a long line onto several.** It is the one edit that silently corrupts
            every line number after it. Long lines are fine - the snippet scrolls sideways.
          - **NEVER add a comment, marker or annotation that is not in the file.** Not `// bug here`,
            not `// <-- this`. Anything you want to say about the code goes in `note`, which sits
            directly under the snippet and is understood to be yours.
          - Never paraphrase, re-order, correct or reconstruct from memory. A snippet that does not
            match what is on disk costs you every other claim in the document.
        {{coverageBlockDoc}}- `readingPath` - the route through the code. `location` must be a real `file:line` you verified.
        - `glossary` - domain vocabulary only. Not general programming terms.
        - `callout` - `tone` is `gotcha`, `insight` or `risk`. Reserve these; three sharp ones beat eight.
          `risk` means you believe something in this change is wrong or will go wrong, and it is
          read by more than the human: each one, with every `gap` and `partial` in the coverage
          map, is handed to the pipeline's refinement phase, which must check it against the code
          and rule on it before the ticket reaches review. So a `risk` names the input or state that
          reaches the problem and the file:line where it happens. Something that merely surprises -
          an invariant, an ordering, a sharp edge the code already handles - is a `gotcha`.
        - `question` - the open question. Usually exactly one, at the end.

        ## What decides whether this is worth reading

        - **Show the code, do not just cite it.** A `file:line` reference asks the reader to go and
          find it; a snippet puts the thing you are talking about in front of them. Where a rule is
          easier to see than to describe - the condition that was wrong, the line that changed, the
          invariant that is not obvious - quote it. **Two snippets is the floor and four is the
          ceiling**, and sections 2 and 3 each owe one. The rules below are about quoting ACCURATELY,
          not about whether to quote: a document with no snippets in it has failed this test, not
          passed it safely.
        - **Concrete or nothing.** Real files, real rules, real numbers, real edge cases. Anything that
          would read the same on a different ticket should be deleted. An `example`'s made-up names
          are not an exception to this: the names are invented so the BEHAVIOUR can be concrete.
        - **Do not restate the record.** The reader can see the description, the work items and the
          implementation summary already. Your value is what you learned from the CODE that the ticket
          does not say.
        - **Say what would have caught you out** while you were reading it.
        - {{budget}}
        """).ReplaceLineEndings("\n");
    }

    // ---- small helpers ------------------------------------------------------

    /// <summary>
    /// Where to read this ticket's work from.
    /// <para>
    /// The ticket's own worktree when it still exists: its files ARE the branch's files, which is
    /// what the analysis is about. The main checkout is the fallback and is not as good — it is
    /// usually sitting on the base branch — but the diff and the history are still reachable from
    /// there, so a pruned worktree degrades the analysis rather than blocking it.
    /// </para>
    /// <para>
    /// Which code an analysis read there - its pin - is <see cref="AnalysisPin.Of"/>, which every
    /// caller asks, so the run, the gate and the staleness badge agree.
    /// </para>
    /// </summary>
    public static string ReadDir(Ticket t, ProjectConfig cfg) =>
        !string.IsNullOrWhiteSpace(t.WorktreePath) && Directory.Exists(t.WorktreePath)
            ? t.WorktreePath!
            : cfg.ProjectPath;
}
