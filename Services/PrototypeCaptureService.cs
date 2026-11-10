using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Whetstone.Models;
using Whetstone.Services.Tracker;

namespace Whetstone.Services;

/// <summary>
/// Photographs the prototype and design links in a ticket so the fleet can see them.
/// <para>
/// A worker may not open a link off this machine - the first hard rule covers reads - so a ticket
/// whose visual reference is a hosted prototype was built from its acceptance criteria alone, and
/// the styling came back. The board is not a worker: it already reads the tracker with its own
/// credentials, and it can open a link too, for the hosts a project's config allows
/// (<see cref="PrototypeCaptureConfig"/>). Each capture is a full-page screenshot at a desktop
/// viewport, filed with the ticket's issue attachments, so it reaches a worker host the way they
/// do (<see cref="HostDataMirror"/>) and every phase is told about it by get_ticket.
/// </para>
/// <para>
/// A capture that fails - a sign-in wall, a timeout, a host the copy may not reach - is recorded
/// with its reason and reported wherever the attachments are, so the prompt's rule still fires: a
/// prototype nobody could see is a block that asks the owner for screenshots.
/// </para>
/// </summary>
public class PrototypeCaptureService
{
    /// <summary>The subfolder of a ticket's issue attachments the captures live in.</summary>
    public const string Folder = "prototype-captures";
    private const string ManifestName = "captures.json";

    /// <summary>How long a failed capture is left alone before an unforced pass tries it again.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(10);

    public const int ViewportWidth = 1440;
    public const int ViewportHeight = 900;
    private const int PageTimeoutMs = 30_000;

    /// <summary>One link the board found, and what became of it.</summary>
    /// <param name="File">The capture's file name in <see cref="Folder"/>; null when there is none.</param>
    /// <param name="Error">Why there is no capture; null when there is one.</param>
    public sealed record Entry(string Url, string FoundIn, string? LinkText, string? File, string? Error, DateTime At, string? Title = null)
    {
        public bool Ok => File is not null && Error is null;
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly DashboardPaths _paths;
    private readonly ProjectRegistry _registry;
    private readonly IssueTrackerFactory _trackers;
    private readonly QaIsolation _isolation;
    private readonly IPageCapturer _capturer;
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _locks = new();

    public PrototypeCaptureService(DashboardPaths paths, ProjectRegistry registry, IssueTrackerFactory trackers,
        QaIsolation isolation, IPageCapturer capturer)
        => (_paths, _registry, _trackers, _isolation, _capturer) = (paths, registry, trackers, isolation, capturer);

    public static string DirFor(DashboardPaths paths, int ticketId) => Path.Combine(paths.IssueAttachmentTicketDir(ticketId), Folder);

    /// <summary>What the last pass recorded for this ticket, read off disk with no fetch behind it.</summary>
    public static IReadOnlyList<Entry> Read(DashboardPaths paths, int ticketId)
    {
        var file = Path.Combine(DirFor(paths, ticketId), ManifestName);
        if (!File.Exists(file)) return Array.Empty<Entry>();
        try { return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(file), Json) ?? new List<Entry>(); }
        catch { return Array.Empty<Entry>(); }
    }

    /// <summary>The captures that are on disk, as attachments a phase can open.</summary>
    public static IReadOnlyList<IssueAttachmentStore.OnDisk> ListOnDisk(DashboardPaths paths, int ticketId)
    {
        var dir = DirFor(paths, ticketId);
        return Read(paths, ticketId)
            .Where(e => e.Ok)
            .Select(e => (e, f: new FileInfo(Path.Combine(dir, e.File!))))
            .Where(x => x.f.Exists)
            .Select(x => new IssueAttachmentStore.OnDisk(x.e.File!, x.f.FullName, x.f.Length, CapturedFrom: x.e.Url))
            .ToList();
    }

    /// <summary>Why this machine cannot capture a page, or null when it can.</summary>
    public string? Unavailable() => _capturer.Unavailable();

