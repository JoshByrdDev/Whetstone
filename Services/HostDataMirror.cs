using System.IO.Compression;

namespace Whetstone.Services;

/// <summary>
/// The files a ticket's run reads or writes by path, kept in step between the board host and a
/// worker host (docs/worker-hosts-plan.md, phase 2).
/// <para>
/// The rule is one sentence: <b>each machine keeps a ticket's files at the same path under its own
/// data root.</b> A ticket's attachments and owner-note files (<c>issue-attachments/&lt;id&gt;</c>,
/// <c>ticket-files/&lt;id&gt;</c>) are copied to the worker host when its run starts, so a
/// <c>localPath</c> a ticket tool gives - the board host's - names a file the worker finds under
/// its own root. QA captures (<c>qa-captures/&lt;id&gt;</c>) go the other way while the run is
/// going, so a screenshot path the worker hands <c>set_qa_shots</c> - the worker host's - names a
/// file the board host has under its root.
/// </para>
/// </summary>
public static class HostDataMirror
{
    public const string QaCapturesFolder = "qa-captures";

    /// <summary>Image types a capture may be; the same set QA shots accept.</summary>
    public static readonly string[] CaptureExtensions = { ".png", ".jpg", ".jpeg", ".webp", ".gif" };

    public const long MaxCaptureBytes = 10 * 1024 * 1024;

    /// <summary>A ticket's own files on the board host, as a zip of paths relative to the data root.</summary>
    public static byte[] BuildTicketData(DashboardPaths paths, int ticketId)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var dir in new[] { paths.TicketFilesTicketDir(ticketId), paths.IssueAttachmentTicketDir(ticketId) })
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    var entry = zip.CreateEntry(Path.GetRelativePath(paths.DataRoot, f).Replace('\\', '/'), CompressionLevel.Fastest);
                    using var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var dst = entry.Open();
                    src.CopyTo(dst);
                }
            }
        }
        return ms.ToArray();
    }

    /// <summary>
    /// A capture's path below its ticket's capture folder, checked: forward-slash segments, none
    /// empty, none a dot or two, and an image extension. Null when it is not one.
    /// </summary>
    public static string? SafeCaptureName(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        var segments = relative.Replace('\\', '/').Split('/');
        if (segments.Any(s => s.Length == 0 || s is "." or ".." || s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) return null;
        if (!CaptureExtensions.Contains(Path.GetExtension(segments[^1]), StringComparer.OrdinalIgnoreCase)) return null;
        return string.Join('/', segments);
    }

    /// <summary>
    /// Where the board host keeps a capture a worker host names by its own path: the same path under
    /// the board's root, when <paramref name="path"/> lies in a worker host's capture folder. Null
    /// for any other path - this maps captures, and nothing else, from another machine's disk.
    /// </summary>
    public static string? ToBoardCapture(string path, IEnumerable<string> hostDataRoots, string boardDataRoot)
    {
        var p = path.Trim().Trim('"').Replace('/', '\\');
        foreach (var root in hostDataRoots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var prefix = root.Trim().Replace('/', '\\').TrimEnd('\\') + "\\" + QaCapturesFolder + "\\";
            if (!p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = p[prefix.Length..];
            var parts = rest.Split('\\');
            if (parts.Length < 2 || !int.TryParse(parts[0], out _)) continue;
            if (SafeCaptureName(string.Join('/', parts[1..])) is null) continue;
            return Path.Combine(new[] { boardDataRoot, QaCapturesFolder }.Concat(parts).ToArray());
        }
        return null;
    }
}
