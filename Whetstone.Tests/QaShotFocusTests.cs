using Whetstone.Api;
using Whetstone.Mcp;
using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// QA marks where in a screenshot the proof sits, so a walkthrough can zoom to it. The rectangle is
/// checked against the image's real size: one that misses is dropped and reported, and the shot is
/// kept - the screenshot is still proof even when the numbers drawn on it were wrong.
/// </summary>
public class QaShotFocusTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "whetstone-focus-" + Guid.NewGuid().ToString("N"));

    public QaShotFocusTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(_conn).Options;
        using var ctx = new DashboardDbContext(_options);
        ctx.Database.EnsureCreated();
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        _conn.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>A PNG header claiming the given size - all ImageInfo reads, and all SaveAsync copies.</summary>
    private string Png(int w, int h)
    {
        var b = new byte[33];
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(b, 0);
        b[16] = (byte)(w >> 24); b[17] = (byte)(w >> 16); b[18] = (byte)(w >> 8); b[19] = (byte)w;
        b[20] = (byte)(h >> 24); b[21] = (byte)(h >> 16); b[22] = (byte)(h >> 8); b[23] = (byte)h;
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(path, b);
        return path;
    }

    private async Task<int> Seed()
    {
        await using var ctx = new DashboardDbContext(_options);
        var t = new Ticket { ProjectKey = "alpha", Title = "t", Phase = TicketPhase.QaTesting };
        ctx.Tickets.Add(t);
        await ctx.SaveChangesAsync();
        return t.Id;
    }

    [Fact]
    public void Reads_png_size_from_the_header()
    {
        Assert.Equal((1280, 720), ImageInfo.Size(Png(1280, 720)));
    }

    [Theory]
    [InlineData(0, 0, 1280, 720, null)]
    [InlineData(668, 574, 530, 30, null)]
    [InlineData(1200, 700, 100, 30, "runs off")]
    [InlineData(-1, 0, 10, 10, "starts off")]
    [InlineData(10, 10, 0, 5, "no area")]
    public void A_rectangle_must_lie_on_the_image(int x, int y, int w, int h, string? problem)
    {
        var got = new ImageRect(x, y, w, h).ProblemWithin(1280, 720);
        if (problem is null) Assert.Null(got);
        else Assert.Contains(problem, got);
    }

    [Fact]
    public async Task A_focus_that_fits_is_stored_and_served()
    {
        var id = await Seed();
        await using var ctx = new DashboardDbContext(_options);
        var store = new QaShotStore(new DashboardPaths(_root));

        await TicketTools.SetQaShots(new TicketService(ctx), store, id,
            new[] { new QaShotInput(Png(1280, 720), "The new row.", new QaFocusInput(668, 574, 530, 30)) });

        await using var read = new DashboardDbContext(_options);
        var shot = await read.QaShots.SingleAsync(s => s.TicketId == id);
        Assert.Equal(new ImageRect(668, 574, 530, 30), shot.Focus());
        Assert.Equal(new ImageRect(668, 574, 530, 30), QaShotDto.From(id, shot).Focus);
    }

    [Fact]
    public async Task A_focus_off_the_image_is_dropped_but_the_shot_is_kept()
    {
        var id = await Seed();
        await using var ctx = new DashboardDbContext(_options);
        var store = new QaShotStore(new DashboardPaths(_root));

        var result = await TicketTools.SetQaShots(new TicketService(ctx), store, id,
            new[] { new QaShotInput(Png(1280, 720), "The new row.", new QaFocusInput(1200, 700, 200, 50)) });

        await using var read = new DashboardDbContext(_options);
        var shot = await read.QaShots.SingleAsync(s => s.TicketId == id);
        Assert.Null(shot.Focus());
        var errors = (List<string>)result.GetType().GetProperty("errors")!.GetValue(result)!;
        Assert.Contains(errors, e => e.Contains("runs off") && e.Contains("shot kept"));
    }

    [Fact]
    public async Task A_shot_without_focus_still_works()
    {
        var id = await Seed();
        await using var ctx = new DashboardDbContext(_options);
        await new TicketService(ctx).SetQaShotsAsync(id, new[] { ("0.png", "The whole screen.") });

        await using var read = new DashboardDbContext(_options);
        Assert.Null((await read.QaShots.SingleAsync(s => s.TicketId == id)).Focus());
    }
}