    /// <summary>
    /// Find the links in the ticket - its own description, and when it is linked, the issue's text,
    /// its comments and its parent chain, read from the tracker here - and capture what is due.
    /// </summary>
    public async Task<IReadOnlyList<Entry>> CaptureAsync(Ticket t, bool force = false, CancellationToken ct = default)
    {
        var cfg = _registry.GetProjectConfig(t.ProjectKey).PrototypeCapture;
        if (!cfg.IsOn) return Read(_paths, t.Id);

        Issue? issue = null;
        IReadOnlyList<IssueContextBuilder.AncestorDto> chain = Array.Empty<IssueContextBuilder.AncestorDto>();
        if (!string.IsNullOrWhiteSpace(t.IssueKey))
        {
            var tracker = _trackers.For(t.ProjectKey);
            if (tracker.IsConfigured)
            {
                try
                {
                    issue = await tracker.GetIssueAsync(t.IssueKey, ct);
                    if (issue is not null)
                        chain = (await IssueContextBuilder.WalkAncestryAsync(issue.Key, issue, k => tracker.GetAncestorAsync(k, ct))).ParentChain;
                }
                // An unreadable issue is a pass that found nothing new - never one that throws away
                // what an earlier pass captured.
                catch { return Read(_paths, t.Id); }
            }
        }
        return await CaptureAsync(t, issue, chain, force, ct);
    }

    /// <summary>The same, over an issue and parent chain the caller has already read.</summary>
    public Task<IReadOnlyList<Entry>> CaptureAsync(Ticket t, Issue? issue,
        IReadOnlyList<IssueContextBuilder.AncestorDto> chain, bool force = false, CancellationToken ct = default)
        => CaptureLinksAsync(t.Id, _registry.GetProjectConfig(t.ProjectKey).PrototypeCapture, SourcesOf(t, issue, chain), force, ct);

    /// <summary>Where a ticket's links can be, nearest first.</summary>
    public static IEnumerable<PrototypeLinks.Source> SourcesOf(Ticket t, Issue? issue, IReadOnlyList<IssueContextBuilder.AncestorDto> chain)
    {
        if (!string.IsNullOrWhiteSpace(t.Description))
            yield return new("ticket", "this ticket's description", t.Description);
        if (issue is not null)
        {
            yield return new("description", $"{issue.Key}'s description", issue.FullText);
            foreach (var c in issue.Comments)
                yield return new($"comment-{c.Id}", $"a comment on {issue.Key}{(c.Author is null ? "" : " by " + c.Author)}", c.Text);
        }
        foreach (var a in chain.Where(a => a.Readable))
            yield return new($"parent-{a.Key}", $"{a.Key}'s description (an ancestor)", a.Description);
    }

