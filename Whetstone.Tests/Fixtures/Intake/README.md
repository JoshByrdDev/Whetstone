# Intake fixtures

Shaped after each vendor's documented format, with placeholder values only - no real project's data.

- `sentry-issue.json`: `{ "issue": ..., "event": ... }` as a project's forwarder puts it on its outbox, the
  issue as Sentry's "Retrieve an Issue" API returns it and the event as "Retrieve the Latest Event for
  an Issue" does (`entries`, `lineNo`, `inApp`, tags as `{ key, value }`).
- `sentry-issue-raw-event.json`: the same, with the event in the raw event payload's shape
  (`exception.values`, `lineno`, `in_app`, tags as `[key, value]` pairs), and the issue regressed.
- `cloudwatch-alarm-sns.json`: an SNS notification envelope as SQS receives it without raw delivery,
  its `Message` a CloudWatch alarm state change to `ALARM`.
- `cloudwatch-ok-raw.json`: the same alarm's change back to `OK`, delivered raw (no envelope).
