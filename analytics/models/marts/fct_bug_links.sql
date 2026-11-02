-- One cause suggested for one bug - suggested, confirmed or rejected - with the bug, the ticket
-- that wrote the blamed lines, the run that most likely did, and what fixing the bug cost.
--
-- Only a confirmed link counts as a bug (is_confirmed). An unmapped link (no source ticket: the
-- lines came from outside the board) is kept, so a trace that blamed only those does not look empty.
with links as (
    select * from {{ ref('stg_board__bug_links') }}
),
tickets as (
    select * from {{ ref('stg_board__tickets') }}
),
written as (
    select * from {{ ref('int_bug_link_runs') }}
),
runs as (
    select * from {{ ref('int_runs') }}
),
fix as (
    select ticket_id, sum(minutes) as minutes, sum(tokens) as tokens
    from runs group by ticket_id
)

select
    l.link_id,
    l.bug_ticket_id,
    b.title                                                         as bug_title,
    b.created_at                                                    as bug_created_at,
    l.project_key,
    coalesce(b.track, 'Delivery')                                   as track,
    l.source_ticket_id,
    s.title                                                         as source_title,
    l.status,
    l.status = 'Confirmed'                                          as is_confirmed,
    l.origin,
    l.lines,
    l.share,
    l.neighbours,
    l.escaped,
    coalesce(l.found_by, 'unknown')                                 as found_by,
    coalesce(l.missed_by, 'unknown')                                as missed_by,
    l.traced_at,
    l.decided_at,
    w.source_delivered_at,
    -- How long the bug went unnoticed: from the source's delivery to the bug being filed.
    case when w.source_delivered_at <= b.created_at
         then datediff('second', w.source_delivered_at, b.created_at) / 86400.0 end as days_to_find,
    w.written_by_run_id,
    coalesce(w.candidate_runs, 0)                                   as candidate_runs,
    coalesce(w.run_tier, 'none')                                    as run_tier,
    r.phase                                                         as written_phase,
    r.skill                                                         as written_skill,
    r.model                                                         as written_model,
    r.provider                                                      as written_provider,
    r.effort                                                        as written_effort,
    r.skill_version                                                 as written_skill_version,
    r.prompt_version                                                as written_prompt_version,
    r.experiment_arm                                                as written_experiment_arm,
    coalesce(f.minutes, 0)                                          as bug_fix_minutes,
    coalesce(f.tokens, 0)                                           as bug_fix_tokens,
    -- The fix's cost shared between its causes by their share of the blamed lines.
    coalesce(f.minutes, 0) * coalesce(l.share, 0)                   as attributed_fix_minutes
from links l
left join tickets b on b.ticket_id = l.bug_ticket_id
left join tickets s on s.ticket_id = l.source_ticket_id
left join written w using (link_id)
left join runs r on r.run_id = w.written_by_run_id
left join fix f on f.ticket_id = l.bug_ticket_id
