-- Each tool call a run made: its matched session's calls, inside the run's own window.
with runs as (
    select r.run_id, r.started_at, r.ended_at, m.session_id
    from {{ ref('stg_board__cycle_runs') }} r
    join {{ ref('int_run_sessions') }} m using (run_id)
)

select
    r.run_id,
    row_number() over (partition by r.run_id order by c.called_at, c.tool_use_id) as seq,
    datediff('millisecond', r.started_at, c.called_at) / 1000.0 as offset_s,
    c.*
from runs r
join {{ ref('int_tool_calls') }} c
  on c.session_id = r.session_id
 and c.called_at >= r.started_at - interval 1 minute
 and c.called_at <= r.ended_at + interval 1 minute
