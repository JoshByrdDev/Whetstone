using System.IO.Compression;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// A ticket's files kept at the same paths under each machine's data root (docs/worker-hosts-plan.md,
/// phase 2): what a worker host is sent, what it may send back, and how a capture it names by its
/// own path is found on the board host. The refusals matter as much as the mapping - a path from
/// another machine must resolve to a capture here and never to anything else on this disk.
/// </summary>
public class HostDataMirrorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "whetstone-mirror-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly DashboardPaths _paths;

    public HostDataMirrorTests()
    {
        _paths = new DashboardPaths(Path.Combine(_root, "board-data"));
    }

    public void Dispose()
    {
        QaShotStore.HostCaptureWait = TimeSpan.FromSeconds(20);
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData("shot.png", "shot.png")]
    [InlineData("sub/shot.JPG", "sub/shot.JPG")]
    [InlineData("sub\\shot.webp", "sub/shot.webp")]
    [InlineData("../shot.png", null)]
    [InlineData("a/../../shot.png", null)]
    [InlineData("shot.exe", null)]
    [InlineData("shot", null)]
    [InlineData("", null)]
    [InlineData("a//shot.png", null)]
    public void A_capture_name_is_an_image_by_a_plain_relative_path(string given, string? want) =>
        Assert.Equal(want, HostDataMirror.SafeCaptureName(given));

    [Fact]
    public void A_capture_named_under_a_worker_hosts_root_is_found_under_the_boards()
    {
        var hostRoots = new[] { @"D:\Whetstone\data" };
        Assert.Equal(Path.Combine(_paths.DataRoot, "qa-captures", "12", "shot.png"),
            HostDataMirror.ToBoardCapture(@"D:\Whetstone\data\qa-captures\12\shot.png", hostRoots, _paths.DataRoot));
        Assert.Equal(Path.Combine(_paths.DataRoot, "qa-captures", "12", "sub", "shot.png"),
            HostDataMirror.ToBoardCapture("d:/whetstone/data/qa-captures/12/sub/shot.png", hostRoots, _paths.DataRoot));
    }

    [Theory]
    [InlineData(@"D:\Whetstone\data\ticket-files\12\secret.png")]   // not a capture folder
    [InlineData(@"D:\Whetstone\data\qa-captures\x\shot.png")]       // not a ticket id
    [InlineData(@"D:\Whetstone\data\qa-captures\12\..\..\config.json")]
    [InlineData(@"D:\Whetstone\data\qa-captures\12\notes.txt")]
    [InlineData(@"E:\elsewhere\qa-captures\12\shot.png")]           // not a worker host's root
    public void Nothing_else_on_another_machine_maps_to_this_one(string path) =>
        Assert.Null(HostDataMirror.ToBoardCapture(path, new[] { @"D:\Whetstone\data" }, _paths.DataRoot));

    [Fact]
    public void A_worker_host_is_sent_a_tickets_own_files_and_no_others()
    {
        Directory.CreateDirectory(_paths.TicketFilesTicketDir(12));
        File.WriteAllText(Path.Combine(_paths.TicketFilesTicketDir(12), "log.txt"), "owner's attachment");
        Directory.CreateDirectory(_paths.IssueAttachmentAncestorDir(12, "PROJ-1"));
        File.WriteAllText(Path.Combine(_paths.IssueAttachmentAncestorDir(12, "PROJ-1"), "mock.png"), "x");
        Directory.CreateDirectory(_paths.TicketFilesTicketDir(13));
        File.WriteAllText(Path.Combine(_paths.TicketFilesTicketDir(13), "other.txt"), "another ticket's");

        using var zip = new ZipArchive(new MemoryStream(HostDataMirror.BuildTicketData(_paths, 12)));
        Assert.Equal(new[] { "issue-attachments/12/from-PROJ-1/mock.png", "ticket-files/12/log.txt" },
            zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void A_ticket_with_no_files_is_an_empty_zip()
    {
        using var zip = new ZipArchive(new MemoryStream(HostDataMirror.BuildTicketData(_paths, 99)));
        Assert.Empty(zip.Entries);
    }

    private WorkerHostService HostsReporting(string dataRoot)
    {
        var cfg = new[] { new WorkerHostConfig("bravo-host", true, 2, WorkerHostConfig.DefaultSkills, new[] { "alpha" }, 5) };
        var hosts = new WorkerHostService(() => cfg, () => "c", () => DateTime.UtcNow);
        hosts.CheckInAsync("bravo-host", "c", Array.Empty<HostRun>(), null, TimeSpan.Zero, default, dataRoot).GetAwaiter().GetResult();
        return hosts;
    }

    private static byte[] Png => new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };

    [Fact]
    public async Task Set_qa_shots_takes_a_worker_hosts_capture_by_its_own_path()
    {
        var hosts = HostsReporting(@"D:\Whetstone\data");
        Assert.Equal(new[] { @"D:\Whetstone\data" }, hosts.HostDataRoots());
        Directory.CreateDirectory(_paths.QaCaptureTicketDir(12));
        File.WriteAllBytes(Path.Combine(_paths.QaCaptureTicketDir(12), "shot.png"), Png);

        var store = new QaShotStore(_paths, hosts);
        var stored = await store.SaveAsync(12, 0, @"D:\Whetstone\data\qa-captures\12\shot.png");
        Assert.Equal("0.png", stored);
        Assert.Equal(Png, File.ReadAllBytes(store.DiskPath(12, stored)!));
    }

    [Fact]
    public async Task A_capture_still_on_its_way_is_waited_for()
    {
        QaShotStore.HostCaptureWait = TimeSpan.FromSeconds(10);
        var store = new QaShotStore(_paths, HostsReporting(@"D:\Whetstone\data"));
        var save = store.SaveAsync(12, 0, @"D:\Whetstone\data\qa-captures\12\late.png");

        await Task.Delay(700);
        Directory.CreateDirectory(_paths.QaCaptureTicketDir(12));
        File.WriteAllBytes(Path.Combine(_paths.QaCaptureTicketDir(12), "late.png"), Png);
        Assert.Equal("0.png", await save);
    }

    [Fact]
    public async Task A_capture_that_never_arrives_says_so()
    {
        QaShotStore.HostCaptureWait = TimeSpan.FromMilliseconds(600);
        var store = new QaShotStore(_paths, HostsReporting(@"D:\Whetstone\data"));
        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => store.SaveAsync(12, 0, @"D:\Whetstone\data\qa-captures\12\never.png"));
        Assert.Contains("worker host", ex.Message);
    }

    [Fact]
    public async Task A_board_host_run_is_unchanged()
    {
        var src = Path.Combine(_root, "local.png");
        File.WriteAllBytes(src, Png);
        Assert.Equal("0.png", await new QaShotStore(_paths).SaveAsync(12, 0, src));
        await Assert.ThrowsAsync<FileNotFoundException>(() => new QaShotStore(_paths).SaveAsync(12, 1, @"D:\Whetstone\data\qa-captures\12\shot.png"));
    }
}