    /// <summary>
    /// The pass itself: decide what each link needs, capture what is due in one browser, and record
    /// the result. A link captured before is kept without a fetch unless <paramref name="force"/>; a
    /// failure is retried after <see cref="RetryAfter"/>. A capture whose link has gone from the
    /// ticket is deleted, so the attachments never show a design the ticket no longer points at.
    /// </summary>
    public async Task<IReadOnlyList<Entry>> CaptureLinksAsync(int ticketId, PrototypeCaptureConfig cfg,
        IEnumerable<PrototypeLinks.Source> sources, bool force = false, CancellationToken ct = default)
    {
        if (!cfg.IsOn) return Read(_paths, ticketId);
        var gate = _locks.GetOrAdd(ticketId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var dir = DirFor(_paths, ticketId);
            var before = Read(_paths, ticketId).ToDictionary(e => e.Url, StringComparer.Ordinal);
            var links = PrototypeLinks.Find(sources, cfg);
            var now = DateTime.UtcNow;
            var entries = new List<Entry>();
            var due = new List<(PrototypeLinks.Link Link, string File)>();
            var unavailable = _capturer.Unavailable();

            foreach (var (link, i) in links.Select((l, i) => (l, i)))
            {
                if (i >= cfg.MaxLinks)
                {
                    entries.Add(new Entry(link.Url, link.FoundIn, link.LinkText, null,
                        $"over this project's {cfg.MaxLinks}-link cap for captures (prototypeCapture.maxLinks)", now));
                    continue;
                }
                before.TryGetValue(link.Url, out var had);
                if (!force && had is { Ok: true } && File.Exists(Path.Combine(dir, had.File!)))
                {
                    entries.Add(had with { FoundIn = link.FoundIn, LinkText = link.LinkText });
                    continue;
                }
                if (!force && had is { Ok: false } && now - had.At < RetryAfter && had.Error is not null && !had.Error.StartsWith("over this"))
                {
                    entries.Add(had with { FoundIn = link.FoundIn, LinkText = link.LinkText });
                    continue;
                }
                var uri = new Uri(link.Url);
                if (_isolation.On && !PrototypeLinks.IsLoopback(uri))
                {
                    entries.Add(new Entry(link.Url, link.FoundIn, link.LinkText, null, QaIsolation.Refusal("capturing a page off this machine"), now));
                    continue;
                }
                if (unavailable is not null)
                {
                    entries.Add(new Entry(link.Url, link.FoundIn, link.LinkText, null, unavailable, now));
                    continue;
                }
                due.Add((link, FileNameFor(link)));
            }

            if (due.Count > 0)
            {
                Directory.CreateDirectory(dir);
                var results = await _capturer.CaptureAsync(
                    due.Select(d => new PageRequest(d.File, d.Link.Url, Path.Combine(dir, d.File))).ToList(),
                    cfg.Hosts, ct);
                foreach (var (link, file) in due)
                {
                    var r = results.TryGetValue(file, out var got) ? got
                        : new PageResult(false, "the capture never answered for this link", null);
                    var ok = r.Ok && File.Exists(Path.Combine(dir, file));
                    entries.Add(new Entry(link.Url, link.FoundIn, link.LinkText, ok ? file : null,
                        ok ? null : r.Error ?? "the capture wrote no file", now, r.Title));
                }
            }

            // Back in the order the links were found, whatever order they were settled in.
            var order = links.Select((l, i) => (l.Url, i)).ToDictionary(x => x.Url, x => x.i);
            entries = entries.OrderBy(e => order.TryGetValue(e.Url, out var i) ? i : int.MaxValue).ToList();

            if (entries.Count == 0 && !Directory.Exists(dir)) return entries;
            Directory.CreateDirectory(dir);
            var keep = entries.Where(e => e.File is not null).Select(e => e.File!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                var name = Path.GetFileName(f);
                if (name.Equals(ManifestName, StringComparison.OrdinalIgnoreCase) || keep.Contains(name)) continue;
                try { File.Delete(f); } catch { /* a stale capture that will not delete is deleted next pass */ }
            }
            await File.WriteAllTextAsync(Path.Combine(dir, ManifestName), JsonSerializer.Serialize(entries, Json), ct);
            return entries;
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// A name that says where the capture came from: where the link was found, the host, and a short
    /// hash of the link so two links on one host never share a file -
    /// <c>description-design.example.com-1a2b3c4d.png</c>.
    /// </summary>
    public static string FileNameFor(PrototypeLinks.Link link)
    {
        var uri = new Uri(link.Url);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(link.Url)))[..8].ToLowerInvariant();
        var host = uri.IsDefaultPort ? uri.Host : $"{uri.Host}-{uri.Port}";
        return $"{Slug(link.Code)}-{Slug(host)}-{hash}.png";
    }

    private static string Slug(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s.ToLowerInvariant())
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '-');
        var slug = sb.ToString().Trim('-', '.');
        return slug.Length == 0 ? "link" : slug.Length > 60 ? slug[..60] : slug;
    }
}

public sealed record PageRequest(string Id, string Url, string OutPath);
public sealed record PageResult(bool Ok, string? Error, string? Title);

/// <summary>How a page becomes a picture. An interface so the bookkeeping is testable without a browser.</summary>
public interface IPageCapturer
{
    /// <summary>Why no page can be captured on this machine, or null.</summary>
    string? Unavailable();

    /// <summary>Capture every request in one browser; the answer is keyed by <see cref="PageRequest.Id"/>.</summary>
    Task<IReadOnlyDictionary<string, PageResult>> CaptureAsync(IReadOnlyList<PageRequest> pages,
        IReadOnlyCollection<string> allowHosts, CancellationToken ct);
}

/// <summary>
/// Captures with <c>data/playwright-mcp/capture-pages.mjs</c>, on the browser library QA's own
/// browser server already installs there - so there is one install to make, not two.
/// </summary>
public sealed class NodePageCapturer : IPageCapturer
{
    private readonly DashboardPaths _paths;
    private readonly ILogger<NodePageCapturer> _log;
    public NodePageCapturer(DashboardPaths paths, ILogger<NodePageCapturer> log) => (_paths, _log) = (paths, log);

    public string? Unavailable() => UnavailableAt(_paths);

