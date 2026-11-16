using System.Collections.Concurrent;
using Amazon;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;

namespace Whetstone.Services.Intake;

/// <summary>One message as a transport hands it over, before any adapter has looked at it.</summary>
public sealed record IntakeEnvelope(string MessageId, string ReceiptHandle, string Body,
    IReadOnlyDictionary<string, string> Attributes, DateTime? SentAt);

/// <summary>What a queue says about itself: the test-connection answer, and whether it is backing up.</summary>
public sealed record IntakeQueueStatus(int? Waiting, int? InFlight, int? RetentionSeconds);

/// <summary>
/// A queue intake reads (docs/intake-plan.md, section 2). A second kind of queue is a class
/// behind this, never a branch in the poller.
/// </summary>
public interface IIntakeTransport
{
    /// <summary>Up to <paramref name="max"/> messages, possibly none. A message stays on the queue until deleted.</summary>
    Task<IReadOnlyList<IntakeEnvelope>> ReceiveAsync(int max, CancellationToken ct);

    /// <summary>Acknowledge one message, once what it caused is committed.</summary>
    Task DeleteAsync(IntakeEnvelope message, CancellationToken ct);

    /// <summary>The queue's attributes, and nothing else: what "Test connection" calls.</summary>
    Task<IntakeQueueStatus> StatusAsync(CancellationToken ct);
}

/// <summary>
/// An SQS queue. The credential the project's half makes can receive, delete and read this one
/// queue's attributes; those are the only three calls here, so nothing in the board asks it for more.
/// </summary>
public sealed class SqsIntakeTransport : IIntakeTransport, IDisposable
{
    private readonly IAmazonSQS _sqs;
    private readonly string _queueUrl;

    public SqsIntakeTransport(IntakeSource source)
    {
        var creds = new BasicAWSCredentials(source.AccessKeyId, source.SecretAccessKey);
        var cfg = new AmazonSQSConfig();
        if (source.ServiceUrl.Length > 0)
        {
            cfg.ServiceURL = source.ServiceUrl;
            cfg.AuthenticationRegion = source.Region.Length > 0 ? source.Region : "us-east-1";
        }
        else cfg.RegionEndpoint = RegionEndpoint.GetBySystemName(source.Region);
        _sqs = new AmazonSQSClient(creds, cfg);
        _queueUrl = source.QueueUrl;
    }

    public async Task<IReadOnlyList<IntakeEnvelope>> ReceiveAsync(int max, CancellationToken ct)
    {
        var r = await _sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = _queueUrl,
            MaxNumberOfMessages = Math.Clamp(max, 1, 10),
            // A short long-poll: an empty queue answers in a few seconds rather than at once, which
            // is what SQS bills by, without holding a pass of the poller for long.
            WaitTimeSeconds = 5,
            MessageAttributeNames = ["All"],
            MessageSystemAttributeNames = ["SentTimestamp"],
        }, ct);
        return (r.Messages ?? []).Select(m => new IntakeEnvelope(
            m.MessageId, m.ReceiptHandle, m.Body ?? "",
            (m.MessageAttributes ?? new()).Where(a => a.Value.StringValue is not null)
                .ToDictionary(a => a.Key, a => a.Value.StringValue!, StringComparer.OrdinalIgnoreCase),
            m.Attributes is { } sys && sys.TryGetValue("SentTimestamp", out var ts) && long.TryParse(ts, out var ms)
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : null)).ToList();
    }

    public Task DeleteAsync(IntakeEnvelope message, CancellationToken ct) =>
        _sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = _queueUrl, ReceiptHandle = message.ReceiptHandle }, ct);

    public async Task<IntakeQueueStatus> StatusAsync(CancellationToken ct)
    {
        var r = await _sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = _queueUrl,
            AttributeNames = ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible", "MessageRetentionPeriod"],
        }, ct);
        int? Get(string k) => r.Attributes is { } a && a.TryGetValue(k, out var v) && int.TryParse(v, out var n) ? n : null;
        return new IntakeQueueStatus(Get("ApproximateNumberOfMessages"), Get("ApproximateNumberOfMessagesNotVisible"),
            Get("MessageRetentionPeriod"));
    }

    public void Dispose() => _sqs.Dispose();
}

