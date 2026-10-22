using System.Text.RegularExpressions;
using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Whetstone.Services.Walkthrough;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;

namespace Whetstone.Api;

/// <param name="Status">none (never written), Pending, Ready or Failed - the latest attempt's.</param>
/// <param name="Error">Why the latest attempt failed; on a Ready deck, a warning (lines left unvoiced).</param>
/// <param name="Deck">The last good deck, kept through a retry, so a rewrite in progress still leaves one to play.</param>
public record WalkthroughDto(
    string Status, string? Error, WalkthroughDeck.Deck? Deck, double AudioSeconds, int Dropped,
    long InputTokens, long OutputTokens, long CacheReadTokens, long CacheCreationTokens,
    string? Model, int DurationMs, DateTime? GeneratedAt);

/// <summary>A Classroom analysis's narrated walkthrough, its voiced lines, and the attachments it shows.</summary>
public static class WalkthroughEndpoints
{
    private static readonly Regex ClipName = new("^[0-9a-f]{40}$", RegexOptions.Compiled);
    private static readonly FileExtensionContentTypeProvider Types = new();

    /// <summary>A walkthrough row as the player reads it, whichever document it narrates; "none" for no row.</summary>
    public static WalkthroughDto Dto(AnalysisWalkthrough? w) => w is null
        ? new WalkthroughDto("none", null, null, 0, 0, 0, 0, 0, 0, null, 0, null)
        : new WalkthroughDto(w.Status.ToString(), w.Error, WalkthroughDeck.Deserialize(w.DeckJson),
            w.AudioSeconds, w.Dropped, w.InputTokens, w.OutputTokens, w.CacheReadTokens, w.CacheCreationTokens,
            w.Model, w.DurationMs, w.GeneratedAt);

    public static void MapWalkthroughEndpoints(this WebApplication app)
    {
        app.MapGet("/api/classroom/analyses/{id:int}/walkthrough", async (int id, DashboardDbContext db) =>
        {
            if (!await db.TicketAnalyses.AnyAsync(a => a.Id == id)) return Results.NotFound();
            var w = await db.AnalysisWalkthroughs.AsNoTracking().FirstOrDefaultAsync(x => x.AnalysisId == id);
            return Results.Ok(Dto(w));
        });

        // Write or rewrite one. New analyses get theirs automatically; this is the Retry button, and
        // the way an analysis from before walkthroughs existed gets one.
        app.MapPost("/api/classroom/analyses/{id:int}/walkthrough", async (int id, DashboardDbContext db, AnalysisQueue queue) =>
        {
            var a = await db.TicketAnalyses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (a is null) return Results.NotFound();
            var (ok, message) = queue.EnqueueWalkthrough(a.TicketId, id);
            return ok ? Results.Accepted(value: new { message }) : Results.Conflict(new { message });
        });

        // Whether this machine can voice a walkthrough at all, asked live so the Classroom can say so
        // before anyone waits on a deck that will come back captions-only. Reads the disk, runs nothing.
        app.MapGet("/api/narrator", (Narrator narrator, DashboardPaths paths) =>
            Results.Ok(new { unavailable = narrator.Unavailable(), dir = paths.NarratorDir }));

        // A voiced line. Named by content hash, so it never changes once written and can be cached hard.
        app.MapGet("/api/walkthrough-clips/{clip}.wav", (string clip, Narrator narrator, HttpContext http) =>
        {
            if (!ClipName.IsMatch(clip)) return Results.NotFound();
            var path = narrator.ClipPath(clip);
            if (!File.Exists(path)) return Results.NotFound();
            http.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            return Results.File(path, "audio/wav", enableRangeProcessing: true);
        });

        // A linked issue's attachment, as cached on disk by IssueAttachmentStore. Read-only, and only
        // from inside that ticket's own folder.
        app.MapGet("/api/tickets/{id:int}/issue-attachments/{**path}", (int id, string path, DashboardPaths paths) =>
        {
            var root = Path.GetFullPath(paths.IssueAttachmentTicketDir(id));
            var full = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                return Results.NotFound();
            return Results.File(full, Types.TryGetContentType(full, out var type) ? type : "application/octet-stream");
        });
    }
}
