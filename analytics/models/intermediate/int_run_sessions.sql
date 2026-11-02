-- Which Claude session each run was.
--
-- A run that recorded its session id is joined on it. One recorded before that is matched by what
-- its session says about itself: a worker prompt names its ticket ("**Ticket:** #123"), and its
-- working directory is the ticket's worktree - either, with the session's lines overlapping the
-- run's time, is a match. The closest by overlap wins, so a run is matched at most once.
--
-- A session can serve more than one run (a resumed session continues the same transcript), so
-- every per-run figure is read from the session's lines inside the run's own window.
with runs as (
    select r.*, t.worktree_path
    from {{ ref('stg_board__cycle_runs') }} r
    left join {{ ref('stg_board__tickets') }} t using (ticket_id)
),
sessions as (
    select * from {{ ref('int_transcript_sessions') }}
),

-- A ticket whose worktree path was cleared still had one: its project's root plus ticket-<id>.
roots as (
    select project_key, mode(regexp_replace({{ norm_path('worktree_path') }}, '/[^/]+$', '')) as root
    from {{ ref('stg_board__tickets') }}
    where worktree_path is not null
    group by project_key
),
expected as (
    select r.run_id,
           coalesce({{ norm_path('r.worktree_path') }}, ro.root || '/ticket-' || r.ticket_id) as cwd
    from runs r left join roots ro using (project_key)
),

recorded as (
    select r.run_id, s.session_id, 'session_id' as match_method, 0 as priority, 0.0 as distance_s
    from runs r join sessions s on s.session_id = r.session_id
),

-- Two traps, both met in real data. A run on a worker host leaves its transcript on that machine,
-- so the nearest local session is the ticket's previous phase - a prompt match must agree on the
-- phase, not just the ticket. And short sessions with no worker prompt run in the same worktree
-- just after a run ends - a match on the directory alone must start when the run starts.
fuzzy as (
    select
        r.run_id, s.session_id,
        case when s.prompt_ticket_id is not null then 'prompt' else 'cwd' end as match_method,
        case when s.prompt_ticket_id is not null then 1 else 2 end as priority,
        abs(datediff('millisecond', r.started_at, s.first_at)) / 1000.0 as distance_s
    from runs r
    join expected e using (run_id)
    join sessions s
      on s.first_at <= r.ended_at + interval 1 minute
     and s.last_at  >= r.started_at - interval 1 minute
     and (
            (s.prompt_ticket_id = r.ticket_id and coalesce(s.prompt_phase, r.phase) = r.phase)
         or (s.prompt_ticket_id is null and s.cwd = e.cwd
             and s.first_at between r.started_at - interval 1 minute and r.started_at + interval 3 minute)
     )
    where r.session_id is null
),

candidates as (
    select * from recorded
    union all
    select * from fuzzy
)

select run_id, session_id, match_method
from candidates
qualify row_number() over (partition by run_id order by priority, distance_s, session_id) = 1
