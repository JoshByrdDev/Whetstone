-- Bug tickets by where they came from (docs/intake-plan.md, section 6): how many were filed, how
-- many reached Done and how long that took, and how many were regressions - per week the bug was
-- filed, and over the whole window (week = 'all'). The answer to "how many bugs came from
-- production reports this month, against how many were found in-house".
--
-- `channel` is the intake channel (sentry, cloudwatch-alarm, storm, unrecognised) on an intake
-- ticket, and '' on every other: a person's, a steward's, a run's, a regression filed by hand.
-- A regression intake filed counts under intake, with `regressions` saying so.
with t as (
    select *,
        created_at                                                  as started_at,
        strftime(date_trunc('week', created_at), '%Y-%m-%d')        as week
    from {{ ref('fct_tickets') }}
    where is_bug and created_at is not null
),
s as (
    {{ scoped('t') }}
),
cut as (
    select *, week as week_of from s
    union all
    select *, 'all' as week_of from s
)

select
    window_days, project_scope, track_scope,
    week_of                                                         as week,
    origin,
    coalesce(intake_channel, '')                                    as channel,
    count(*)                                                        as filed,
    count(*) filter (where reached_done)                            as done,
    median(hours_to_done) filter (where reached_done)               as median_hours_to_done,
    count(*) filter (where regression_of is not null)               as regressions
from cut
group by all
