using System.Text.Json;
using Whetstone.Mcp;
using Whetstone.Services;
using Whetstone.Services.Tracker;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The board's screenshots of a ticket's prototype links: which links count, which may be fetched
/// at all, and what a failed capture leaves behind for the fleet to act on.
/// </summary>
public class PrototypeCaptureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ccd-proto-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly DashboardPaths _paths;

    public PrototypeCaptureTests() => _paths = new DashboardPaths(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static PrototypeCaptureConfig Cfg(string json) =>
        PrototypeCaptureConfig.From(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!);

    private static PrototypeCaptureConfig Hosts(params string[] hosts) => new(hosts, Array.Empty<string>(), 5);

    private static PrototypeLinks.Source Src(string text, string code = "description") => new(code, $"PROJ-1's {code}", text);

    private static IReadOnlyList<PrototypeLinks.Link> Find(PrototypeCaptureConfig cfg, params string[] texts) =>
        PrototypeLinks.Find(texts.Select((t, i) => Src(t, i == 0 ? "description" : $"comment-{i}")), cfg);

    // ---- Link detection ------------------------------------------------------

    [Fact]
    public void Markdown_wiki_html_and_bare_links_are_all_found()
    {
        var links = Find(Hosts("design.example.com"),
            "See the [checkout prototype](https://design.example.com/proto/1) and "
            + "[Old one|https://design.example.com/proto/2], <a href=\"https://design.example.com/proto/3\">this</a>, "
            + "and https://design.example.com/proto/4.");

        Assert.Equal(new[]
        {
            "https://design.example.com/proto/1", "https://design.example.com/proto/2",
            "https://design.example.com/proto/3", "https://design.example.com/proto/4",
        }, links.Select(l => l.Url));
        Assert.Equal("checkout prototype", links[0].LinkText);
        Assert.Null(links[3].LinkText);
    }

    [Fact]
    public void A_link_found_twice_is_captured_once_and_credited_to_where_it_was_first_seen()
    {
        var links = Find(Hosts("design.example.com"),
            "The design: https://design.example.com/p",
            "As discussed, [the prototype](https://design.example.com/p)");

        var one = Assert.Single(links);
        Assert.Equal("description", one.Code);
    }

    [Fact]
    public void Trailing_punctuation_is_not_part_of_the_link()
    {
        var one = Assert.Single(Find(Hosts("design.example.com"), "Prototype: https://design.example.com/p/9)."));
        Assert.Equal("https://design.example.com/p/9", one.Url);
    }

    [Fact]
    public void Link_text_narrows_what_an_allowed_host_offers()
    {
        var cfg = new PrototypeCaptureConfig(new[] { "app.example.com" }, new[] { "prototype", "mockup" }, 5);
        var links = Find(cfg,
            "Logs are at https://app.example.com/logs\n"
            + "The mockup: https://app.example.com/m/1\n"
            + "[Prototype](https://app.example.com/p/2)");

        Assert.Equal(new[] { "https://app.example.com/p/2", "https://app.example.com/m/1" }, links.Select(l => l.Url));
    }

    // ---- The allowlist -------------------------------------------------------

    [Fact]
    public void A_link_whose_host_is_not_listed_is_never_offered_whatever_its_text_says()
    {
        var cfg = new PrototypeCaptureConfig(new[] { "design.example.com" }, new[] { "prototype" }, 5);
        var links = Find(cfg,
            "[Prototype](https://intranet.example.net/admin) and [prototype](https://design.example.com.evil.test/p) "
            + "and [prototype](https://notdesign.example.com/p)");

        Assert.Empty(links);
    }

    [Fact]
    public void A_listed_host_covers_its_subdomains_and_a_listed_port_must_match()
    {
        Assert.True(PrototypeLinks.HostAllowed(new Uri("https://www.design.example.com/x"), new[] { "design.example.com" }));
        Assert.True(PrototypeLinks.HostAllowed(new Uri("http://localhost:5399/x"), new[] { "localhost:5399" }));
        Assert.False(PrototypeLinks.HostAllowed(new Uri("http://localhost:5338/x"), new[] { "localhost:5399" }));
        Assert.False(PrototypeLinks.HostAllowed(new Uri("https://example.com/x"), new[] { "design.example.com" }));
    }

    [Fact]
    public void Only_http_links_without_credentials_are_ever_fetched()
    {
        var cfg = Hosts("design.example.com");
        Assert.Empty(Find(cfg, "ftp://design.example.com/p and https://user:pw@design.example.com/p"));
    }

    [Fact]
    public void No_hosts_means_off_and_nothing_is_found()
    {
        Assert.False(Cfg("{}").IsOn);
        Assert.False(Cfg("""{"prototypeCapture":{"linkText":["prototype"]}}""").IsOn);
        Assert.Empty(Find(Cfg("{}"), "[Prototype](https://design.example.com/p)"));
    }

    [Fact]
    public void Config_forgives_a_pasted_scheme_or_path_and_drops_what_is_not_a_host()
    {
        var cfg = Cfg("""{"prototypeCapture":{"hosts":["https://Design.Example.com/proto","localhost:5399","*.cdn.example.org","not a host",""],"maxLinks":99}}""");
        Assert.Equal(new[] { "design.example.com", "localhost:5399", "cdn.example.org" }, cfg.Hosts);
        Assert.Equal(20, cfg.MaxLinks);
    }

    [Fact]
    public void A_capture_is_named_for_where_its_link_was_found()
    {
        var link = new PrototypeLinks.Link("https://design.example.com/p/1", "parent-PROJ-7", "PROJ-7's description", null);
        var name = PrototypeCaptureService.FileNameFor(link);
        Assert.Matches(@"^parent-proj-7-design\.example\.com-[0-9a-f]{8}\.png$", name);
    }

    // ---- Capturing -----------------------------------------------------------

    private sealed class FakeCapturer : IPageCapturer
    {
        public string? Why;
        public Func<PageRequest, PageResult> Answer = _ => new PageResult(true, null, "A prototype");
        public List<string> Asked { get; } = new();
        public string? Unavailable() => Why;

        public Task<IReadOnlyDictionary<string, PageResult>> CaptureAsync(IReadOnlyList<PageRequest> pages,
            IReadOnlyCollection<string> allowHosts, CancellationToken ct)
        {
            var results = new Dictionary<string, PageResult>();
            foreach (var p in pages)
            {
                Asked.Add(p.Url);
                var r = Answer(p);
                if (r.Ok) File.WriteAllBytes(p.OutPath, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
                results[p.Id] = r;
            }
            return Task.FromResult<IReadOnlyDictionary<string, PageResult>>(results);
        }
    }

    private PrototypeCaptureService Service(FakeCapturer capturer, bool isolated = false)
    {
        var iso = new QaIsolation(isolated);
        var registry = new ProjectRegistry(_paths, iso);
        return new PrototypeCaptureService(_paths, registry, new IssueTrackerFactory(registry, iso), iso, capturer);
    }

    [Fact]
    public async Task A_captured_link_is_listed_among_the_tickets_attachments_with_its_link()
    {
        var svc = Service(new FakeCapturer());
        await svc.CaptureLinksAsync(7, Hosts("design.example.com"), new[] { Src("[Prototype](https://design.example.com/p)") });

        var one = Assert.Single(new IssueAttachmentStore(_paths).ListCached(7));
        Assert.Equal("https://design.example.com/p", one.CapturedFrom);
        Assert.Null(one.SourceIssue);
        Assert.True(File.Exists(one.LocalPath));
        // Under the ticket's attachment folder, so a worker host is sent it with the rest.
        Assert.StartsWith(_paths.IssueAttachmentTicketDir(7), one.LocalPath);
    }

    [Fact]
    public async Task A_failed_capture_is_recorded_with_its_reason_and_reported_as_not_cached()
    {
        var capturer = new FakeCapturer { Answer = _ => new PageResult(false, "the page asks for a password - a sign-in wall, not the prototype", null) };
        var svc = Service(capturer);
        var got = await svc.CaptureLinksAsync(7, Hosts("design.example.com"), new[] { Src("[Prototype](https://design.example.com/p)") });

        var entry = Assert.Single(got);
        Assert.False(entry.Ok);
        Assert.Contains("sign-in wall", entry.Error);
        Assert.Empty(new IssueAttachmentStore(_paths).ListCached(7));

        // And what every phase reads says so, with the instruction the prompt's rule turns on.
        var note = TicketTools.CapturesNote(PrototypeCaptureService.Read(_paths, 7))!;
        Assert.Contains("https://design.example.com/p", note);
        Assert.Contains("sign-in wall", note);
        Assert.Contains("ask the owner for screenshots", note);
    }

    [Fact]
    public async Task A_machine_that_cannot_capture_reports_every_link_as_failed_rather_than_skipping_it()
    {
        var capturer = new FakeCapturer { Why = "the board's browser is not installed" };
        var got = await Service(capturer).CaptureLinksAsync(7, Hosts("design.example.com"),
            new[] { Src("https://design.example.com/a https://design.example.com/b") });

        Assert.Equal(2, got.Count);
        Assert.All(got, e => Assert.Equal("the board's browser is not installed", e.Error));
        Assert.Empty(capturer.Asked);
    }

    [Fact]
    public async Task A_link_captured_once_is_not_fetched_again_and_a_failure_waits_before_a_retry()
    {
        var capturer = new FakeCapturer();
        var svc = Service(capturer);
        var cfg = Hosts("design.example.com");
        await svc.CaptureLinksAsync(7, cfg, new[] { Src("https://design.example.com/ok") });
        await svc.CaptureLinksAsync(7, cfg, new[] { Src("https://design.example.com/ok") });
        Assert.Single(capturer.Asked);

        capturer.Answer = _ => new PageResult(false, "the page did not finish loading in 30s", null);
        await svc.CaptureLinksAsync(7, cfg, new[] { Src("https://design.example.com/slow") });
        await svc.CaptureLinksAsync(7, cfg, new[] { Src("https://design.example.com/slow") });
        Assert.Equal(2, capturer.Asked.Count);

        // Forced - the drawer's "Capture again" - fetches everything, the good one too.
        await svc.CaptureLinksAsync(7, cfg, new[] { Src("https://design.example.com/ok https://design.example.com/slow") }, force: true);
        Assert.Equal(4, capturer.Asked.Count);
    }

    [Fact]
    public async Task A_capture_whose_link_has_gone_from_the_ticket_is_deleted()
    {
        var svc = Service(new FakeCapturer());
        var cfg = Hosts("design.example.com");
        await svc.CaptureLinksAsync(7, cfg, new[] { Src("https://design.example.com/old") });
        await svc.CaptureLinksAsync(7, cfg, new[] { Src("https://design.example.com/new") });

        var one = Assert.Single(new IssueAttachmentStore(_paths).ListCached(7));
        Assert.Equal("https://design.example.com/new", one.CapturedFrom);
        Assert.Single(Directory.GetFiles(PrototypeCaptureService.DirFor(_paths, 7), "*.png"));
    }

    [Fact]
    public async Task Links_past_the_cap_are_named_not_fetched()
    {
        var capturer = new FakeCapturer();
        var cfg = new PrototypeCaptureConfig(new[] { "design.example.com" }, Array.Empty<string>(), 1);
        var got = await Service(capturer).CaptureLinksAsync(7, cfg,
            new[] { Src("https://design.example.com/a https://design.example.com/b") });

        Assert.Single(capturer.Asked);
        Assert.Contains("1-link cap", Assert.Single(got, e => !e.Ok).Error);
    }

    [Fact]
    public async Task An_isolated_QA_copy_refuses_a_page_off_this_machine_but_may_capture_its_own()
    {
        var capturer = new FakeCapturer();
        var got = await Service(capturer, isolated: true).CaptureLinksAsync(7, Hosts("design.example.com", "localhost:5399"),
            new[] { Src("https://design.example.com/p and http://localhost:5399/proto") });

        Assert.Equal(new[] { "http://localhost:5399/proto" }, capturer.Asked);
        var refused = Assert.Single(got, e => !e.Ok);
        Assert.Equal("https://design.example.com/p", refused.Url);
        Assert.Contains(QaIsolation.EnvVar, refused.Error);
    }

    [Fact]
    public void Sources_cover_the_description_every_comment_and_each_readable_ancestor()
    {
        var issue = Issue.Empty("PROJ-2") with
        {
            Description = "desc",
            Comments = new[] { new IssueComment("10", "Someone", null, "c1") },
        };
        var chain = new[]
        {
            new IssueContextBuilder.AncestorDto(1, "PROJ-2", "PROJ-1", null, null, null, "parent text", true,
                Array.Empty<IssueContextBuilder.AttachmentDto>(), Array.Empty<IssueContextBuilder.NotCachedDto>()),
            new IssueContextBuilder.AncestorDto(2, "PROJ-1", "PROJ-0", null, null, null, null, false,
                Array.Empty<IssueContextBuilder.AttachmentDto>(), Array.Empty<IssueContextBuilder.NotCachedDto>()),
        };
        var t = new Whetstone.Models.Ticket { ProjectKey = "alpha", Description = "local" };

        var codes = PrototypeCaptureService.SourcesOf(t, issue, chain).Select(s => s.Code).ToArray();
        Assert.Equal(new[] { "ticket", "description", "comment-10", "parent-PROJ-1" }, codes);
    }

    [Fact]
    public async Task A_capture_travels_to_a_worker_host_with_the_tickets_other_files()
    {
        await Service(new FakeCapturer()).CaptureLinksAsync(7, Hosts("design.example.com"),
            new[] { Src("[Prototype](https://design.example.com/p)") });

        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(HostDataMirror.BuildTicketData(_paths, 7)));
        Assert.Contains(zip.Entries, e => e.FullName.StartsWith("issue-attachments/7/prototype-captures/") && e.FullName.EndsWith(".png"));
    }
}