    /// <summary>The same question, for a caller with no capturer to hand - the Setup check.</summary>
    public static string? UnavailableAt(DashboardPaths paths)
    {
        if (!File.Exists(paths.PageCaptureScript)) return $"the capture script is missing ({paths.PageCaptureScript})";
        if (!Directory.Exists(Path.Combine(Path.GetDirectoryName(paths.PageCaptureScript)!, "node_modules", "playwright-core")))
            return "the board's browser is not installed - run `npm install` once in data/playwright-mcp";
        return null;
    }

    public async Task<IReadOnlyDictionary<string, PageResult>> CaptureAsync(IReadOnlyList<PageRequest> pages,
        IReadOnlyCollection<string> allowHosts, CancellationToken ct)
    {
        var results = new Dictionary<string, PageResult>();
        var psi = new ProcessStartInfo("node")
        {
            WorkingDirectory = Path.GetDirectoryName(_paths.PageCaptureScript)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = ProcessText.Utf8,
            StandardOutputEncoding = ProcessText.Utf8,
            StandardErrorEncoding = ProcessText.Utf8,
        };
        psi.ArgumentList.Add(Path.GetFileName(_paths.PageCaptureScript));

        using var proc = new Process { StartInfo = psi };
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            try
            {
                using var doc = JsonDocument.Parse(e.Data);
                var r = doc.RootElement;
                if (!r.TryGetProperty("id", out var id)) return;
                var ok = r.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
                lock (results)
                    results[id.GetString()!] = new PageResult(ok,
                        r.TryGetProperty("error", out var err) ? err.GetString() : null,
                        r.TryGetProperty("title", out var title) ? title.GetString() : null);
            }
            catch (Exception ex) { _log.LogDebug(ex, "Unreadable capture line: {Line}", e.Data); }
        };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };

        string? failed = null;
        try
        {
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.StandardInput.WriteAsync(JsonSerializer.Serialize(new
            {
                items = pages.Select(p => new { id = p.Id, url = p.Url, @out = p.OutPath }),
                allowHosts,
                width = PrototypeCaptureService.ViewportWidth,
                height = PrototypeCaptureService.ViewportHeight,
                timeoutMs = 30_000,
            }));
            proc.StandardInput.Close();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30 + 60 * pages.Count));
            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                failed = ct.IsCancellationRequested ? "the capture was cancelled" : "the capture ran out of time";
            }
        }
        catch (Exception ex) { failed = $"could not start node to capture the page: {ex.Message}"; }

        failed ??= proc.HasExited && proc.ExitCode != 0
            ? $"the capture script failed: {stderr.ToString().Trim().Split('\n').LastOrDefault()?.Trim()}"
            : null;
        lock (results)
        {
            foreach (var p in pages)
                if (!results.ContainsKey(p.Id))
                    results[p.Id] = new PageResult(false, failed ?? "the capture never answered for this link", null);
            return new Dictionary<string, PageResult>(results);
        }
    }
}

/// <summary>
/// Captures in the background, so the moments that should start one - a claim, a new ticket - do
/// not wait on a browser. A claim's caller gives up after fifteen seconds.
/// </summary>
public sealed class PrototypeCaptureQueue : BackgroundService
{
    private readonly Channel<int> _channel = Channel.CreateUnbounded<int>(new UnboundedChannelOptions { SingleReader = true });
    private readonly HashSet<int> _pending = new();
    private readonly IServiceScopeFactory _scopes;
    private readonly PrototypeCaptureService _captures;
    private readonly ILogger<PrototypeCaptureQueue> _log;

    public PrototypeCaptureQueue(IServiceScopeFactory scopes, PrototypeCaptureService captures, ILogger<PrototypeCaptureQueue> log)
        => (_scopes, _captures, _log) = (scopes, captures, log);

    public void Enqueue(int ticketId)
    {
        lock (_pending) { if (!_pending.Add(ticketId)) return; }
        _channel.Writer.TryWrite(ticketId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var id in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            lock (_pending) _pending.Remove(id);
            try
            {
                using var scope = _scopes.CreateScope();
                var t = await scope.ServiceProvider.GetRequiredService<TicketService>().GetAsync(id);
                if (t is not null) await _captures.CaptureAsync(t, ct: stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { _log.LogWarning(ex, "Prototype capture for ticket {Id} failed", id); }
        }
    }
}
