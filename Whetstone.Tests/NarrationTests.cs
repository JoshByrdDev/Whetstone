using Microsoft.Extensions.Logging.Abstractions;
using Whetstone.Api;
using Whetstone.Services;
using Whetstone.Services.Walkthrough;
using Xunit;
using static Whetstone.Services.Walkthrough.WalkthroughDeck;

namespace Whetstone.Tests;

/// <summary>
/// When a walkthrough is voiced (<see cref="ProjectConfig.Narration"/>): on demand, writing a deck
/// voices nothing and reuses only what is already on disk; always, it voices as it is written; and a
/// clip that is not on this machine is served as a caption, which is what offers Generate.
/// </summary>
public class NarrationTests : IDisposable
{
    private readonly string _root;
    private readonly DashboardPaths _paths;

    public NarrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "whetstone-narration-" + Guid.NewGuid().ToString("N"));
        _paths = new DashboardPaths(_root);
        Directory.CreateDirectory(Path.Combine(_root, "projects", "alpha"));
        Directory.CreateDirectory(_paths.WalkthroughClipsDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }

    /// <summary>A narrator that records what it is asked to voice and writes a one-second clip for each.</summary>
    private sealed class CountingNarrator : Narrator
    {
        public int Calls;
        public CountingNarrator(DashboardPaths paths) : base(paths, NullLogger<Narrator>.Instance) { }

        public override Task<IReadOnlyList<Voiced>> VoiceAsync(IReadOnlyList<Line> lines, string voice,
            IRunProgress progress, CancellationToken ct)
        {
            Calls++;
            foreach (var l in lines) WriteWav(ClipPath(l.Id(voice)), 1);
            return Task.FromResult<IReadOnlyList<Voiced>>(lines.Select(l => new Voiced(l.Id(voice), 1, null)).ToList());
        }
    }

    private static void WriteWav(string path, int seconds)
    {
        const int rate = 24000, bytesPerSample = 2;
        var data = rate * bytesPerSample * seconds;
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8.ToArray()); w.Write(36 + data); w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * bytesPerSample); w.Write((short)bytesPerSample); w.Write((short)16);
        w.Write("data"u8.ToArray()); w.Write(data); w.Write(new byte[data]);
    }

    private static IReadOnlyList<Scene> Scenes(params string[] says) =>
        new[] { new Scene("Intro", "title", says.Select(s => new Beat(s, null)).ToList()) };

    private ProjectConfig Config(string? defaultsJson)
    {
        if (defaultsJson is not null) File.WriteAllText(_paths.ProjectConfigDefaults("alpha"), defaultsJson);
        return new ProjectRegistry(_paths).GetProjectConfig("alpha");
    }

    private Task<(IReadOnlyList<Scene> Scenes, string? Warning)> Write(Narrator narrator, ProjectConfig cfg, IReadOnlyList<Scene> scenes) =>
        WalkthroughVoicing.VoiceScenesAsync(narrator, _paths, "alpha", Narrator.DefaultVoice, scenes,
            generate: cfg.NarrateAlways, NoProgress.Instance, CancellationToken.None);

    // ---- the setting ----------------------------------------------------------------------------

    [Fact]
    public void A_project_that_does_not_say_voices_on_demand()
    {
        var cfg = Config("""{ "displayName": "Alpha" }""");
        Assert.Equal(NarrationModes.OnDemand, cfg.Narration);
        Assert.False(cfg.NarrateAlways);
    }

    [Theory]
    [InlineData("always", "always")]
    [InlineData("Always", "always")]
    [InlineData("onDemand", "onDemand")]
    [InlineData("sometimes", "onDemand")]
    public void Narration_is_read_from_the_project_defaults(string written, string read)
    {
        var cfg = Config($$"""{ "narration": "{{written}}" }""");
        Assert.Equal(read, cfg.Narration);
        Assert.Equal(read == "always", cfg.NarrateAlways);
    }

    // ---- writing a deck -------------------------------------------------------------------------

    [Fact]
    public async Task Writing_a_deck_on_an_on_demand_project_voices_nothing()
    {
        var narrator = new CountingNarrator(_paths);
        var (scenes, warning) = await Write(narrator, Config("""{ "narration": "onDemand" }"""),
            Scenes("The board now waits for the owner.", "Then it merges."));

        Assert.Equal(0, narrator.Calls);
        Assert.Empty(Directory.GetFiles(_paths.WalkthroughClipsDir, "*.wav"));
        Assert.Null(warning);   // on demand, an unvoiced line is not a failure
        Assert.All(scenes[0].Beats, b =>
        {
            Assert.Null(b.Clip);
            Assert.NotNull(b.Spoken);
            Assert.True(b.Dur >= 2.0);   // timed at a reading pace, so it plays as a caption
        });
    }

    [Fact]
    public async Task On_demand_still_uses_a_line_that_is_already_voiced()
    {
        var narrator = new CountingNarrator(_paths);
        var say = Pronunciation.ForProject(_paths, "alpha");
        var line = new Narrator.Line(Pronunciation.Sentences("Then it merges.").Select(say.Speak).ToList());
        WriteWav(narrator.ClipPath(line.Id(Narrator.DefaultVoice)), 3);

        var (scenes, _) = await Write(narrator, Config(null), Scenes("The board waits.", "Then it merges."));

        Assert.Equal(0, narrator.Calls);
        Assert.Null(scenes[0].Beats[0].Clip);
        Assert.Equal(line.Id(Narrator.DefaultVoice), scenes[0].Beats[1].Clip);
        Assert.Equal(3.0, scenes[0].Beats[1].Dur, 3);
    }

    [Fact]
    public async Task Writing_a_deck_on_an_always_project_voices_it()
    {
        var narrator = new CountingNarrator(_paths);
        var (scenes, warning) = await Write(narrator, Config("""{ "narration": "always" }"""),
            Scenes("The board waits.", "Then it merges."));

        Assert.Equal(1, narrator.Calls);
        Assert.Null(warning);
        Assert.All(scenes[0].Beats, b => { Assert.NotNull(b.Clip); Assert.Equal(1.0, b.Dur, 3); });
        Assert.Equal(2, Directory.GetFiles(_paths.WalkthroughClipsDir, "*.wav").Length);
    }

    [Fact]
    public async Task Generate_voices_an_on_demand_deck_and_reuses_what_is_there()
    {
        var narrator = new CountingNarrator(_paths);
        var (written, _) = await Write(narrator, Config(null), Scenes("The board waits.", "Then it merges."));

        var (voiced, warning) = await WalkthroughVoicing.VoiceScenesAsync(narrator, _paths, "alpha",
            Narrator.DefaultVoice, written, generate: true, NoProgress.Instance, CancellationToken.None);

        Assert.Equal(1, narrator.Calls);
        Assert.Null(warning);
        Assert.All(voiced[0].Beats, b => Assert.NotNull(b.Clip));
        Assert.Equal(2.0, WalkthroughVoicing.AudioSeconds(voiced), 3);
    }

    // ---- serving a deck -------------------------------------------------------------------------

    [Fact]
    public void A_clip_not_on_this_machine_is_served_as_a_caption_and_counted()
    {
        var deck = new Deck(Narrator.DefaultVoice, new[]
        {
            new Scene("Intro", "title", new[]
            {
                new Beat("here", null) { Clip = "aa", Dur = 1 },
                new Beat("gone", null) { Clip = "bb", Dur = 4 },
                new Beat("never", null) { Dur = 2 },
            }),
        });

        var (served, unvoiced) = WalkthroughEndpoints.OnThisMachine(deck, clip => clip == "aa");

        Assert.Equal(2, unvoiced);
        Assert.Equal(new string?[] { "aa", null, null }, served!.Scenes[0].Beats.Select(b => b.Clip));
        Assert.Equal(4, served.Scenes[0].Beats[1].Dur);   // the slot keeps its length
    }
}
