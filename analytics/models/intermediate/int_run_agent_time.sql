-- How a run's agent spent its session: the span of its lines, and how much of that span some tool
-- was running.
--
-- Tool time is the union of the main thread's call intervals, not their sum: calls made in
-- parallel overlap, and summing them would claim more tool time than there was wall clock. The
-- remainder of the span is the model's - thinking and writing between calls.
with runs as (
    select r.run_id, r.started_at, r.ended_at, m.session_id
    from {{ ref('stg_board__cycle_runs') }} r
    join {{ ref('int_run_sessions') }} m using (run_id)
),

span as (
    select r.run_id,
           datediff('millisecond', min(l.ts), max(l.ts)) / 1000.0 as session_span_s
    from runs r
    join {{ ref('stg_transcripts__lines') }} l
      on l.session_id = r.session_id
     and l.ts >= r.started_at - interval 1 minute
     and l.ts <= r.ended_at + interval 1 minute
     and not l.is_sidechain
    group by r.run_id
),

calls as (
    select run_id, called_at as s, returned_at as e
    from {{ ref('int_run_tool_calls') }}
    where not is_sidechain and returned_at is not null and returned_at >= called_at
),

-- Gaps and islands: a call starts a new island when it begins after every earlier call has ended.
marked as (
    select *,
           case when s > max(e) over (partition by run_id order by s, e
                                      rows between unbounded preceding and 1 preceding)
                then 1 else 0 end as starts_island
    from calls
),
islands as (
    select *, sum(starts_island) over (partition by run_id order by s, e rows unbounded preceding) as island
    from marked
),
busy as (
    select run_id, sum(datediff('millisecond', s0, e0)) / 1000.0 as tool_busy_s
    from (select run_id, island, min(s) as s0, max(e) as e0 from islands group by run_id, island)
    group by run_id
),

counts as (
    select run_id, count(*) as tool_calls, count(*) filter (where is_error) as tool_errors,
           sum(seconds) as tool_call_s
    from {{ ref('int_run_tool_calls') }}
    group by run_id
)

select
    r.run_id,
    sp.session_span_s,
    coalesce(b.tool_busy_s, 0)  as tool_busy_s,
    coalesce(c.tool_calls, 0)   as tool_calls,
    coalesce(c.tool_errors, 0)  as tool_errors,
    coalesce(c.tool_call_s, 0)  as tool_call_s
from runs r
left join span sp using (run_id)
left join busy b using (run_id)
left join counts c using (run_id)
