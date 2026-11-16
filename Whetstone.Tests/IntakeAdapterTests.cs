using Whetstone.Models;
using Whetstone.Services.Intake;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The two adapters against fixtures shaped after each vendor's documented format
/// (Fixtures/Intake/README.md): what is recognised, what the fingerprint and state are, and what
/// of the report reaches the ticket.
/// </summary>
public class IntakeAdapterTests
{
    public static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Intake", name));

    public static IntakeEnvelope Sentry(string file = "sentry-issue.json", string? id = null) =>
        new(id ?? Guid.NewGuid().ToString("N"), "rh", Fixture(file), new Dictionary<string, string> { ["kind"] = "sentry-issue" }, DateTime.UtcNow);

    public static IntakeEnvelope Plain(string body, string? id = null) =>
        new(id ?? Guid.NewGuid().ToString("N"), "rh", body, new Dictionary<string, string>(), DateTime.UtcNow);

    [Fact]
    public void A_sentry_issue_from_the_API_is_one_report_with_its_stack_at_file_and_line()
    {
        var (r, problem) = IntakeAdapters.Read(Sentry());
        Assert.Null(problem);
        Assert.NotNull(r);
        Assert.Equal(("sentry-issue", IntakeChannels.Sentry, "4510000001", IntakeStates.Firing), (r!.Adapter, r.Channel, r.Fingerprint, r.State));
        Assert.Equal("TypeError: Cannot read properties of null (reading 'id') in bookVisit", r.Title);
        Assert.Equal(17, r.Count);
        Assert.Equal("3f9c2e1b7a6d5c4b3a291807f6e5d4c3b2a19087", r.Release);
        Assert.Equal("https://example-org.sentry.io/issues/4510000001/", r.OwnerLink);
        Assert.True(r.IsBug);
        Assert.False(r.IsTest);
        Assert.Equal(TicketPriority.Normal, r.Severity);
        Assert.Equal(new DateTime(2026, 11, 16, 14, 41, 7, 512, DateTimeKind.Utc), r.OccurredAt);
        // In-app frames, most recent call first, and the framework frame left out.
        var visit = r.Body.IndexOf("`src/booking/visit.ts:42` in `bookVisit`", StringComparison.Ordinal);
        var page = r.Body.IndexOf("`src/booking/BookingPage.tsx:118` in `onConfirm`", StringComparison.Ordinal);
        Assert.True(visit > 0 && page > visit, r.Body);
        Assert.DoesNotContain("react-dom", r.Body);
        Assert.Contains("**Transaction:** `/visits/book`", r.Body);
        Assert.Contains("**Step:** `confirm`", r.Body);
        Assert.Contains("**Environment:** `production`", r.Body);
        Assert.Contains("17 times", r.Body);
        // The owner's link is the ticket's to place, in the owner's part; never in the problem text.
        Assert.DoesNotContain("sentry.io", r.Body);
    }

    [Fact]
    public void A_raw_event_is_read_too_and_a_regressed_substatus_is_a_regression()
    {
        var (r, _) = IntakeAdapters.Read(Sentry("sentry-issue-raw-event.json"));
        Assert.NotNull(r);
        Assert.Equal(IntakeStates.Regressed, r!.State);
        Assert.Equal("alpha-api@9a8b7c6d", r.Release);
        Assert.Equal(TicketPriority.High, r.Severity);
        Assert.True(r.IsTest);
        // The chain newest first: what was raised last is what the reader meets first.
        Assert.True(r.Body.IndexOf("InvalidOperationException", StringComparison.Ordinal)
            < r.Body.IndexOf("ArgumentNullException", StringComparison.Ordinal));
        Assert.Contains("`Billing/InvoiceService.cs:77` in `Close`", r.Body);
        Assert.Contains("**Route:** `POST /invoices/{id}/close`", r.Body);
    }

    [Fact]
    public void A_sentry_body_without_the_attribute_is_not_a_sentry_issue()
    {
        var (r, problem) = IntakeAdapters.Read(Plain(Fixture("sentry-issue.json")));
        Assert.Null(r);
        Assert.Null(problem);
    }

    [Fact]
    public void A_sentry_issue_with_no_id_is_recognised_and_said_to_be_unreadable()
    {
        var (r, problem) = IntakeAdapters.Read(new IntakeEnvelope("m", "rh", """{ "issue": {}, "event": {} }""",
            new Dictionary<string, string> { ["kind"] = "sentry-issue" }, null));
        Assert.Null(r);
        Assert.Contains("no id", problem);
    }

    [Fact]
    public void An_alarm_in_an_SNS_envelope_is_firing_with_its_reason_and_threshold()
    {
        var (r, _) = IntakeAdapters.Read(Plain(Fixture("cloudwatch-alarm-sns.json")));
        Assert.NotNull(r);
        Assert.Equal((IntakeChannels.CloudWatchAlarm, "alpha-api-5xx", IntakeStates.Firing), (r!.Channel, r.Fingerprint, r.State));
        Assert.Equal("Alarm alpha-api-5xx: More than five 5xx responses from the API in a minute", r.Title);
        Assert.False(r.IsBug);
        Assert.Equal(new DateTime(2026, 11, 16, 14, 5, 12, 345, DateTimeKind.Utc), r.OccurredAt);
        Assert.Contains("Threshold Crossed", r.Body);
        Assert.Contains("SUM of `AWS/ApiGateway/5XXError` (ApiName=alpha-api) > 5", r.Body);
        Assert.Equal("https://us-east-1.console.aws.amazon.com/cloudwatch/home?region=us-east-1#alarmsV2:alarm/alpha-api-5xx", r.OwnerLink);
    }

    [Fact]
    public void An_alarm_delivered_raw_back_to_OK_is_cleared()
    {
        var (r, _) = IntakeAdapters.Read(Plain(Fixture("cloudwatch-ok-raw.json")));
        Assert.Equal(IntakeStates.Cleared, r!.State);
        Assert.Equal("alpha-api-5xx", r.Fingerprint);
    }

    [Fact]
    public void Insufficient_data_is_read_and_never_firing()
    {
        var body = Fixture("cloudwatch-ok-raw.json").Replace("\"NewStateValue\": \"OK\"", "\"NewStateValue\": \"INSUFFICIENT_DATA\"");
        var (r, _) = IntakeAdapters.Read(Plain(body));
        Assert.Equal(IntakeStates.Unknown, r!.State);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{ "Type": "Notification", "Message": "a plain text notification" }""")]
    [InlineData("""{ "hello": "world" }""")]
    public void Anything_else_is_unrecognised(string body)
    {
        var (r, problem) = IntakeAdapters.Read(Plain(body));
        Assert.Null(r);
        Assert.Null(problem);
    }
}
