using Whetstone.Services.Tracker;

namespace Whetstone.Services;

/// <summary>
/// Caches a linked issue's attachments on disk so a phase can actually look at them.
/// <para>
/// Attachments are often the most informative part of a ticket - a mockup, a screenshot of the bug,
/// a spreadsheet of expected values - and a worker planning from the description alone is guessing
/// at whatever they showed. It cannot fetch them itself: the tracker token lives here, server-side,
/// and deliberately never reaches the worker. So we download with our credentials and hand over
/// local paths, the same shape as <see cref="QaShotStore"/>.
/// </para>
/// </summary>
public class IssueAttachmentStore
{
    /// <summary>
    /// What is worth caching: things a model can actually read. Source and config are plain text, and
    /// an attached prototype client or schema is often the contract a feature is built against - so
    /// they are in. Anything compiled or archived is not: a model cannot read it, and an archive is a
    /// way to carry a binary past this list.
    /// </summary>
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".gif",   // screenshots and mockups
        ".pdf",                                      // specs, statements, letters
        ".txt", ".md", ".csv", ".json", ".xml", ".log", ".sql",
        // source a model can Read as text
        ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".cs", ".py", ".java", ".kt", ".go", ".rb", ".php",
        ".ps1", ".psm1", ".sh", ".html", ".htm", ".css", ".scss", ".vue", ".svelte",
        // config and contracts
        ".yaml", ".yml", ".toml", ".ini", ".config", ".graphql", ".proto", ".xsd", ".wsdl", ".http",
        ".tsv", ".diff", ".patch",
    };

    private const long MaxBytes = 20 * 1024 * 1024;  // per attachment
    public const int MaxCount = 10;                  // per issue, unless the caller budgets tighter

    private readonly DashboardPaths _paths;
    public IssueAttachmentStore(DashboardPaths paths) => _paths = paths;

    /// <summary>One cached attachment, as handed to the worker.</summary>
    public record Cached(string Id, string FileName, string LocalPath, long Bytes, string? MimeType, string? Author, string? Created);

    /// <summary>An attachment we deliberately did not cache, and why. Never dropped silently.</summary>
    public record Skipped(string FileName, string Reason);

    public record Result(IReadOnlyList<Cached> Attachments, IReadOnlyList<Skipped> NotCached);

    /// <summary>One attachment as read back off disk, with no tracker call behind it.</summary>
    /// <param name="SourceIssue">Null when it hangs off the ticket's own issue; otherwise the ancestor
    /// key it was inherited from. A parent's mockup is worth reading and worth not mistaking for your
    /// own - "the epic shows the layout" and "your ticket shows the layout" are different claims.</param>
    public record OnDisk(string FileName, string LocalPath, long Bytes, string? SourceIssue = null);

    /// <summary>
    /// What is already cached for this ticket, read straight from the folder.
    /// <para>
    /// This is how a phase finds out an attachment exists at all. <see cref="CacheAsync"/> only runs
    /// when someone asks for the full issue context, so a phase that never thought to ask never
    /// learned there was a design to look at - it just reasoned without one and said so afterwards.
    /// Listing the folder costs nothing and needs no credentials, so every phase can be told.
    /// </para>
    /// </summary>
    public IReadOnlyList<OnDisk> ListCached(int ticketId)
    {
        var root = new DirectoryInfo(_paths.IssueAttachmentTicketDir(ticketId));
        if (!root.Exists) return Array.Empty<OnDisk>();

        // The ticket's own attachments sit flat in its folder; anything inherited from an ancestor
        // sits in a "from-KEY" subfolder. Both are listed - a phase that never opens the parent's
        // mockup because nobody mentioned it is the failure this whole listing exists to prevent -
        // but they arrive labelled, so the reader knows which issue made the claim.
        var own = root.GetFiles()
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => new OnDisk(OriginalName(f.Name), f.FullName, f.Length));

        var inherited = root.GetDirectories()
            .Select(d => (Key: DashboardPaths.AncestorKeyFromDir(d.Name), Dir: d))
            .Where(d => d.Key is not null)
            .OrderBy(d => d.Key, StringComparer.OrdinalIgnoreCase)
            .SelectMany(d => d.Dir.GetFiles()
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => new OnDisk(OriginalName(f.Name), f.FullName, f.Length, d.Key)));

        return own.Concat(inherited).ToList();
    }

    /// <summary>Undo <see cref="SafeName"/>: drop the attachment id we prefixed for uniqueness.</summary>
    private static string OriginalName(string stored)
    {
        var dash = stored.IndexOf('-');
        if (dash <= 0) return stored;

        // A numeric run is Jira's attachment id. Azure DevOps uses a GUID, whose own hyphens make
        // "first dash" the wrong split - the id has to be recognised whole, or "a1b2...-Design.pdf"
        // comes back as "b2c3-...-Design.pdf" and an agent told to open "the design PDF" is handed
        // a name that is not one.
        if (stored[..dash].All(char.IsAsciiDigit)) return stored[(dash + 1)..];

        const int guid = 36;
        if (stored.Length > guid && stored[guid] == '-' && Guid.TryParse(stored[..guid], out _))
            return stored[(guid + 1)..];

        return stored;
    }

    /// <summary>
    /// Download every cacheable attachment into this ticket's folder, reusing anything already on
    /// disk with the same size. Returns what was cached and what was not, so the caller can say so
    /// rather than implying full coverage.
    /// </summary>
    /// <param name="sourceIssueKey">Null for the ticket's own issue. Set to an ancestor's key to file
    /// its attachments in a subfolder of their own - a parent's mockup is context worth having, but
    /// it is not the ticket's own attachment and must not be listed as one.</param>
    /// <param name="limit">How many attachments this issue may contribute. Ancestors share a budget
    /// across the chain, so a deep hierarchy of well-illustrated epics cannot turn one context call
    /// into forty downloads. Anything over it is reported as skipped, never dropped in silence.</param>
    public async Task<Result> CacheAsync(int ticketId, IReadOnlyList<IssueAttachment> attachments,
        IIssueTracker tracker, string? sourceIssueKey = null, int limit = MaxCount)
    {
        var cached = new List<Cached>();
        var skipped = new List<Skipped>();

        if (attachments.Count == 0) return new Result(cached, skipped);

        if (limit <= 0)
        {
            foreach (var a in attachments)
                skipped.Add(new Skipped(a.FileName, "the attachment budget for this context call was already spent"));
            return new Result(cached, skipped);
        }

        var dir = sourceIssueKey is null
            ? _paths.IssueAttachmentTicketDir(ticketId)
            : _paths.IssueAttachmentAncestorDir(ticketId, sourceIssueKey);
        var considered = 0;

        foreach (var a in attachments)
        {
            var ext = Path.GetExtension(a.FileName);
            if (!AllowedExtensions.Contains(ext))
            {
                skipped.Add(new Skipped(a.FileName, $"'{ext}' is not a readable type"));
                continue;
            }
            if (a.Bytes > MaxBytes)
            {
                skipped.Add(new Skipped(a.FileName, $"{a.Bytes / (1024 * 1024)}MB is over the {MaxBytes / (1024 * 1024)}MB limit"));
                continue;
            }
            if (considered >= limit)
            {
                skipped.Add(new Skipped(a.FileName, $"over the {limit}-attachment cap for this issue"));
                continue;
            }
            considered++;

            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, SafeName(a.Id, a.FileName));

            // Attachments are immutable in both trackers - an edit produces a new attachment with a
            // new id - so a file already sitting at this id is current. The size comparison is a
            // guard against a half-written file, not a freshness check, which is why a tracker that
            // did not report a size (0) is not a reason to fetch the bytes again.
            var onDisk = new FileInfo(dest);
            if (!(onDisk.Exists && (a.Bytes == 0 || onDisk.Length == a.Bytes)))
            {
                var bytes = await tracker.DownloadAttachmentAsync(a);
                if (bytes is null || bytes.Length == 0)
                {
                    skipped.Add(new Skipped(a.FileName, "download failed"));
                    continue;
                }
                await File.WriteAllBytesAsync(dest, bytes);
                onDisk = new FileInfo(dest);
            }

            cached.Add(new Cached(a.Id, a.FileName, dest, onDisk.Length, a.MimeType, a.Author, a.Created));
        }

        return new Result(cached, skipped);
    }

    /// <summary>Prefix with the attachment id so two attachments of the same name cannot collide.</summary>
    private static string SafeName(string id, string fileName)
    {
        var name = Path.GetFileName(fileName);
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        if (string.IsNullOrWhiteSpace(name)) name = "attachment";
        return $"{id}-{name}";
    }
}
