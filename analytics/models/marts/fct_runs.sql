-- One worker run, for drill-down and for any question the aggregate marts do not answer yet.
--
-- machine_runs: how many runs that machine had going when this one started, this one included - the
-- load it ran under, so a machine that looks fast can be told from one that was merely quiet. A
-- floor before AddRunHost: runs that could not be placed are not counted on either machine.
with r as (
    select * from {{ ref('int_runs') }}
),

loaded as (
    select
        r.*,
        case when r.machine = 'unknown' then null else 1 + (
            select count(*) from r o
            where o.machine = r.machine and o.run_id <> r.run_id
              and o.started_at <= r.started_at and o.ended_at > r.started_at
        ) end as machine_runs
    from r
)

select
    r.*,
    -- The machine and its load together, as the page splits by them: `@board / 2-3 running`.
    case when r.machine_runs is null then 'unknown'
         else r.machine || ' / ' || case when r.machine_runs <= 1 then '1'
                                         when r.machine_runs <= 3 then '2-3'
                                         when r.machine_runs <= 6 then '4-6'
                                         else '7+' end || ' running'
    end as machine_load,
    t.title as ticket_title
from loaded r
left join {{ ref('stg_board__tickets') }} t using (ticket_id)
