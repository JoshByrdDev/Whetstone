-- One ticket enrolled in an experiment, measured as the board measures it (ExperimentService
-- .MeasureAsync, docs/bugs-and-experiments-plan.md 2.5): every run of every skill from its
-- assignment onward, because a terser speculate that costs implement an extra pass has cost that
-- pass. Kept in step with the board's code so the two can be checked against each other - which is
-- why it reads the runs as recorded rather than int_runs' worked-out entry reasons.
--
-- One thing the board does not do: `left_arm_at`, the first run of the experiment's skill that
-- named the arm but ran as configured. A ticket that left its arm is excluded from the arm's
-- applied-only cost in mart_experiment_arms.
with a as (
    select a.*, e.project_key, e.wait_days
    from {{ ref('stg_board__experiment_assignments') }} a
    join {{ ref('stg_board__experiments') }} e using (experiment_id)
),
tickets as (
    select * from {{ ref('stg_board__tickets') }}
),
runs as (
    select * from {{ ref('stg_board__cycle_runs') }}
),
moves as (
    select * from {{ ref('stg_board__phase_transitions') }}
),
escaped as (
    select distinct source_ticket_id as ticket_id
    from {{ ref('stg_board__bug_links') }}
    where status = 'Confirmed' and escaped and source_ticket_id is not null
),

run_totals as (
    select
        a.experiment_id,
        a.ticket_id,
        count(r.run_id)                                                              as runs,
        coalesce(sum(r.duration_s), 0) / 60.0                                        as worker_minutes,
        coalesce(sum(coalesce(r.input_tokens, 0) + coalesce(r.output_tokens, 0)
                     + coalesce(r.cache_creation_tokens, 0)), 0)                     as fresh_tokens,
        coalesce(sum(coalesce(r.cache_read_tokens, 0)), 0)                           as cache_read_tokens,
        -- A conflict is caused by other tickets landing, not by the arm, so it is not rework here.
        count(r.run_id) filter (where r.entry_reason in ('qa-failed', 'refine-sent-back', 'review-changes'))
                                                                                     as rework_runs,
        count(r.run_id) filter (where r.experiment_id = a.experiment_id and r.experiment_applied)
                                                                                     as runs_applied,
        min(r.started_at) filter (where r.experiment_id = a.experiment_id and not r.experiment_applied)
                                                                                     as left_arm_at
    from a
    left join runs r
      on r.ticket_id = a.ticket_id and r.started_at >= a.assigned_at - interval 1 minute
    group by a.experiment_id, a.ticket_id
),

move_totals as (
    select
        a.experiment_id,
        a.ticket_id,
        bool_or(m.from_phase = 'QaTesting' and m.backward and m.moved_at >= a.assigned_at)    as had_qa_failure,
        max(m.moved_at) filter (where m.to_phase = 'Done' and m.moved_at >= a.assigned_at)    as done_after,
        max(m.moved_at) filter (where m.to_phase = 'Done')                                    as done_ever
    from a
    left join moves m on m.ticket_id = a.ticket_id
    group by a.experiment_id, a.ticket_id
)

select
    a.experiment_id,
    a.ticket_id,
    t.title,
    a.project_key,
    coalesce(t.track, 'Delivery')                                       as track,
    a.arm_key,
    a.ordinal,
    a.assigned_at,
    t.phase,
    -- Done only while the ticket is Done now: a reopened ticket has no outcome yet.
    case when t.phase = 'Done' then coalesce(mt.done_after, mt.done_ever) end as done_at,
    rt.runs,
    rt.worker_minutes,
    rt.fresh_tokens,
    rt.cache_read_tokens,
    coalesce(mt.had_qa_failure, false)                                  as had_qa_failure,
    rt.rework_runs > 0                                                  as had_rework,
    rt.rework_runs,
    e.ticket_id is not null                                             as escaped_bug,
    -- An escaped bug is known once the ticket has been Done for the wait, or as soon as one is
    -- confirmed: a bad outcome cannot un-happen.
    e.ticket_id is not null
      or (t.phase = 'Done' and coalesce(mt.done_after, mt.done_ever) + to_days(a.wait_days) <= {{ as_of() }})
                                                                        as escape_known,
    rt.runs_applied,
    rt.left_arm_at,
    rt.left_arm_at is not null                                          as left_arm
from a
-- A ticket since deleted is no longer anyone's evidence.
join tickets t on t.ticket_id = a.ticket_id
join run_totals rt on rt.experiment_id = a.experiment_id and rt.ticket_id = a.ticket_id
join move_totals mt on mt.experiment_id = a.experiment_id and mt.ticket_id = a.ticket_id
left join escaped e on e.ticket_id = a.ticket_id
