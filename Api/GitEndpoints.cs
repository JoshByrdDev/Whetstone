using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Whetstone.Services.Tracker;

namespace Whetstone.Api;

/// <summary>What the PR button was asked to raise. Both fields default to the ticket's own words.</summary>
public record CreatePrRequest(string? Title, string? Description);

/// <summary>The changeset Team Explorer reported for a ticket's check-in.</summary>
public record RecordCheckinRequest(int? Changeset);

/// <summary>The files the human was shown as the server's, and the commit message they settled on.</summary>
public record TfvcSyncRequest(string[]? Paths, string? Message);

/// <summary>
/// The human's git actions on one ticket: check its branch out here, and raise its PR.
///
/// <para>
/// Ticket-scoped rather than project-scoped on purpose. The board never asks "check out branch X" —
/// it asks "put me on ticket 8025", and the branch is whatever the ticket records. Resolving that
/// here means the browser never gets to name a branch, so a bad ticket id is a 404 rather than a
/// checkout of something arbitrary.
/// </para>
/// </summary>
public static class GitEndpoints
{
    public static void MapGitEndpoints(this WebApplication app)
    {
        // 409 on refusal, because the only refusal that matters — a dirty working tree — is a
        // conflict with the user's own uncommitted work, and the UI has to say exactly that.
        app.MapPost("/api/tickets/{id:int}/git/checkout", async (
            int id, TicketService tickets, TicketGitService git, ProjectRegistry registry, CancellationToken ct) =>
        {
            var t = await tickets.GetAsync(id);
            if (t is null) return Results.NotFound();

            // On a TFVC project the hub IS the workspace, so putting it on a ticket branch shows TFVC
            // the whole branch as pending edits. The board hides the button there; this is the rule.
            var cfg = registry.GetProjectConfig(t.ProjectKey);
            if (cfg.IsTfvc)
                return Results.Json(new CheckoutResult(false,
                    $"{cfg.DisplayName}'s hub is also its TFVC workspace, so this will not check a ticket branch out there - " +
                    "TFVC would see the whole branch as pending edits. Read the diff in the check-in dialog, or open the agent's worktree.",
                    BranchOf(t), cfg.ProjectPath, false, null, false, Array.Empty<GitChange>()), statusCode: 409);

            var result = await git.CheckoutAsync(t.ProjectKey, BranchOf(t), ct);
            return result.Ok ? Results.Ok(result) : Results.Json(result, statusCode: 409);
        });

        // What the dialog opens on: local reads only, so it is on screen while the base check below
        // is still talking to origin. The two are separate GETs rather than one streamed response
        // because they answer separate questions — this one is "what is on this branch", and it is
        // the same answer whether or not the remote can be reached.
        app.MapGet("/api/tickets/{id:int}/git/pr", async (
            int id, TicketService tickets, TicketGitService git, LinkedIssueResolver resolver,
            IssueTrackerFactory trackers, ProjectRegistry registry, CancellationToken ct) =>
        {
            var t = await tickets.GetAsync(id);
            if (t is null) return Results.NotFound();

            var r = await resolver.ResolveAsync(t);
            var pre = await git.LocalPreflightAsync(t.ProjectKey, BranchOf(t), ct);
            var issueRef = IssueRefFor(trackers, t);
            var cfg = registry.GetProjectConfig(t.ProjectKey);
            return Results.Ok(new
            {
                // Which dialog this diff belongs in - "git" raises a PR, "tfvc" checks in.
                repoType = cfg.RepoType,
                // A git project that delivers by merging into its base: the dialog's last button merges.
                mergeInsteadOfPr = cfg.MergeInsteadOfPr && !cfg.IsTfvc,
                preflight = pre,
                suggestedTitle = SuggestTitle(t, r, issueRef),
                suggestedDescription = PrDescription.Compose(t, pre.CommitBody, r.Description, issueRef),
            });
        });

        // The other half, and the slow one: it fetches from origin, so it is seconds rather than
        // milliseconds. Every gate on raising the PR reads these numbers, so the dialog must wait
        // for this before it lets the button through — it just no longer waits to draw.
        app.MapGet("/api/tickets/{id:int}/git/pr/base", async (
            int id, TicketService tickets, TicketGitService git, CancellationToken ct) =>
        {
            var t = await tickets.GetAsync(id);
            if (t is null) return Results.NotFound();

            return Results.Ok(await git.BaseCheckAsync(t.ProjectKey, BranchOf(t), ct));
        });

        // 409 again on refusal, and a conflicted rebase is a refusal: the body carries the files that
        // clashed, and that list is the answer the user came for. Nothing moved either way.
        app.MapPost("/api/tickets/{id:int}/git/rebase", async (
            int id, TicketService tickets, TicketGitService git, ProjectRegistry registry, CancellationToken ct) =>
        {
            var t = await tickets.GetAsync(id);
            if (t is null) return Results.NotFound();

            if ((RefuseWriteOnReview(t, "rebase it") ?? RefuseOnTfvc(registry, t, "rebase it against a remote")) is { } no)
                return Results.Json(new { ok = false, error = no }, statusCode: 409);

            var result = await git.RebaseAsync(t.ProjectKey, BranchOf(t), ct);
            return result.Ok ? Results.Ok(result) : Results.Json(result, statusCode: 409);
        });

        // Separate from creating the PR on purpose — see TicketGitService.ForcePushAsync. The PR
        // endpoint must never reach for this when its own push is rejected.
        app.MapPost("/api/tickets/{id:int}/git/force-push", async (
            int id, TicketService tickets, TicketGitService git, ProjectRegistry registry, CancellationToken ct) =>
        {
            var t = await tickets.GetAsync(id);
            if (t is null) return Results.NotFound();

            if ((RefuseWriteOnReview(t, "force push it") ?? RefuseOnTfvc(registry, t, "force push it")) is { } no)
                return Results.Json(new { ok = false, error = no }, statusCode: 409);

            var result = await git.ForcePushAsync(t.ProjectKey, BranchOf(t), ct);
            return result.Ok ? Results.Ok(result) : Results.Json(result, statusCode: 409);
        });

        app.MapPost("/api/tickets/{id:int}/git/pr", async (
            int id, CreatePrRequest? req, TicketService tickets, TicketGitService git,
            LinkedIssueResolver resolver, IssueTrackerFactory trackers, ProjectRegistry registry, CancellationToken ct) =>
        {
            var t = await tickets.GetAsync(id);
            if (t is null) return Results.NotFound();

            if ((RefuseWriteOnReview(t, "push it or raise a pull request for it")
                 ?? RefuseOnTfvc(registry, t, "push it or raise a pull request for it")) is { } no)
                return Results.Json(new { ok = false, error = no }, statusCode: 409);

            var title = string.IsNullOrWhiteSpace(req?.Title)
                ? SuggestTitle(t, await resolver.ResolveAsync(t), IssueRefFor(trackers, t))
                : req!.Title!.Trim();

            var result = await git.CreateAsync(t.ProjectKey, BranchOf(t), title, req?.Description, ct);
            return result.Ok ? Results.Ok(result) : Results.BadRequest(result);
        });

        // The PR's counterpart on a project that merges to its base instead (mergeInsteadOfPr). 409 on
        // refusal like rebase: a conflicted merge is rolled back and the body names the files.
        app.MapPost("/api/tickets/{id:int}/git/merge", async (
            int id, CreatePrRequest? req, TicketService tickets, TicketGitService git, ProjectRegistry registry,
            CancellationToken ct) =>
        {
            var t = await tickets.GetAsync(id);
            if (t is null) return Results.NotFound();

            if ((RefuseWriteOnReview(t, "merge it")
                 ?? RefuseOnTfvc(registry, t, "merge it into its base")) is { } no)
                return Results.Json(new { ok = false, error = no }, statusCode: 409);

            var cfg = registry.GetProjectConfig(t.ProjectKey);
            if (!cfg.MergeInsteadOfPr)
                return Results.Json(new
                {
                    ok = false,
                    error = $"{cfg.DisplayName} delivers by pull request. Turn on \"Merge instead of pull request\" in its settings to merge from here.",
                }, statusCode: 409);

            var result = await git.MergeAsync(t.ProjectKey, BranchOf(t), req?.Title, req?.Description, ct);
            if (!result.Ok) return Results.Json(result, statusCode: 409);

            // The ticket follows the code, exactly as a TFVC check-in does: only NeedsReview can be
            // signed off to Done, and anything else is said rather than forced.
            if (t.Phase == TicketPhase.Done)
                result = result with { PhaseNote = "The ticket was already Done." };
            else if (PhaseMachine.CanTransition(t.Phase, TicketPhase.Done, t.Track))
            {
                try
                {
                    await tickets.TransitionAsync(id, TicketPhase.Done,
                        $"Merged into {result.BaseBranch} at {result.MergeSha?[..Math.Min(7, result.MergeSha.Length)]}",
                        TransitionActor.Human);
                    result = result with { MovedToDone = true };
                }
                catch (InvalidTransitionException ex) { result = result with { PhaseNote = ex.Message }; }
            }
            else
                result = result with
                {
                    PhaseNote = $"The ticket was left in {t.Phase}: only a ticket at NeedsReview can be signed off to Done, so move it by hand.",
                };

            return Results.Ok(result);
        });

        // ---- TFVC check-in --------------------------------------------------
        // The PR flow's counterpart on a project whose real source control is TFVC; see
        // TicketGitService.Checkin. Stage, abort and record answer 409 on refusal like the rest.

        app.MapGet("/api/tickets/{id:int}/git/checkin", async (
            int id, TicketService tickets, TicketGitService git, CancellationToken ct) =>
        {
            var t = await tickets.GetAsync(id);
            if (t is null) return Results.NotFound();
            return Results.Ok(await git.CheckinStatusAsync(t.ProjectKey, BranchOf(t), ct));
        });

        app.MapPost("/api/tickets/{id:int}/git/checkin/stage", async (
            int id, TicketService tickets, TicketGitService git, CancellationToken ct) =>
        {
            var t = await tickets.GetAsync(id);
            if (t is null) return Results.NotFound();
            if (RefuseWriteOnReview(t, "merge it for a check-in") is { } no)
                return Results.Json(new { ok = false, error = no }, statusCode: 409);

            var result = await git.StageCheckinAsync(t.ProjectKey, BranchOf(t), ct);
            return result.Ok ? Results.Ok(result) : Results.Json(result, statusCode: 409);
        });

        app.MapPost("/api/tickets/{id:int}/git/checkin/abort", async (
            int id, TicketService tickets, TicketGitService git, CancellationToken ct) =>
        {
            var t = await tickets.GetAsync(id);
            if (t is null) return Results.NotFound();
            if (RefuseWriteOnReview(t, "abort a merge for it") is { } no)
                return Results.Json(new { ok = false, error = no }, statusCode: 409);

            var result = await git.AbortCheckinAsync(t.ProjectKey, BranchOf(t), ct);
            return result.Ok ? Results.Ok(result) : Results.Json(result, statusCode: 409);
        });

        app.MapPost("/api/tickets/{id:int}/git/checkin/record", async (
            int id, RecordCheckinRequest? req, TicketService tickets, TicketGitService git,
            LinkedIssueResolver resolver, IssueTrackerFactory trackers, CancellationToken ct) =>
        {
            var t = await tickets.GetAsync(id);
            if (t is null) return Results.NotFound();
            if (RefuseWriteOnReview(t, "record a check-in for it") is { } no)
                return Results.Json(new { ok = false, error = no }, statusCode: 409);
            if (req?.Changeset is not > 0)
                return Results.Json(new { ok = false, error = "Enter the changeset number Team Explorer reported." }, statusCode: 400);

            var changeset = req.Changeset.Value;
            var title = SuggestTitle(t, await resolver.ResolveAsync(t), IssueRefFor(trackers, t));
            var result = await git.RecordCheckinAsync(t.ProjectKey, BranchOf(t), changeset, title, ct);
            if (!result.Ok) return Results.Json(result, statusCode: 409);

            // The ticket follows the code. Only NeedsReview can be signed off to Done, and that is
            // where a finished ticket waits; anywhere else is said rather than forced, because the
            // commit has already happened and the phase is the one part a person can fix by hand.
            if (t.Phase == TicketPhase.Done)
                result = result with { PhaseNote = "The ticket was already Done." };
            else if (PhaseMachine.CanTransition(t.Phase, TicketPhase.Done, t.Track))
            {
                try
                {
                    await tickets.TransitionAsync(id, TicketPhase.Done,
                        $"Checked in to TFVC as changeset {changeset}", TransitionActor.Human);
                    result = result with { MovedToDone = true };
                }
                catch (InvalidTransitionException ex) { result = result with { PhaseNote = ex.Message }; }
            }
            else
                result = result with
                {
                    PhaseNote = $"The ticket was left in {t.Phase}: only a ticket at NeedsReview can be signed off to Done, so move it by hand.",
                };

            return Results.Ok(result);
        });

        // ---- Sync with TFVC -------------------------------------------------
        // Project-scoped rather than ticket-scoped: a Get Latest lands in the hub whatever ticket is
        // on the board. See TicketGitService.Sync. The POST names the files it was shown and is
        // refused (409) unless they are still exactly what would be committed.

        app.MapGet("/api/projects/{key}/tfvc/sync", async (
            string key, ProjectRegistry registry, TicketGitService git, CancellationToken ct) =>
        {
            if (!registry.IsEnabled(key)) return Results.NotFound(new { error = $"Unknown project '{key}'." });
            return Results.Ok(await git.TfvcSyncPreviewAsync(key, ct));
        });

        app.MapPost("/api/projects/{key}/tfvc/sync", async (
            string key, TfvcSyncRequest? req, ProjectRegistry registry, TicketGitService git, CancellationToken ct) =>
        {
            if (!registry.IsEnabled(key)) return Results.NotFound(new { error = $"Unknown project '{key}'." });
            var result = await git.TfvcSyncAsync(key, req?.Paths ?? Array.Empty<string>(), req?.Message, ct);
            return result.Ok ? Results.Ok(result) : Results.Json(result, statusCode: 409);
        });
    }

