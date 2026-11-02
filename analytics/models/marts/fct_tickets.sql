-- One ticket: whether QA passed it first time, and what its runs cost in total.
--
-- First-pass QA is over the ticket's whole history, not a window, as on the board: "did QA ever
-- send this back" is not a question a date range can answer. A window applies to when QA first
-- saw it (first_qa_at).
with t as (
    select * from {{ ref('stg_board__tickets') }}
),
moves as (
    select * from {{ ref('stg_board__phase_transitions') }}
),
runs as (
    select * from {{ ref('int_runs') }}
),

qa as (
    select
        ticket_id,
        min(moved_at) filter (where to_phase = 'QaTesting')                         as first_qa_at,
        count(*) filter (where from_phase = 'QaTesting' and backward)          as qa_failures,
        count(*) filter (where backward)                                       as backward_moves,
        min(moved_at) filter (where to_phase = 'Done')                               as done_at
    from moves
    group by ticket_id
),

run_totals as (
    select
        ticket_id,
        count(*)                                                    as runs,
        sum(minutes)                                                as minutes,
        sum(minutes) filter (where rework_class = 'rework')         as rework_minutes,
        sum(tokens)                                                 as tokens,
        sum(tokens) filter (where rework_class = 'rework')          as rework_tokens,
        count(*) filter (where phase = 'Implementing')              as implement_runs,
        count(*) filter (where phase = 'QaTesting')                 as qa_runs
    from runs
    group by ticket_id
),

-- The implement run that handed the ticket to QA the first time: what built the thing QA judged.
first_build as (
    select r.ticket_id, r.model, r.provider, r.effort, r.skill_version, r.prompt_version, r.lines_changed
    from runs r join qa using (ticket_id)
    where r.phase = 'Implementing' and r.started_at <= qa.first_qa_at
    qualify row_number() over (partition by r.ticket_id order by r.started_at desc) = 1
)

select
    t.ticket_id,
    t.project_key,
    t.track,
    t.title,
    t.phase,
    t.created_at,
    qa.first_qa_at,
    qa.first_qa_at is not null                                   as qa_attempted,
    coalesce(qa.qa_failures, 0)                                  as qa_failures,
    qa.first_qa_at is not null and coalesce(qa.qa_failures, 0) = 0 as first_pass_qa,
    coalesce(qa.backward_moves, 0)                               as backward_moves,
    qa.done_at,
    qa.done_at is not null                                       as reached_done,
    datediff('second', t.created_at, qa.done_at) / 3600.0        as hours_to_done,
    coalesce(rt.runs, 0)                                         as runs,
    coalesce(rt.minutes, 0)                                      as minutes,
    coalesce(rt.rework_minutes, 0)                               as rework_minutes,
    coalesce(rt.tokens, 0)                                       as tokens,
    coalesce(rt.rework_tokens, 0)                                as rework_tokens,
    coalesce(rt.implement_runs, 0)                               as implement_runs,
    coalesce(rt.qa_runs, 0)                                      as qa_runs,
    fb.model                                                     as build_model,
    fb.provider                                                  as build_provider,
    fb.effort                                                    as build_effort,
    fb.skill_version                                             as build_skill_version,
    fb.prompt_version                                            as build_prompt_version
    -- Room for escaped defects (docs/analytics.md): a defect ticket linked to the ticket and run
    -- that shipped it lands here as a count, beside first_pass_qa.
from t
left join qa using (ticket_id)
left join run_totals rt using (ticket_id)
left join first_build fb using (ticket_id)
