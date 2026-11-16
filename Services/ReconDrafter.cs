using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Whetstone.Models;
using Whetstone.Persistence;

namespace Whetstone.Services;

/// <summary>
/// Drafted directions (docs/recon-tab-plan.md, "Drafted directions"): for a Recon gap or a changed
/// area, one read-only agent run writes two or three directions as jobs - a person, signed in as
/// one of the area's roles, and what they came to get done - for the owner to pick from, edit and
/// file. The owner asks for it from the Scout form; nothing here files anything.
/// <para>
/// <b>What the drafter is told</b> is what the board already knows, and only that: the area's name,
/// its pages, the owner's <c>about</c> and the roles it is used as, which cells nobody has scouted,
/// every past direction on the project with what it yielded, the directions out being scouted now,
/// and the taste file. Not the code, the specs or the plans - the same rule Chart keeps, for the
/// same reason: a direction drafted from what is built or planned is written as a feature, and the
/// pilot found that one written as a job finds more. It runs in an empty scratch folder, so there is
/// nothing to read even if it tried.
/// </para>
/// <para>
/// <b>What comes back is checked, not trusted</b> (<see cref="Check"/>): a candidate signed in as a
/// role the area does not list is dropped, as is one that repeats a past or open direction, and a
/// draft with nothing left is refused rather than stored. A draft is kept (<see cref="ReconDraft"/>)
/// so the form reopens on it, and a discovery filed from one records it
/// (<see cref="Ticket.ReconDraftId"/>), so Recon's yield can say whether drafting pays.
/// </para>
/// <para>
/// An agent run: refused on a QA copy, at the door (<see cref="QaIsolation"/>'s table) and here.
/// It takes Chart's agent role - the closest kind of work - so it runs on whatever provider and
/// model Chart is set to.
/// </para>
/// </summary>
public sealed class ReconDrafter
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(6);

    /// <summary>At most this many past directions are shown it, best-yielding first.</summary>
    public const int PastDirections = 30;

    /// <summary>Which drafts are running, by project and key, so a second press does not start a second run.</summary>
    private static readonly ConcurrentDictionary<string, byte> Running = new();

    private readonly DashboardDbContext _db;
    private readonly ReconService _recon;
    private readonly ProjectRegistry _registry;
    private readonly AnalyticsService _marts;
    private readonly DiscoveryTaste _taste;
    private readonly AgentProviderFactory _providers;
    private readonly ClaudeUsageService _usage;
    private readonly DashboardPaths _paths;
    private readonly QaIsolation _isolation;
    private readonly ILogger<ReconDrafter> _log;

    public ReconDrafter(DashboardDbContext db, ReconService recon, ProjectRegistry registry, AnalyticsService marts,
        DiscoveryTaste taste, AgentProviderFactory providers, ClaudeUsageService usage, DashboardPaths paths,
        QaIsolation isolation, ILogger<ReconDrafter> log)
    {
        _db = db; _recon = recon; _registry = registry; _marts = marts; _taste = taste;
        _providers = providers; _usage = usage; _paths = paths; _isolation = isolation; _log = log;
    }

    /// <summary>One drafted direction.</summary>
    /// <param name="Direction">What goes in the form: who they are and what they came to do, as a job.</param>
    /// <param name="SignedInAs">One of the area's roles, word for word.</param>
    /// <param name="Width"><c>phone</c>, <c>desk</c> or <c>both</c>.</param>
    /// <param name="Why">One sentence: what this would find that past directions have not looked for.</param>
    public sealed record Candidate(string Direction, string SignedInAs, string Width, string Why);

    /// <summary>A stored draft, as the form reads it.</summary>
    public sealed record DraftView(int Id, string Key, string Area, IReadOnlyList<Candidate> Candidates, string? Question,
        DateTime CreatedAt, string? Model, int DurationMs);

    public static DraftView View(ReconDraft d) =>
        new(d.Id, d.SuggestionKey, d.Area, ReadCandidates(d.CandidatesJson), d.Question, d.CreatedAt, d.Model, d.DurationMs);

    public static IReadOnlyList<Candidate> ReadCandidates(string json)
    {
        try { return JsonSerializer.Deserialize<List<Candidate>>(json, Wire) ?? new(); }
        catch (JsonException) { return Array.Empty<Candidate>(); }
    }

    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    /// <summary>The last draft for a suggestion, or null when none was asked for.</summary>
    public async Task<DraftView?> LatestAsync(string project, string? key, CancellationToken ct = default)
    {
        var k = DraftableKey(key);
        var d = await _db.ReconDrafts.AsNoTracking()
            .Where(x => x.ProjectKey == project && x.SuggestionKey == k)
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .FirstOrDefaultAsync(ct);
        return d is null ? null : View(d);
    }

    /// <summary>
    /// Which keys can be drafted for: a gap (an area's, or one cell's) and a changed area. A rerun is
    /// a past direction offered word for word - drafting one would be a different direction, which is
    /// what a gap or changed row is for.
    /// </summary>
    public static string DraftableKey(string? key)
    {
        var k = ReconKey.Clean(key) ?? throw new ReconService.Refused("Which suggestion? A key is needed.");
        if (ReconKey.Problem(k) is { } why) throw new ReconService.Refused(why);
        if (!k.StartsWith("gap:", StringComparison.Ordinal) && !k.StartsWith("changed:", StringComparison.Ordinal))
            throw new ReconService.Refused("Only a gap or a changed area is drafted for: a rerun offers its past direction word for word.");
        return k;
    }

    // ---- what it is told --------------------------------------------------------------------

    /// <summary>A past direction on the project and what it yielded, as the yield mart has it.</summary>
    public sealed record Past(string Direction, int Discoveries, int Shown, int Approved, int NewToMe,
        int RejectedGeneric, int RejectedAlreadyDecided, int RejectedWrong);

    /// <summary>Everything the prompt is built from, gathered once; built by tests directly.</summary>
    public sealed record Brief(
        string Product, string Area, string About, IReadOnlyList<string> Pages, IReadOnlyList<string> Roles,
        bool RolesAreTheAreas, string Kind, IReadOnlyList<string> Gaps, IReadOnlyList<string> Reasons,
        IReadOnlyList<Past> PastDirections, IReadOnlyList<string> InFlight, string Taste);

    public async Task<Brief> BriefAsync(string project, string key, CancellationToken ct = default)
    {
        if (!_registry.IsEnabled(project)) throw new ReconService.Refused($"No enabled project named {project}.", notFound: true);
        var cfg = _registry.GetProjectConfig(project);
        if (!cfg.Discovery.Present) throw new ReconService.Refused($"{project} has no discovery block, so it has no Recon.", notFound: true);
        if (ReconService.CannotFile(cfg) is { } cannot) throw new ReconService.Refused(cannot);

        var (areaName, cell) = Parse(key);
        var area = cfg.Discovery.Areas.FirstOrDefault(a => string.Equals(a.Name, areaName, StringComparison.Ordinal))
            ?? throw new ReconService.Refused($"{areaName} is not one of {project}'s areas.");
        var roles = cfg.Discovery.RolesOf(area);

        // The suggestion as the page has it: its empty cells and why it is on the list.
        var read = await _recon.SuggestionsAsync(project, DateTime.UtcNow, ct);
        var rowKey = cell is null ? key : $"gap:{areaName}";
        var row = read.Suggestions.FirstOrDefault(r => r.GetValueOrDefault("suggestionKey") as string == rowKey);
        var gaps = new List<string>();
        if (cell is { } c) gaps.Add(CellPhrase(c.Role, c.Width));
        else if (row?.GetValueOrDefault("cells") is JsonElement { ValueKind: JsonValueKind.Array } cells)
            foreach (var x in cells.EnumerateArray())
                gaps.Add(CellPhrase(Str(x, "role"), Str(x, "width")));
        var reasons = row?.GetValueOrDefault("reasons") is JsonElement { ValueKind: JsonValueKind.Array } rs
            ? rs.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.String).Select(r => r.GetString()!).ToList()
            : new List<string>();

        // Every past direction on the project, best first, as one line each with what it yielded.
        var past = _recon.Yield(project, 0).Directions
            .Select(r => new Past(
                r.GetValueOrDefault("latestDirection") as string ?? "",
                Int(r, "discoveries"), Int(r, "shown"), Int(r, "approved"), Int(r, "newToMe"),
                Int(r, "rejectedGeneric"), Int(r, "rejectedAlreadyDecided"), Int(r, "rejectedWrong")))
            .Where(p => p.Direction.Trim().Length > 0)
            .Take(PastDirections).ToList();

        var inFlight = await _db.Tickets.AsNoTracking()
            .Where(t => t.ProjectKey == project && t.Track == TicketTrack.Discovery && t.Phase != TicketPhase.Done && t.Description != null)
            .OrderBy(t => t.Id).Select(t => t.Description!).ToListAsync(ct);

        var taste = (await _taste.StatusAsync(project)).Text;

        return new Brief(
            string.IsNullOrWhiteSpace(cfg.DisplayName) ? project : cfg.DisplayName,
            area.Name, area.About, area.Pages, roles, area.Roles.Length > 0,
            key.StartsWith("changed:", StringComparison.Ordinal) ? "changed" : "gap",
            gaps, reasons, past, inFlight, taste);
    }

    /// <summary>The area a gap or changed key is about, or null for a rerun (or no key).</summary>
    public static string? AreaOf(string? key) =>
        key is null || key.StartsWith("rerun:", StringComparison.Ordinal) || !key.Contains(':') ? null : Parse(key).Area;

    private static (string Area, (string Role, string Width)? Cell) Parse(string key)
    {
        var body = key[(key.IndexOf(':') + 1)..];
        if (key.StartsWith("gap:", StringComparison.Ordinal))
        {
            // gap:<area>:<role>:<width> - the area may itself hold a colon, so read from the right.
            var parts = body.Split(':');
            if (parts.Length >= 3 && parts[^1] is "phone" or "desk")
                return (string.Join(':', parts[..^2]), (parts[^2], parts[^1]));
        }
        return (body, null);
    }

    public static string CellPhrase(string role, string width) =>
        (role == "any role" ? "as anyone" : $"as {role}") + (width == "phone" ? " on a phone" : width == "desk" ? " at a desk" : "");

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Int(Dictionary<string, object?> r, string col) =>
        r.GetValueOrDefault(col) is { } v ? Convert.ToInt32(v) : 0;

    // ---- the prompt and the answer ------------------------------------------------------------

    public const string JsonSchema = """
    {
      "type": "object",
      "required": ["candidates", "question"],
      "additionalProperties": false,
      "properties": {
        "candidates": {
          "type": "array",
          "minItems": 1,
          "maxItems": 3,
          "items": {
            "type": "object",
            "required": ["direction", "signedInAs", "width", "why"],
            "additionalProperties": false,
            "properties": {
              "direction": { "type": "string" },
              "signedInAs": { "type": "string" },
              "width": { "type": "string", "enum": ["phone", "desk", "both"] },
              "why": { "type": "string" }
            }
          }
        },
        "question": { "type": ["string", "null"] }
      }
    }
    """;

    public static string Prompt(Brief b)
    {
        var s = new StringBuilder();
        s.AppendLine($"You are choosing where to point the next scout on {b.Product}.");
        s.AppendLine();
        s.AppendLine("A scout is an agent with a browser and nothing else. It is handed a direction - who to be, and what they came to do - and uses this machine's copy of the product as that person, on its showcase data. What it notices goes to the owner as proposals. Another agent (Chart) turns your direction into a persona and three to five jobs before the scout starts, so you write the direction, not the route.");
        s.AppendLine();
        s.AppendLine("Write two or three candidate directions for the area below. The owner picks one, edits it if they like, and files it.");
        s.AppendLine();
        s.AppendLine("## What makes a direction pay");
        s.AppendLine("- **A job, never a feature.** \"Get Saturday's bookings sorted before the shop opens, on your phone in the car park\" - not \"check the bookings page\". A pilot found the wording of a direction steers what a scout finds more than anything else, and a job finds more than a feature.");
        s.AppendLine("- **A particular person.** Where they are, what they want, what they already know, what is at stake for them today. Specific enough to play, short enough to read in one breath: one to three sentences.");
        s.AppendLine("- **Somewhere nobody has looked.** Aim at the cells nobody has scouted, and away from what past directions already covered. Two candidates that would walk the same path are one candidate.");
        s.AppendLine("- **Learn from the record.** A past direction with approvals and \"new to me\" proposals is the kind that pays here; one whose proposals were rejected as generic or already decided is the kind that does not. Do not copy a past direction - the owner can rerun that from the page.");
        s.AppendLine("- **Nothing you would have to invent.** You know the area only by its name, its pages and the owner's words about it. Do not describe screens, buttons or features you have not been told exist; describe the person and their reason for coming. Chart and the scout find the screens.");
        s.AppendLine();
        s.AppendLine("## The area");
        s.AppendLine($"**{b.Area}**");
        s.AppendLine(b.About.Length > 0
            ? $"The owner's words: {b.About}"
            : "The owner has not described this area yet - you have only its name and its pages. Draft from those, and say in `question` what one or two sentences about it would let you draft better.");
        if (b.Pages.Count > 0) s.AppendLine($"Its pages (URL patterns): {string.Join(", ", b.Pages)}");
        s.AppendLine(b.Roles.Count == 0
            ? "The project lists no roles: say who they are in the product's own words (signed out, a member, a business owner...), as `signedInAs`."
            : $"{(b.RolesAreTheAreas ? "It is used as" : "The project's roles (the area names none of its own)")}: {string.Join(", ", b.Roles.Select(r => $"\"{r}\""))}. `signedInAs` is one of those, word for word - coverage is counted by that word.");
        s.AppendLine();
        if (b.Kind == "changed")
        {
            s.AppendLine("## Why now");
            s.AppendLine("Its code has changed since it was last scouted, and nothing the past directions found here was approved. A new kind of person or job is wanted, not the old ones again.");
        }
        else
        {
            s.AppendLine("## The gap");
            s.AppendLine(b.Gaps.Count > 0
                ? $"Nobody has used it {string.Join("; ", b.Gaps)}. Each candidate covers one of these - between them, cover different ones where you can."
                : "Nobody has scouted it as the roles above.");
        }
        if (b.Reasons.Count > 0) s.AppendLine($"Why it is suggested: {string.Join("; ", b.Reasons)}.");
        s.AppendLine();
        s.AppendLine("## Past directions on this project, with what they yielded");
        if (b.PastDirections.Count == 0) s.AppendLine("None yet: this is among the first scouts.");
        foreach (var p in b.PastDirections)
        {
            var rejected = new List<string>();
            if (p.RejectedGeneric > 0) rejected.Add($"{p.RejectedGeneric} rejected as generic");
            if (p.RejectedAlreadyDecided > 0) rejected.Add($"{p.RejectedAlreadyDecided} already decided");
            if (p.RejectedWrong > 0) rejected.Add($"{p.RejectedWrong} wrong");
            s.AppendLine($"- \"{OneLine(p.Direction)}\" - {p.Discoveries} discover{(p.Discoveries == 1 ? "y" : "ies")}, {p.Shown} shown, {p.Approved} approved, {p.NewToMe} new to me" +
                (rejected.Count > 0 ? $", {string.Join(", ", rejected)}" : ""));
        }
        s.AppendLine();
        if (b.InFlight.Count > 0)
        {
            s.AppendLine("## Being scouted now - do not repeat these");
            foreach (var d in b.InFlight) s.AppendLine($"- \"{OneLine(d)}\"");
            s.AppendLine();
        }
        if (b.Taste.Trim().Length > 0)
        {
            s.AppendLine("## What the owner wants proposed (the taste file)");
            s.AppendLine("Aim at the kind of finding this describes.");
            s.AppendLine();
            s.AppendLine(b.Taste.Trim());
            s.AppendLine();
        }
        s.AppendLine("## Your answer");
        s.AppendLine("- `candidates`: two or three, best first. Each: `direction` (what the owner files), `signedInAs`, `width` (`phone`, `desk`, or `both` when the job spans them), and `why` - one sentence on what this would find that the record says nobody has looked for.");
        s.AppendLine("- `question`: null when you had enough to go on. Otherwise one sentence for the owner: what to add to this area's description (its `about`) so the next draft needs no guessing.");
        s.AppendLine();
        s.AppendLine("Read no files and run nothing: everything you are given is above. Answer in the schema.");
        return s.ToString();
    }

    private static string OneLine(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// The candidates worth showing, from what the run answered: each with a direction, signed in as
    /// one of the area's roles (respelled as the project spells it), at a width it knows, and not a
    /// past or open direction again. Null with the reason when nothing is left.
    /// </summary>
    public static (IReadOnlyList<Candidate>? Kept, string? Question, string? Error) Check(JsonElement answer, Brief b)
    {
        var raw = new List<Candidate>();
        if (answer.TryGetProperty("candidates", out var cs) && cs.ValueKind == JsonValueKind.Array)
            foreach (var c in cs.EnumerateArray())
                if (c.ValueKind == JsonValueKind.Object)
                    raw.Add(new Candidate(Str(c, "direction").Trim(), Str(c, "signedInAs").Trim(), Str(c, "width").Trim(), Str(c, "why").Trim()));
        var question = answer.TryGetProperty("question", out var q) && q.ValueKind == JsonValueKind.String && q.GetString()!.Trim() is { Length: > 0 } qs ? qs : null;

        var seen = b.PastDirections.Select(p => Norm(p.Direction)).Concat(b.InFlight.Select(Norm)).ToHashSet(StringComparer.Ordinal);
        var kept = new List<Candidate>();
        foreach (var c in raw)
        {
            if (c.Direction.Length == 0) continue;
            var role = b.Roles.Count == 0 ? c.SignedInAs : b.Roles.FirstOrDefault(r => string.Equals(r, c.SignedInAs, StringComparison.OrdinalIgnoreCase));
            if (role is null || role.Length == 0) continue;
            var width = c.Width is "phone" or "desk" or "both" ? c.Width : "both";
            if (!seen.Add(Norm(c.Direction))) continue;
            kept.Add(c with { SignedInAs = role, Width = width });
        }
        if (kept.Count == 0)
            return (null, question, raw.Count == 0
                ? "the draft came back with no candidates"
                : "every candidate it wrote was signed in as a role this area does not list, or repeated a direction already tried");
        return (kept.Take(3).ToList(), question, null);
    }

    private static string Norm(string s) => OneLine(s).TrimEnd('.', ' ').ToLowerInvariant();

    // ---- the run ------------------------------------------------------------------------------

    /// <summary>Draft directions for one suggestion and store them. The stored draft, or a refusal.</summary>
    public async Task<DraftView> DraftAsync(string project, string? key, CancellationToken ct = default)
    {
        if (_isolation.On) throw new ReconService.Refused(QaIsolation.Refusal("drafting a direction, which launches an agent,"));
        var k = DraftableKey(key);
        var brief = await BriefAsync(project, k, ct);
        if (await _usage.EnsureSignedInAsync(ct) is { } authError) throw new ReconService.Refused(authError);

        var running = $"{project}\n{k}";
        if (!Running.TryAdd(running, 0)) throw new ReconService.Refused("A draft for this suggestion is already being written.");
        var scratch = Path.Combine(_paths.RunDir, "recon-draft", Guid.NewGuid().ToString("N"));
        try
        {
            var empty = Path.Combine(scratch, "read");
            Directory.CreateDirectory(empty);
            var provider = _providers.For("chart");
            var run = await AgentJsonRunner.RunAsync(provider, empty, Path.Combine(scratch, "run"), Prompt(brief),
                JsonSchema, NoProgress.Instance, Timeout, "direction draft", _log, ct);
            if (run.Outcome is not { } outcome) throw new ReconService.Refused($"The draft did not finish: {run.Error}");
            if (outcome.Error is not null) throw new ReconService.Refused($"The draft did not finish: {outcome.Error}");
            if (outcome.Structured is not { } answer) throw new ReconService.Refused("The draft finished without an answer.");

            var (kept, question, error) = Check(answer, brief);
            if (kept is null) throw new ReconService.Refused($"Nothing to offer: {error}.{(question is null ? "" : " It asked: " + question)}");

            var row = new ReconDraft
            {
                ProjectKey = project, SuggestionKey = k, Area = brief.Area,
                CandidatesJson = JsonSerializer.Serialize(kept, Wire), Question = question,
                Model = outcome.Model, InputTokens = outcome.InputTokens, OutputTokens = outcome.OutputTokens,
                CacheReadTokens = outcome.CacheReadTokens, CacheCreationTokens = outcome.CacheCreationTokens,
                DurationMs = run.DurationMs, CreatedAt = DateTime.UtcNow,
            };
            _db.ReconDrafts.Add(row);
            await _db.SaveChangesAsync(CancellationToken.None);
            return View(row);
        }
        finally
        {
            Running.TryRemove(running, out _);
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
            catch (IOException) { /* a scratch folder left behind is harmless */ }
        }
    }

    // ---- where a filed direction came from ----------------------------------------------------

    /// <summary>
    /// Where a discovery's direction came from, for <see cref="Ticket.DirectionSource"/>: a drafted
    /// candidate as it was, or one the owner changed; a rerun's direction offered word for word; else
    /// the owner's own. Compared without case, spacing or a closing full stop, so a tidied draft is
    /// still the draft.
    /// </summary>
    public static string SourceOf(string? direction, IReadOnlyList<Candidate>? draft, string? rerunDirection)
    {
        var d = Norm(direction ?? "");
        if (draft is not null)
            return draft.Any(c => Norm(c.Direction) == d) ? "draft" : "draft-edited";
        if (rerunDirection is not null && Norm(rerunDirection) == d) return "rerun";
        return "owner";
    }
}