    /// <summary>
    /// Why a remote-facing git action must not run on this ticket's project, or null when it may.
    /// A TFVC project has no remote to push to and delivers by check-in; its dialog never offers
    /// these, and this is what keeps an old bundle or a hand-made request from finding out otherwise.
    /// </summary>
    private static string? RefuseOnTfvc(ProjectRegistry registry, Models.Ticket t, string action)
    {
        var cfg = registry.GetProjectConfig(t.ProjectKey);
        return cfg.IsTfvc
            ? $"{cfg.DisplayName} delivers by TFVC check-in, so this dashboard will not {action}. Use the check-in dialog."
            : null;
    }

    /// <summary>
    /// The branch this ticket's git actions operate on: its own on the Delivery track, and the
    /// branch under review on the Review track — which is recorded on the ReviewTarget rather than
    /// in <see cref="Models.Ticket.Branch"/>, because a review ticket never owns a branch.
    /// </summary>
    private static string BranchOf(Models.Ticket t) =>
        t.Track == Models.TicketTrack.Review
            ? t.ReviewTarget?.SourceBranch ?? ""
            : t.Branch ?? "";

    /// <summary>
    /// Why this git action must not run on this ticket, or null when it may.
    /// <para>
    /// The three operations that WRITE to origin — raising the pull request, rebasing, force-pushing
    /// — are refused outright on a review ticket, and this is deliberately a server-side refusal
    /// rather than a hidden button. The branch belongs to someone else: a force push to it destroys
    /// work this machine never had a copy of, and no amount of UI care makes an endpoint that would
    /// do it safe to leave reachable. Reading is untouched — checkout, the ranked diff and the base
    /// check are exactly what a reviewer came for.
    /// </para>
    /// </summary>
    private static string? RefuseWriteOnReview(Models.Ticket t, string action) =>
        t.Track == Models.TicketTrack.Review
            ? $"This is a peer review ticket: the branch belongs to someone else, so this dashboard will not {action}. "
            + "Read the diff, stage your feedback, and send it from the review panel."
            : null;

