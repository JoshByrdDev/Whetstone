-- Why each run happened and which time round it was, for every run.
--
-- A run recorded since run tracking carries both, worked out by the board (RunEntryReasons.Of).
-- For a run recorded before, they are worked out here the same way, from the move that put the
-- ticket in the run's queue - the CASE below is that method once more, kept in step with it and
-- with the AddRunTracking migration's backfill. On a database the migration has run on, every row
-- is 'recorded' and this only reads.
with runs as (
    select * from {{ ref('stg_board__cycle_runs') }}
),
transitions as (
    select * from {{ ref('stg_board__phase_transitions') }}
),

-- The last move before the run that was not into the run's own phase: what queued it.
queued_by as (
    select r.run_id, t.from_phase, t.reason, t.backward
    from runs r
    join transitions t
      on t.ticket_id = r.ticket_id and t.moved_at <= r.started_at and t.to_phase <> r.phase
    qualify row_number() over (partition by r.run_id order by t.moved_at desc, t.transition_id desc) = 1
),

derived as (
    select
        r.run_id,
        case
            when r.resumed then 'resumed'
            when q.from_phase is null then null
            when q.from_phase = 'Blocked' then 'unblocked'
            when q.reason ilike '%conflicts with%' then 'conflict'
            when not q.backward then 'forward'
            when q.from_phase = 'QaTesting' then 'qa-failed'
            when q.from_phase = 'Refining' then 'refine-sent-back'
            else 'review-changes'
        end as entry_reason,
        row_number() over (partition by r.ticket_id, r.skill order by r.started_at, r.run_id) as pass
    from runs r
    left join queued_by q using (run_id)
)

select
    r.run_id,
    coalesce(r.entry_reason, d.entry_reason, 'unknown') as entry_reason,
    case when r.entry_reason is not null then 'recorded'
         when d.entry_reason is not null then 'derived'
         else 'unknown' end                              as entry_reason_source,
    coalesce(r.pass, d.pass)                             as pass
from runs r
join derived d using (run_id)
