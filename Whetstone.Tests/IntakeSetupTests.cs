using Whetstone.Services;
using Whetstone.Services.Intake;
using Xunit;
using static Whetstone.Services.SetupCheckService;

namespace Whetstone.Tests;

/// <summary>The Setup check's intake row, and the Settings page's writer for the block it reads.</summary>
public class IntakeSetupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "whetstone-intake-setup-" + Guid.NewGuid().ToString("N")[..8]);
    public IntakeSetupTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    private static IntakeSourceState Polled(string? error = null, int? waiting = 0) =>
        new(DateTime.UtcNow, error is null ? DateTime.UtcNow : null, error, new IntakeQueueStatus(waiting, 0, 1209600), null);

    [Fact]
    public void A_source_that_answers_is_ok()
    {
        var item = IntakeItem(new IntakeSetup("alpha", true, [], [("prod", Polled(waiting: 3))]));
        Assert.Equal(("intake-alpha", "ok"), (item.Key, item.State));
        Assert.Contains("prod: answers, 3 waiting", item.Detail);
        Assert.False(item.Required);
    }

    [Fact]
    public void A_failing_credential_and_a_backing_up_queue_are_warnings()
    {
        Assert.Equal("warn", IntakeItem(new IntakeSetup("alpha", true, [], [("prod", Polled(error: "The security token included in the request is invalid."))])).State);
        var backed = IntakeItem(new IntakeSetup("alpha", true, [], [("prod", Polled(waiting: 500))]));
        Assert.Equal("warn", backed.State);
        Assert.Contains("backing up", backed.Detail);
        Assert.Equal("warn", IntakeItem(new IntakeSetup("alpha", true, ["Source prod is missing region."], [])).State);
    }

    [Fact]
    public void The_check_reaches_no_queue_itself()
    {
        var probe = new Probe(_ => "x", _ => "x", (_, _) => "x", () => null, () => null, () => true,
            Intake: () => [new IntakeSetup("alpha", true, [], [("prod", null)])]);
        var item = Assert.Single(Evaluate(probe, [], []), i => i.Key == "intake-alpha");
        Assert.Contains("not polled yet", item.Detail);
    }

    [Fact]
    public void The_writer_keeps_every_other_key_and_a_blank_secret_keeps_the_saved_one()
    {
        var path = Path.Combine(_dir, "project.json");
        File.WriteAllText(path, """{ "projectPath": "/x/alpha", "intake": { "enabled": true, "sources": [ { "name": "prod", "kind": "sqs", "queueUrl": "q", "region": "us-east-1", "accessKeyId": "AK", "secretAccessKey": "SECRET-1234" } ] } }""");
        var (saved, problems) = IntakeConfigWriter.Write(path, new IntakeConfigInput(true,
            [new IntakeSourceInput("prod", "sqs", "q2", "us-east-1", "AK", "", 90, null)], 3, 9, "docs/runbook.md"));
        Assert.Empty(problems);
        Assert.NotNull(saved);
        var cfg = IntakeConfig.FromFile(path);
        Assert.Equal(("q2", "SECRET-1234", 90), (cfg.Sources[0].QueueUrl, cfg.Sources[0].SecretAccessKey, cfg.Sources[0].PollSeconds));
        Assert.Equal(new IntakeLimits(3, 9), cfg.Limits);
        Assert.Contains("\"projectPath\": \"/x/alpha\"", File.ReadAllText(path));
    }

    [Fact]
    public void The_writer_refuses_a_block_that_would_not_read()
    {
        var path = Path.Combine(_dir, "project.json");
        File.WriteAllText(path, "{}");
        var (saved, problems) = IntakeConfigWriter.Write(path, new IntakeConfigInput(true,
            [new IntakeSourceInput("prod", "sqs", "", "", "", "", null, null)], null, null, null));
        Assert.Null(saved);
        Assert.NotEmpty(problems);
        Assert.Equal("{}", File.ReadAllText(path));
    }
}