    /// <summary>How this ticket's issue should be cited in prose that leaves this machine, or null
    /// when there is no issue to cite.</summary>
    private static string? IssueRefFor(IssueTrackerFactory trackers, Models.Ticket t)
        => string.IsNullOrWhiteSpace(t.IssueKey) ? null : trackers.For(t.ProjectKey).CommitRef(t.IssueKey);

    /// <summary>
    /// The PR's first draft title: <c>PROJ-123: what it is</c> — the issue's reference, then its
    /// own summary, which is the shape every branch on these repos is already named after.
    /// <para>
    /// The title has to come through the <see cref="LinkedIssueResolver"/> rather than off the
    /// ticket, because a linked ticket deliberately stores none: the tracker owns it and it is
    /// resolved live (see <see cref="Models.Ticket.Title"/>). Reading the empty local field was
    /// what put "Ticket #28" on real pull requests — our own row id, which means nothing to anyone
    /// outside this dashboard and is nobody else's business. Nothing here may fall back to it.
    /// </para>
    /// <para>
    /// When the tracker cannot be reached the resolver hands back the key itself as the title, so
    /// the draft is the bare reference rather than a stuttered "PROJ-123: PROJ-123". Thin, but
    /// true, and the dialog lets it be edited — which is what the whole draft is for.
    /// </para>
    /// </summary>
    /// <param name="issueRef">How this project's tracker wants its issue cited in a subject line -
    /// <c>PROJ-123</c> on Jira, <c>AB#1234</c> on Azure DevOps, where that exact form is what
    /// makes the pull request link back to the work item at all. Null falls back to the raw key,
    /// which is what an unlinked ticket and the tests both want.</param>
    public static string SuggestTitle(Models.Ticket t, LinkedIssueResolver.Resolved resolved, string? issueRef = null)
    {
        var key = t.IssueKey?.Trim() ?? "";
        var reference = string.IsNullOrWhiteSpace(issueRef) ? key : issueRef.Trim();
        var title = resolved.Title?.Trim() ?? "";

        if (key.Length == 0)
            // A local ticket always carries its own title; the branch is the last resort, never the id.
            return title.Length > 0 ? title : (t.Branch?.Trim() is { Length: > 0 } b ? b : "Pull request");

        return title.Length == 0 || title == key ? reference : $"{reference}: {title}";
    }
}
