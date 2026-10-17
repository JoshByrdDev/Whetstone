namespace Whetstone.Services;

/// <summary>
/// Owns the on-disk storage for QA shots (the screenshots captured by the QA phase). Image bytes
/// live under data/qa-shots/&lt;ticketId&gt;/&lt;order&gt;.&lt;ext&gt;; the DB only keeps the filename +
/// caption. The worker hands us an absolute path to a screenshot it just captured (everything runs
/// locally), and we copy it into our own storage so it survives the target repo being cleaned.
/// </summary>
public class QaShotStore
{
    // Image formats we accept from a worker capture, mapped to the content type we serve them back as.
    private static readonly Dictionary<string, string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
        [".gif"] = "image/gif",
    };

    private const long MaxBytes = 10 * 1024 * 1024; // a screenshot over 10MB is almost certainly a mistake

    private readonly DashboardPaths _paths;
    private readonly WorkerHostService? _hosts;

    /// <summary>
    /// How long to wait for a worker host's capture to arrive. Its worker uploads each one as it
    /// lands, every couple of seconds, so a path handed over straight after the screenshot was taken
    /// may be a moment ahead of the file.
    /// </summary>
    public static TimeSpan HostCaptureWait { get; set; } = TimeSpan.FromSeconds(20);

    public QaShotStore(DashboardPaths paths, WorkerHostService? hosts = null) => (_paths, _hosts) = (paths, hosts);

    /// <summary>Delete every stored shot for a ticket (called before writing a fresh set).</summary>
    public void Clear(int ticketId)
    {
        var dir = _paths.QaShotTicketDir(ticketId);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    /// <summary>
    /// Copy the worker's screenshot at <paramref name="sourcePath"/> into storage as
    /// {order}{ext}. Returns the stored filename. Throws if the file is missing, too big, or not a
    /// supported image type.
    /// </summary>
    public async Task<string> SaveAsync(int ticketId, int order, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("empty image path");
        var src = sourcePath.Trim().Trim('"');
        // A run on a worker host names its capture by its own path. The same file is under this
        // machine's data root, uploaded by that worker as it was taken - see HostDataMirror.
        if (!File.Exists(src) && _hosts is not null
            && HostDataMirror.ToBoardCapture(src, _hosts.HostDataRoots(), _paths.DataRoot) is { } mirrored)
        {
            var until = DateTime.UtcNow + HostCaptureWait;
            while (!File.Exists(mirrored) && DateTime.UtcNow < until) await Task.Delay(500);
            if (File.Exists(mirrored)) src = mirrored;
            else throw new FileNotFoundException($"no file at '{src}', and it has not reached the board host from the worker host that took it");
        }
        if (!File.Exists(src))
            throw new FileNotFoundException($"no file at '{src}' (pass an absolute path)");

        var ext = Path.GetExtension(src).ToLowerInvariant();
        if (!AllowedTypes.ContainsKey(ext))
            throw new InvalidOperationException($"unsupported image type '{ext}' (use png/jpg/webp/gif)");

        var info = new FileInfo(src);
        if (info.Length == 0) throw new InvalidOperationException("image file is empty");
        if (info.Length > MaxBytes) throw new InvalidOperationException($"image is {info.Length / (1024 * 1024)}MB, over the 10MB limit");

        var dir = _paths.QaShotTicketDir(ticketId);
        Directory.CreateDirectory(dir);
        var fileName = $"{order}{ext}";
        var dest = Path.Combine(dir, fileName);

        // Copy bytes (don't move — the source belongs to the target repo's .playwright-mcp).
        await using var input = File.OpenRead(src);
        await using var output = File.Create(dest);
        await input.CopyToAsync(output);
        return fileName;
    }

    /// <summary>Absolute path to a stored shot file, or null if the filename is unsafe/missing.</summary>
    public string? DiskPath(int ticketId, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\'))
            return null;
        var path = Path.Combine(_paths.QaShotTicketDir(ticketId), fileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Content type to serve a stored shot with, from its extension (defaults to png).</summary>
    public static string ContentType(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return AllowedTypes.TryGetValue(ext, out var ct) ? ct : "image/png";
    }
}
