using Whetstone.Models;

namespace Whetstone.Services;

/// <summary>
/// Owns the on-disk storage for files the ticket's owner attaches to a progress note. Bytes live
/// under data/ticket-files/&lt;ticketId&gt;/&lt;generated&gt;.&lt;ext&gt;; the DB keeps the original
/// name, the size and the stored filename (see <see cref="NoteAttachment"/>).
/// <para>
/// The sibling of <see cref="QaShotStore"/>, and deliberately more permissive than it. A QA shot is
/// always a screenshot the pipeline took itself, so that store accepts images only. This one takes
/// whatever a person needed to show us — the two things they reach for are a screenshot of what is
/// wrong and a dump of what the log said, and refusing the second would send them back to the
/// terminal for exactly the case this exists to keep on the dashboard.
/// </para>
/// </summary>
public class TicketFileStore
{
    /// <summary>
    /// What a note may carry, mapped to the content type we serve it back as, and whether a browser
    /// renders it inline.
    /// <para>
    /// An allowlist rather than a blocklist, and the reason is not really security — this dashboard
    /// binds to localhost and serves one person. It is that every one of these is a type an agent can
    /// actually USE: it opens them with the Read tool, which handles images and text and nothing
    /// else. Accepting a .zip would store a file the worker can only report that it cannot read.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, (string ContentType, bool IsImage)> AllowedTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".png"]  = ("image/png", true),
            [".jpg"]  = ("image/jpeg", true),
            [".jpeg"] = ("image/jpeg", true),
            [".webp"] = ("image/webp", true),
            [".gif"]  = ("image/gif", true),
            [".pdf"]  = ("application/pdf", false),

            // Text, served as text/plain whatever it claims to be: the browser must never be talked
            // into executing something out of this folder, and the agent reads bytes either way.
            [".txt"]   = ("text/plain", false),
            [".log"]   = ("text/plain", false),
            [".md"]    = ("text/plain", false),
            [".json"]  = ("text/plain", false),
            [".csv"]   = ("text/plain", false),
            [".tsv"]   = ("text/plain", false),
            [".xml"]   = ("text/plain", false),
            [".yml"]   = ("text/plain", false),
            [".yaml"]  = ("text/plain", false),
            [".sql"]   = ("text/plain", false),
            [".diff"]  = ("text/plain", false),
            [".patch"] = ("text/plain", false),
            [".har"]   = ("text/plain", false),
        };

    /// <summary>
    /// Per-file ceiling. Generous next to the QA shot store's 10MB because a log dump is the case
    /// this was built for and logs are big; small enough that a mis-drag of a video is refused
    /// rather than copied.
    /// </summary>
    public const long MaxBytes = 25 * 1024 * 1024;

    /// <summary>How many files one note may carry, so a drag of a whole folder is refused as a set.</summary>
    public const int MaxPerNote = 6;

    public static string AllowedExtensionList => string.Join(", ", AllowedTypes.Keys.OrderBy(k => k));

    private readonly DashboardPaths _paths;
    public TicketFileStore(DashboardPaths paths) => _paths = paths;

    /// <summary>The absolute path a stored attachment resolves to.</summary>
    public string PathFor(NoteAttachment a) => Path.Combine(_paths.TicketFilesTicketDir(a.TicketId), a.StoredFile);

    /// <summary>
    /// Copy an uploaded stream into storage and return the row to persist (unsaved, with no
    /// CommentId yet — the caller attaches it to the note it is creating).
    /// </summary>
    public async Task<NoteAttachment> SaveAsync(int ticketId, string fileName, Stream content, long length)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            throw new InvalidOperationException("attachment has no filename");
        if (length <= 0)
            throw new InvalidOperationException($"'{fileName}' is empty");
        if (length > MaxBytes)
            throw new InvalidOperationException($"'{fileName}' is {length / (1024 * 1024)}MB, over the {MaxBytes / (1024 * 1024)}MB limit");

        // GetFileName strips any directory the browser sent along with it, so a name like
        // "..\..\config.json" reduces to "config.json" before it is even looked at.
        var safeName = Path.GetFileName(fileName.Trim());
        var ext = Path.GetExtension(safeName).ToLowerInvariant();
        if (!AllowedTypes.TryGetValue(ext, out var type))
            throw new InvalidOperationException(
                $"'{safeName}' is not a type a worker can open{(ext.Length == 0 ? " (it has no extension)" : $" ('{ext}')")}. "
              + $"Attach one of: {AllowedExtensionList}.");

        var dir = _paths.TicketFilesTicketDir(ticketId);
        Directory.CreateDirectory(dir);

        // The stored name is generated, never derived from the upload: two notes both carrying
        // "screenshot.png" must not be the same file on disk.
        var stored = $"{Guid.NewGuid():N}{ext}";
        var dest = Path.Combine(dir, stored);
        await using (var output = File.Create(dest))
            await content.CopyToAsync(output);

        return new NoteAttachment
        {
            TicketId = ticketId,
            FileName = safeName,
            StoredFile = stored,
            ContentType = type.ContentType,
            IsImage = type.IsImage,
            Bytes = new FileInfo(dest).Length,
        };
    }

    /// <summary>Delete a stored file. Best effort — a row without its bytes is still worth removing.</summary>
    public void Delete(NoteAttachment a)
    {
        try
        {
            var path = PathFor(a);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { /* the row is going either way */ }
    }
}
