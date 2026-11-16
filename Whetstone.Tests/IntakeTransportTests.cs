using System.Net.Sockets;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using Whetstone.Services;
using Whetstone.Services.Intake;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// Intake's config and its queues (docs/intake-plan.md, sections 2 and 9): the credential is read
/// from the machine-local file only, a QA copy is never handed a real queue, and the stand-in
/// behaves the way SQS does where the poller depends on it - at least once.
/// </summary>
public class IntakeTransportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "whetstone-intake-" + Guid.NewGuid().ToString("N")[..8]);

    public IntakeTransportTests() => Directory.CreateDirectory(Path.Combine(_root, "projects", "alpha"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private const string Sqs = """
        { "name": "prod", "kind": "sqs", "queueUrl": "https://sqs.us-east-1.amazonaws.com/000000000000/alpha-outbox",
          "region": "us-east-1", "accessKeyId": "AKIAEXAMPLE", "secretAccessKey": "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", "pollSeconds": 30 }
        """;

    private string Write(string file, string json)
    {
        var p = Path.Combine(_root, "projects", "alpha", file);
        File.WriteAllText(p, json);
        return p;
    }

    [Fact]
    public void A_source_is_read_with_its_limits_and_runbook()
    {
        var cfg = IntakeConfig.FromFile(Write("project.json",
            $$"""{ "intake": { "enabled": true, "sources": [ {{Sqs}} ], "limits": { "newPerHour": 2, "newPerDay": 7 }, "runbook": "docs/runbook.md" } }"""));
        Assert.True(cfg.IsOn);
        var s = Assert.Single(cfg.Sources);
        Assert.Equal(("prod", "sqs", 30), (s.Name, s.Kind, s.PollSeconds));
        Assert.Equal(new IntakeLimits(2, 7), cfg.Limits);
        Assert.Equal("docs/runbook.md", cfg.Runbook);
        Assert.Empty(cfg.Problems);
        Assert.EndsWith("EKEY", s.MaskedSecret);
        Assert.DoesNotContain("wJalr", s.MaskedSecret);
    }

    [Fact]
    public void The_committed_defaults_are_never_read_for_intake()
    {
        Write("project.defaults.json", $$"""{ "intake": { "enabled": true, "sources": [ {{Sqs}} ] } }""");
        var settings = new IntakeSettings(new ProjectRegistry(new DashboardPaths(_root)));
        Assert.False(settings.For("alpha").IsOn);
    }

    [Fact]
    public void A_source_missing_its_credential_is_a_problem_said_in_words()
    {
        var cfg = IntakeConfig.FromFile(Write("project.json",
            """{ "intake": { "enabled": true, "sources": [ { "name": "prod", "kind": "sqs", "queueUrl": "q", "region": "us-east-1" }, { "name": "x", "kind": "kafka" } ] } }"""));
        Assert.False(cfg.IsOn);
        Assert.Contains(cfg.Problems, p => p.Contains("accessKeyId") && p.Contains("secretAccessKey"));
        Assert.Contains(cfg.Problems, p => p.Contains("kafka"));
    }

    [Fact]
    public void Defaults_when_unsaid()
    {
        var cfg = IntakeConfig.FromFile(Write("project.json", """{ "intake": { "enabled": true, "sources": [ { "name": "local", "kind": "standin" } ] } }"""));
        Assert.Equal(IntakeLimits.Default, cfg.Limits);
        Assert.Equal(60, cfg.Sources[0].PollSeconds);
        Assert.True(cfg.Sources[0].IsStandIn);
    }

    [Fact]
    public void A_QA_copy_is_never_handed_a_real_queue()
    {
        var cfg = IntakeConfig.From(System.Text.Json.JsonDocument.Parse($$"""{ "enabled": true, "sources": [ {{Sqs}} ] }""").RootElement);
        var (t, refused) = new IntakeTransports(new QaIsolation(true)).For("alpha", cfg.Sources[0]);
        Assert.Null(t);
        Assert.Contains("isolated QA copy", refused);

        var standIn = cfg.Sources[0] with { Kind = IntakeSource.StandIn };
        var (s, none) = new IntakeTransports(new QaIsolation(true)).For("alpha", standIn);
        Assert.IsType<InMemoryIntakeTransport>(s);
        Assert.Null(none);
    }

    [Fact]
    public async Task The_stand_in_delivers_at_least_once()
    {
        var q = new InMemoryIntakeTransport();
        var a = q.Send("one");
        q.Send("two");
        var first = await q.ReceiveAsync(10, default);
        Assert.Equal(2, first.Count);
        await q.DeleteAsync(first[0], default);
        // Not deleted is seen again; deleted is not.
        var again = await q.ReceiveAsync(10, default);
        Assert.Equal("two", Assert.Single(again).Body);
        q.Redeliver(a);
        Assert.Contains(await q.ReceiveAsync(10, default), m => m.MessageId == a.MessageId);
    }

    /// <summary>
    /// The SQS class against a local SQS-compatible stand-in, when one is running: ElasticMQ on 9324
    /// or LocalStack on 4566. Skipped, saying so, when neither answers.
    /// </summary>
    [SqsStandInFact]
    public async Task The_SQS_class_receives_deletes_and_reads_attributes()
    {
        var url = SqsStandInFact.Url!;
        using var admin = new AmazonSQSClient(new BasicAWSCredentials("x", "x"),
            new AmazonSQSConfig { ServiceURL = url, AuthenticationRegion = "us-east-1" });
        var queue = (await admin.CreateQueueAsync("whetstone-intake-test-" + Guid.NewGuid().ToString("N")[..8])).QueueUrl;
        try
        {
            await admin.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = queue, MessageBody = "{\"hello\":1}",
                MessageAttributes = new() { ["kind"] = new MessageAttributeValue { DataType = "String", StringValue = "sentry-issue" } },
            });
            var t = new SqsIntakeTransport(new IntakeSource("local", "sqs", queue, "us-east-1", "x", "x", 60, url));
            var status = await t.StatusAsync(default);
            Assert.Equal(1, status.Waiting);
            var got = Assert.Single(await t.ReceiveAsync(10, default));
            Assert.Equal("sentry-issue", got.Attributes["kind"]);
            Assert.NotNull(got.SentAt);
            await t.DeleteAsync(got, default);
            Assert.Equal(0, (await t.StatusAsync(default)).InFlight ?? 0);
        }
        finally { await admin.DeleteQueueAsync(queue); }
    }
}

/// <summary>A fact that runs only when an SQS-compatible stand-in answers on this machine.</summary>
public sealed class SqsStandInFact : FactAttribute
{
    public static readonly string? Url = Find();

    public SqsStandInFact()
    {
        if (Url is null)
            Skip = "No SQS-compatible stand-in is running on localhost (ElasticMQ on 9324, LocalStack on 4566; "
                + "or set WHETSTONE_SQS_STANDIN_URL). Start one with: docker run --rm -p 9324:9324 softwaremill/elasticmq-native";
    }

    private static string? Find()
    {
        var env = Environment.GetEnvironmentVariable("WHETSTONE_SQS_STANDIN_URL");
        var candidates = string.IsNullOrWhiteSpace(env) ? new[] { "http://localhost:9324", "http://localhost:4566" } : new[] { env.Trim() };
        foreach (var c in candidates)
        {
            var u = new Uri(c);
            try
            {
                using var tcp = new TcpClient();
                if (tcp.ConnectAsync(u.Host, u.Port).Wait(300) && tcp.Connected) return c;
            }
            catch { /* not there */ }
        }
        return null;
    }
}