/// <summary>
/// A queue in this process's memory: what the tests drive, and what a QA copy files intake tickets
/// from. Delivery is at least once, like SQS: a message received and not deleted comes back on the
/// next receive, and <see cref="Redeliver"/> sends one again outright.
/// </summary>
public sealed class InMemoryIntakeTransport : IIntakeTransport
{
    private readonly ConcurrentQueue<IntakeEnvelope> _waiting = new();
    private readonly ConcurrentDictionary<string, IntakeEnvelope> _inFlight = new();
    private int _seq;

    public List<string> Deleted { get; } = [];

    public IntakeEnvelope Send(string body, IReadOnlyDictionary<string, string>? attributes = null, string? messageId = null)
    {
        var id = messageId ?? $"standin-{Interlocked.Increment(ref _seq)}-{Guid.NewGuid():N}"[..32];
        var m = new IntakeEnvelope(id, "rh-" + Guid.NewGuid().ToString("N"), body,
            attributes ?? new Dictionary<string, string>(), DateTime.UtcNow);
        _waiting.Enqueue(m);
        return m;
    }

    /// <summary>The same message delivered again, as SQS may do after a delete is lost.</summary>
    public void Redeliver(IntakeEnvelope m) => _waiting.Enqueue(m with { ReceiptHandle = "rh-" + Guid.NewGuid().ToString("N") });

    public Task<IReadOnlyList<IntakeEnvelope>> ReceiveAsync(int max, CancellationToken ct)
    {
        // What was received last time and never deleted is visible again.
        foreach (var k in _inFlight.Keys.ToList())
            if (_inFlight.TryRemove(k, out var back)) _waiting.Enqueue(back);
        var got = new List<IntakeEnvelope>();
        while (got.Count < max && _waiting.TryDequeue(out var m)) { got.Add(m); _inFlight[m.ReceiptHandle] = m; }
        return Task.FromResult<IReadOnlyList<IntakeEnvelope>>(got);
    }

    public Task DeleteAsync(IntakeEnvelope message, CancellationToken ct)
    {
        _inFlight.TryRemove(message.ReceiptHandle, out _);
        lock (Deleted) Deleted.Add(message.MessageId);
        return Task.CompletedTask;
    }

    public Task<IntakeQueueStatus> StatusAsync(CancellationToken ct) =>
        Task.FromResult(new IntakeQueueStatus(_waiting.Count, _inFlight.Count, 14 * 24 * 3600));
}

/// <summary>
/// The transport for each configured source. The stand-ins live as long as the process, one per
/// project and source, so a route can put a message on one and the next pass finds it. A QA copy
/// is handed no SQS transport at all: it refuses here, behind the route table.
/// </summary>
public sealed class IntakeTransports(QaIsolation isolation)
{
    private readonly ConcurrentDictionary<string, InMemoryIntakeTransport> _standIns = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Set by a test to hand back its own transport for a real kind of source.</summary>
    public Func<string, IntakeSource, IIntakeTransport?>? Override { get; set; }

    public InMemoryIntakeTransport StandIn(string project, string source) =>
        _standIns.GetOrAdd($"{project}/{source}", _ => new InMemoryIntakeTransport());

    /// <summary>The transport for a source, or why there is none here.</summary>
    public (IIntakeTransport? Transport, string? Refused) For(string project, IntakeSource source)
    {
        if (Override?.Invoke(project, source) is { } o) return (o, null);
        if (source.IsStandIn) return (StandIn(project, source.Name), null);
        if (isolation.On) return (null, QaIsolation.Refusal("reading a project's intake queue"));
        return (new SqsIntakeTransport(source), null);
    }
}
